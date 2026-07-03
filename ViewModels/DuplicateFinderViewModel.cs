using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 重复文件查找 ViewModel
/// 支持扫描范围选择、匹配方式（SHA256/MD5）、文件类型过滤
/// 重复组展示（每组可展开查看文件），自动选中保留最早文件
/// 受保护路径文件安全标记
/// </summary>
public partial class DuplicateFinderViewModel : ViewModelBase
{
    private readonly IDuplicateFinderService _service;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    /// <summary>可扫描的路径列表</summary>
    public ObservableCollection<ScanPathModel> ScanPaths { get; } = new();

    /// <summary>重复文件组列表</summary>
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = new();

    /// <summary>哈希算法选项</summary>
    public string[] HashModeOptions { get; } = { "SHA256（推荐）", "MD5（更快但碰撞风险稍高）" };

    /// <summary>当前哈希算法索引（0=SHA256, 1=MD5）</summary>
    [ObservableProperty]
    private int _selectedHashModeIndex;

    /// <summary>文件类型过滤（如 .jpg;*.png），用分号分隔</summary>
    [ObservableProperty]
    private string _fileFilter = "";

    /// <summary>是否正在扫描</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>扫描到的重复组总数</summary>
    [ObservableProperty]
    private int _totalGroupCount;

    /// <summary>扫描到的重复文件总数</summary>
    [ObservableProperty]
    private int _totalFileCount;

    /// <summary>总浪费空间（格式化）</summary>
    [ObservableProperty]
    private string _totalWastedSpace = "0 B";

    /// <summary>
    /// 构造函数，通过 DI 注入服务和日志
    /// </summary>
    public DuplicateFinderViewModel(IDuplicateFinderService service, ILogService log)
    {
        _service = service;
        _log = log;
        LoadDefaultPaths();
        SelectedHashModeIndex = 0; // 默认 SHA256
    }

    /// <summary>
    /// 加载默认扫描路径（所有固定磁盘根目录）
    /// </summary>
    private void LoadDefaultPaths()
    {
        ScanPaths.Clear();
        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                try
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    {
                        ScanPaths.Add(new ScanPathModel
                        {
                            Path = drive.Name,
                            DisplayName = $"{drive.Name} ({drive.VolumeLabel ?? "本地磁盘"})",
                            IsSelected = false
                        });
                    }
                }
                catch { }
            }

            // 额外添加用户目录作为可选路径
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ScanPaths.Add(new ScanPathModel
            {
                Path = userProfile,
                DisplayName = $"用户目录 ({userProfile})",
                IsSelected = false
            });

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            ScanPaths.Add(new ScanPathModel
            {
                Path = desktop,
                DisplayName = $"桌面 ({desktop})",
                IsSelected = false
            });

            // 默认全选
            foreach (var p in ScanPaths)
                p.IsSelected = true;

            _log.LogInfo($"加载了 {ScanPaths.Count} 个扫描路径");
        }
        catch (Exception ex)
        {
            _log.LogError("加载扫描路径失败", ex);
        }
    }

    /// <summary>
    /// 选择扫描路径
    /// 目前先提供固定磁盘多选的简单入口，后续再扩展为完整文件夹树选择器。
    /// </summary>
    [RelayCommand]
    private void ChooseScanPaths()
    {
        try
        {
            var drives = ScanPaths.Where(p => p.Path.EndsWith(":\\", StringComparison.OrdinalIgnoreCase)).ToList();
            if (drives.Count == 0)
            {
                StatusMessage = "未找到可用的本地固定磁盘";
                return;
            }

            var selected = string.Join("、", drives.Where(d => d.IsSelected).Select(d => d.DisplayName));
            var message = "请先在此功能中使用默认磁盘列表进行勾选/取消勾选。\n\n" +
                          "当前可选磁盘：\n" + string.Join("\n", drives.Select(d => $"- {d.DisplayName}")) +
                          "\n\n当前已选：" + (string.IsNullOrWhiteSpace(selected) ? "无" : selected);
            MessageBox.Show(message, "选择扫描路径", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusMessage = "已显示可选扫描路径，请勾选需要扫描的磁盘后再开始扫描";
        }
        catch (Exception ex)
        {
            StatusMessage = $"选择扫描路径失败: {ex.Message}";
            _log.LogError("选择扫描路径失败", ex);
        }
    }
    /// <summary>
    /// 开始扫描重复文件命令
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        var selectedPaths = ScanPaths.Where(p => p.IsSelected).Select(p => p.Path).ToList();
        if (!selectedPaths.Any())
        {
            StatusMessage = "请先选择扫描范围";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsScanning = true;
        IsBusy = true;
        IsProgressIndeterminate = true;
        DuplicateGroups.Clear();
        StatusMessage = "正在扫描重复文件...";

        try
        {
            var progress = new Progress<string>(msg =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusMessage = msg;
                });
            });

            var groups = await _service.FindDuplicatesAsync(
                selectedPaths,
                FileFilter,
                SelectedHashModeIndex == 1, // true = MD5
                progress,
                token);

            // 更新 UI
            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var group in groups)
                {
                    // 自动标记最早的文件为保留（不删除）
                    if (group.Files.Any())
                    {
                        var earliest = group.Files.OrderBy(f => f.LastWriteTime).First();
                        earliest.IsMarkedToKeep = true;
                    }

                    DuplicateGroups.Add(group);
                }

                TotalGroupCount = DuplicateGroups.Count;
                TotalFileCount = DuplicateGroups.Sum(g => g.Files.Count);
                TotalWastedSpace = DuplicateGroup.FormatSize(
                    DuplicateGroups.Sum(g => g.WastedSpace));

                IsProgressIndeterminate = false;
                StatusMessage = $"找到 {TotalGroupCount} 组重复文件，" +
                                $"共 {TotalFileCount} 个文件，" +
                                $"浪费空间 {TotalWastedSpace}";
            });

            _log.LogInfo($"重复文件扫描完成: {TotalGroupCount} 组, {TotalFileCount} 个文件");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
            IsProgressIndeterminate = false;
            _log.LogInfo("重复文件扫描被取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
            IsProgressIndeterminate = false;
            _log.LogError("重复文件扫描失败", ex);
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 取消扫描命令
    /// </summary>
    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
        IsScanning = false;
        IsBusy = false;
        IsProgressIndeterminate = false;
        StatusMessage = "扫描已取消";
    }

    /// <summary>
    /// 删除选中（非保留的）重复文件
    /// 保留被标记为 "keep" 的文件
    /// 受保护路径文件不删除
    /// </summary>
    [RelayCommand]
    private async Task DeleteDuplicatesAsync()
    {
        var filesToDelete = DuplicateGroups
            .SelectMany(g => g.Files)
            .Where(f => !f.IsMarkedToKeep && !f.IsProtected)
            .ToList();

        if (!filesToDelete.Any())
        {
            StatusMessage = "没有可删除的重复文件";
            return;
        }

        var wastedDisplay = DuplicateGroup.FormatSize(filesToDelete.Sum(f => f.Size));
        if (!Helpers.ConfirmationHelper.RequestL1(
                $"确认删除 {filesToDelete.Count} 个重复文件？\n\n预计释放空间: {wastedDisplay}"))
            return;

        IsBusy = true;
        StatusMessage = "正在删除重复文件...";

        int deleted = 0;
        int failed = 0;

        await Task.Run(() =>
        {
            foreach (var file in filesToDelete)
            {
                try
                {
                    if (Helpers.ProtectedPaths.IsProtectedPath(file.FullPath))
                        continue;

                    File.Delete(file.FullPath);
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }
        });

        // 移除已成功删除的文件所在的组
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var toRemoveGroups = new List<DuplicateGroup>();
            foreach (var group in DuplicateGroups)
            {
                var remaining = group.Files
                    .Where(f => !filesToDelete.Contains(f) || File.Exists(f.FullPath))
                    .ToList();
                if (remaining.Count < 2)
                    toRemoveGroups.Add(group);
                else
                    group.Files = remaining;
            }

            foreach (var g in toRemoveGroups)
                DuplicateGroups.Remove(g);

            TotalGroupCount = DuplicateGroups.Count;
            TotalFileCount = DuplicateGroups.Sum(g => g.Files.Count);
            TotalWastedSpace = DuplicateGroup.FormatSize(
                DuplicateGroups.Sum(g => g.WastedSpace));
        });

        IsBusy = false;
        StatusMessage = $"删除完成: {deleted} 成功, {failed} 失败";
        _log.LogOperation("duplicate", "delete", $"删除 {deleted} 个重复文件, 失败 {failed}");
    }

    /// <summary>
    /// 在文件资源管理器中打开文件
    /// </summary>
    [RelayCommand]
    private void OpenFileLocation(DuplicateFileInfo? file)
    {
        if (file == null) return;
        try
        {
            var dir = Path.GetDirectoryName(file.FullPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file.FullPath}\"");
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"打开文件位置失败: {file.FullPath}", ex);
        }
    }
}

/// <summary>
/// 扫描路径模型
/// </summary>
public partial class ScanPathModel : ObservableObject
{
    /// <summary>路径</summary>
    [ObservableProperty]
    private string _path = "";

    /// <summary>显示名称</summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>是否选中</summary>
    [ObservableProperty]
    private bool _isSelected;
}
