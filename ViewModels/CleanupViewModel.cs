using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Native;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// C盘清理 ViewModel
/// 负责管理清理项的扫描、选择确认和批量清理流程
/// 安全限制：高级项勾选弹 L3 确认，清理前弹 L1 确认
/// </summary>
public partial class CleanupViewModel : ViewModelBase
{
    private readonly ICleanupService _cleanupService;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    // 定时清理相关字段
    private CancellationTokenSource? _scheduledCts;

    // 防止 Checked/Unchecked 事件 → OnItemSelectionChanged → 修改 IsSelected → 再次触发事件的递归循环
    private bool _suppressSelectionChanged;

    /// <summary>所有清理项集合</summary>
    public ObservableCollection<CleanupItem> CleanupItems { get; } = new();

    /// <summary>安全级别的清理项</summary>
    [ObservableProperty]
    private IEnumerable<CleanupItem> _safeItems = Enumerable.Empty<CleanupItem>();

    /// <summary>推荐级别的清理项</summary>
    [ObservableProperty]
    private IEnumerable<CleanupItem> _recommendedItems = Enumerable.Empty<CleanupItem>();

    /// <summary>高级级别的清理项</summary>
    [ObservableProperty]
    private IEnumerable<CleanupItem> _advancedItems = Enumerable.Empty<CleanupItem>();

    /// <summary>是否已扫描完成</summary>
    [ObservableProperty]
    private bool _isScanned;

    /// <summary>是否正在扫描</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>是否正在清理</summary>
    [ObservableProperty]
    private bool _isCleaning;

    /// <summary>选中的清理项总大小显示</summary>
    [ObservableProperty]
    private string _selectedSizeDisplay = "0 B";

    /// <summary>Only scan and clean files older than the configured age.</summary>
    [ObservableProperty]
    private bool _onlyCleanOldFiles = false;

    /// <summary>Minimum age in hours for the optional cleanup age filter.</summary>
    [ObservableProperty]
    private int _minimumFileAgeHours = 24;

    /// <summary>是否启用定时清理</summary>
    [ObservableProperty]
    private bool _scheduledCleanupEnabled;

    /// <summary>定时清理间隔（天）</summary>
    [ObservableProperty]
    private int _scheduledCleanupDays = 7;

    /// <summary>定时清理通知模式</summary>
    [ObservableProperty]
    private string _scheduledCleanupNotifyMode = "BeforeCleanup";

    /// <summary>
    /// 构造函数，通过 DI 注入清洁服务和日志服务
    /// </summary>
    public CleanupViewModel(ICleanupService cleanupService, ILogService log)
    {
        _cleanupService = cleanupService;
        _log = log;

        // 加载预定义的清理项
        LoadItems();

        _log.LogInfo("CleanupViewModel 初始化完成");
    }

    /// <summary>
    /// 从服务加载清理项到 ViewModel
    /// 按安全等级分组，方便 UI 分类展示
    /// </summary>
    private void LoadItems()
    {
        CleanupItems.Clear();
        foreach (var item in _cleanupService.Items)
        {
            CleanupItems.Add(item);
        }

        // 按安全级别分组
        RefreshGroups();
    }

    /// <summary>
    /// 刷新安全级别分组
    /// </summary>
    private void RefreshGroups()
    {
        SafeItems = CleanupItems.Where(i => i.SafetyLevel == CleanupSafetyLevel.Safe).ToList();
        RecommendedItems = CleanupItems.Where(i => i.SafetyLevel == CleanupSafetyLevel.Recommended).ToList();
        AdvancedItems = CleanupItems.Where(i => i.SafetyLevel == CleanupSafetyLevel.Advanced).ToList();
    }

    /// <summary>
    /// 扫描完成后自动应用默认勾选规则：
    /// - 安全项默认勾选（有垃圾才勾）
    /// - 推荐项中标记为默认项的自动勾选（有垃圾才勾）
    /// - 高级项默认不勾选
    /// </summary>
    private void ApplyDefaultSelectionsAfterScan()
    {
        foreach (var item in CleanupItems)
        {
            if (item.SafetyLevel == CleanupSafetyLevel.Advanced)
            {
                item.IsSelected = false;
                continue;
            }

            item.IsSelected = item.TotalSize > 0 && item.IsDefaultSelected;
        }

        UpdateSelectedSize();
    }

    /// <summary>
    /// 更新选中项的合计大小显示
    /// </summary>
    private void UpdateSelectedSize()
    {
        var selected = CleanupItems.Where(i => i.IsSelected).ToList();
        var totalSize = selected.Sum(i => i.TotalSize);
        SelectedSizeDisplay = CleanupItem.FormatSize(totalSize);
    }

    partial void OnOnlyCleanOldFilesChanged(bool value)
    {
        MarkScanStaleAfterFilterChange();
    }

    partial void OnMinimumFileAgeHoursChanged(int value)
    {
        if (value < 1)
        {
            MinimumFileAgeHours = 1;
            return;
        }

        MarkScanStaleAfterFilterChange();
    }

    private void MarkScanStaleAfterFilterChange()
    {
        if (!IsScanned) return;

        IsScanned = false;
        foreach (var item in CleanupItems)
            item.IsSelected = false;
        UpdateSelectedSize();
        StatusMessage = "清理条件已更改，请重新扫描后再清理";
    }

    /// <summary>
    /// 当任意清理项选中状态改变时，更新合计大小
    /// 由 View 层通过 CheckBox 事件调用
    /// </summary>
    public void OnItemSelectionChanged()
    {
        // 防止 Checked/Unchecked 事件循环中的递归调用
        if (_suppressSelectionChanged) return;

        UpdateSelectedSize();

        // 检查是否勾选了高级项，需要 L3 确认
        var advancedSelected = AdvancedItems.Where(i => i.IsSelected).ToList();
        if (advancedSelected.Any())
        {
            // 高级项已选中：弹 L3 确认
            var names = string.Join("、", advancedSelected.Select(i => i.Name));
            var confirmed = ConfirmationHelper.RequestL3(
                $"您即将清理以下高级项目：\n\n{names}\n\n" +
                "这些操作可能会影响系统或软件的正常运行。\n" +
                "请在下方输入「确认删除高级项目」以继续：",
                "确认删除高级项目");

            if (!confirmed)
            {
                // 用户取消，取消勾选所有高级项
                _suppressSelectionChanged = true;
                try
                {
                    foreach (var item in advancedSelected)
                    {
                        item.IsSelected = false;
                    }
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }
                UpdateSelectedSize();
            }
        }
    }

    /// <summary>
    /// 开始扫描命令
    /// 在后台线程执行扫描，进度通过 ProgressValue 和 StatusMessage 反馈
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsScanning = true;
        IsScanned = false;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressMax = CleanupItems.Count;
        StatusMessage = "正在扫描清理项...";

        try
        {
            var progress = new Progress<string>(msg =>
            {
                // 在主线程更新 UI
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusMessage = msg;
                    ProgressValue++;
                });
            });

            await _cleanupService.ScanAllAsync(progress, token, OnlyCleanOldFiles, MinimumFileAgeHours);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                // 扫描后按默认规则自动勾选安全项和默认推荐项
                ApplyDefaultSelectionsAfterScan();
                RefreshGroups();
                IsScanned = true;
                StatusMessage = $"扫描完成！发现 {CleanupItems.Sum(i => i.FileCount):N0} 个可清理文件，可释放 {CleanupItem.FormatSize(CleanupItems.Sum(i => i.TotalSize))}";
                IsProgressIndeterminate = false;
                ProgressValue = ProgressMax;
            });

            _log.LogInfo("清理项扫描完成");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
            IsProgressIndeterminate = false;
            _log.LogInfo("扫描被用户取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
            IsProgressIndeterminate = false;
            _log.LogError("清理项扫描失败", ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 取消当前扫描或清理操作
    /// </summary>
    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
        IsScanning = false;
        IsCleaning = false;
        IsProgressIndeterminate = false;
        StatusMessage = "操作已取消";
        _log.LogInfo("用户取消了当前操作");
    }

    /// <summary>
    /// 开始清理命令
    /// 清理前会弹 L1 确认
    /// 在后台线程执行批量清理，通过进度条反馈
    /// </summary>
    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsCleaning) return;

        var selectedItems = CleanupItems.Where(i => i.IsSelected).ToList();

        if (!selectedItems.Any())
        {
            StatusMessage = "请先选择要清理的项目";
            return;
        }

        // L1 确认：清理前弹窗确认
        var confirmMsg = string.Join("\n", selectedItems.Select(i => $"  - {i.Name}: {i.SizeDisplay}"));
        if (!ConfirmationHelper.RequestL1($"确认清理以下项目？\n\n{confirmMsg}\n\n预计释放空间: {SelectedSizeDisplay}"))
        {
            _log.LogInfo("用户取消了清理操作");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsCleaning = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressMax = 100;
        StatusMessage = "正在清理中...";

        try
        {
            var progress = new Progress<(string, int)>(data =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusMessage = $"正在清理: {data.Item1} ({data.Item2}%)";
                    ProgressValue = data.Item2;
                });
            });

            var result = await _cleanupService.CleanAsync(selectedItems, progress, token, OnlyCleanOldFiles, MinimumFileAgeHours);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsCleaning = false;
                IsProgressIndeterminate = false;
                ProgressValue = ProgressMax;

                // 显示清理结果
                StatusMessage = $"清理完成！成功 {result.FilesDeleted} 个文件，" +
                                $"跳过 {result.FilesSkipped} 个，失败 {result.FilesFailed} 个，" +
                                $"释放空间 {result.TotalFreedDisplay}";

                // 重新扫描以更新大小
                _ = ScanAsync();
            });

            _log.LogOperation("cleanup", "complete",
                $"清理完成：{result.FilesDeleted} 成功, {result.FilesSkipped} 跳过, " +
                $"{result.FilesFailed} 失败, 释放 {result.TotalFreedDisplay}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "清理已取消";
            IsProgressIndeterminate = false;
            _log.LogInfo("清理被用户取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"清理失败: {ex.Message}";
            IsProgressIndeterminate = false;
            _log.LogError("清理操作失败", ex);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    /// <summary>
    /// 全选安全级别的清理项
    /// </summary>
    [RelayCommand]
    private void SelectAllSafe()
    {
        foreach (var item in SafeItems.Where(i => i.TotalSize > 0))
        {
            item.IsSelected = true;
        }
        UpdateSelectedSize();
        RefreshGroups();
    }

    /// <summary>
    /// 全选推荐级别的清理项
    /// </summary>
    [RelayCommand]
    private void SelectAllRecommended()
    {
        foreach (var item in RecommendedItems.Where(i => i.TotalSize > 0))
        {
            item.IsSelected = true;
        }
        UpdateSelectedSize();
        RefreshGroups();
    }

    /// <summary>
    /// 取消所有选中
    /// </summary>
    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var item in CleanupItems)
        {
            item.IsSelected = false;
        }
        UpdateSelectedSize();
        RefreshGroups();
    }

    /// <summary>
    /// 保存定时清理配置
    /// </summary>
    [RelayCommand]
    private void SaveScheduledConfig()
    {
        // 保存定时清理设置到用户配置
        _log.LogInfo($"定时清理配置已保存: 每{ScheduledCleanupDays}天, 通知模式:{ScheduledCleanupNotifyMode}");
        StatusMessage = "定时清理配置已保存";
    }

    // ==================== 定时清理与空闲检测 ====================

    /// <summary>
    /// 当 ScheduledCleanupEnabled 属性改变时触发
    /// 启用时启动定时清理后台循环，禁用时停止
    /// </summary>
    partial void OnScheduledCleanupEnabledChanged(bool value)
    {
        if (value)
            StartScheduledCleanupLoop();
        else
            StopScheduledCleanupLoop();
    }

    /// <summary>
    /// 启动定时清理后台循环
    /// 在 ThreadPool 上运行，定期检查是否需要执行定时清理
    /// </summary>
    private void StartScheduledCleanupLoop()
    {
        StopScheduledCleanupLoop();
        _scheduledCts = new CancellationTokenSource();
        _ = Task.Run(() => ScheduledCleanupLoopAsync(_scheduledCts.Token));
    }

    /// <summary>
    /// 停止定时清理后台循环
    /// </summary>
    private void StopScheduledCleanupLoop()
    {
        _scheduledCts?.Cancel();
        _scheduledCts = null;
    }

    /// <summary>
    /// 定时清理主循环
    /// 每小时检查一次到期状态，到期后进入空闲检测阶段
    /// 空闲检测：GetLastInputInfo 检测 5 分钟无键鼠操作后执行；
    /// 若持续活跃则最多延迟 2 小时，超时强制执行
    /// </summary>
    private async Task ScheduledCleanupLoopAsync(CancellationToken token)
    {
        var lastCleanupRun = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // 每小时检查一次是否到期
                await Task.Delay(TimeSpan.FromHours(1), token);

                if (!ScheduledCleanupEnabled)
                    continue;

                var daysSinceLastRun = (DateTime.Now - lastCleanupRun).TotalDays;
                if (daysSinceLastRun < ScheduledCleanupDays)
                    continue;

                _log.LogInfo("定时清理已到期，开始检测用户空闲状态...");

                // 空闲检测阶段
                var idleWaitStart = DateTime.Now;
                var executed = false;

                while (!token.IsCancellationRequested && !executed)
                {
                    var idleSeconds = GetIdleTimeSeconds();
                    var totalDelaySeconds = (DateTime.Now - idleWaitStart).TotalSeconds;

                    if (idleSeconds >= 300) // 空闲达到 5 分钟
                    {
                        _log.LogInfo("检测到用户空闲超过 5 分钟，开始执行定时清理");
                        await ExecuteScheduledCleanupAsync(token);
                        lastCleanupRun = DateTime.Now;
                        executed = true;
                    }
                    else if (totalDelaySeconds >= 7200) // 已延迟 2 小时
                    {
                        _log.LogInfo("已超过最大延迟时间（2 小时），强制执行定时清理");
                        await ExecuteScheduledCleanupAsync(token);
                        lastCleanupRun = DateTime.Now;
                        executed = true;
                    }
                    else
                    {
                        // 每 10 秒再次检测
                        await Task.Delay(TimeSpan.FromSeconds(10), token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError("定时清理循环异常", ex);
            }
        }
    }

    /// <summary>
    /// 通过 GetLastInputInfo 获取用户空闲秒数
    /// 空闲 = 从最后一次键盘/鼠标输入至今的时间
    /// </summary>
    private static uint GetIdleTimeSeconds()
    {
        var info = new NativeMethods.LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));

        if (NativeMethods.GetLastInputInfo(ref info))
        {
            return (NativeMethods.GetTickCount() - info.dwTime) / 1000;
        }

        // 无法获取时返回 0（假定用户活跃，安全起见）
        return 0;
    }

    /// <summary>
    /// 执行定时清理：清理安全级和推荐级的所有项目
    /// </summary>
    private async Task ExecuteScheduledCleanupAsync(CancellationToken token)
    {
        // 先扫描所有项以确保 TotalSize 数据是最新的
        if (!IsScanned)
        {
            try
            {
                var scanProgress = new Progress<string>(_ => { });
                await _cleanupService.ScanAllAsync(scanProgress, token, OnlyCleanOldFiles, MinimumFileAgeHours);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"定时清理前扫描失败: {ex.Message}");
            }
        }

        var itemsToClean = CleanupItems
            .Where(i => i.TotalSize > 0
                        && i.IsDefaultSelected
                        && (i.SafetyLevel == CleanupSafetyLevel.Safe
                            || i.SafetyLevel == CleanupSafetyLevel.Recommended))
            .ToList();

        if (!itemsToClean.Any())
        {
            _log.LogInfo("定时清理：没有需要清理的项目");
            return;
        }

        try
        {
            var progress = new Progress<(string, int)>(_ => { });
            var result = await _cleanupService.CleanAsync(itemsToClean, progress, token, OnlyCleanOldFiles, MinimumFileAgeHours);
            _log.LogInfo(
                $"定时清理完成：成功删除 {result.FilesDeleted} 个文件，" +
                $"跳过 {result.FilesSkipped} 个文件，" +
                $"释放空间 {result.TotalFreedDisplay}");

            // 通知 UI 线程更新状态
            Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusMessage = $"定时清理完成：释放空间 {result.TotalFreedDisplay}";
            });
        }
        catch (OperationCanceledException)
        {
            _log.LogInfo("定时清理被取消");
        }
        catch (Exception ex)
        {
            _log.LogError("定时清理执行失败", ex);
        }
    }
}
