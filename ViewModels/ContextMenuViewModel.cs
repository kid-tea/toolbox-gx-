using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 右键菜单管理 ViewModel
/// 功能：扫描9个Shell Extension 注册位置的右键菜单项、启用/禁用、删除、搜索过滤
/// 安全提醒：L1 + 今天不提示/更新前不提示选项
/// 操作后：自动重启 Explorer 或提示手动重启
/// 系统项检测：Approved 列表中的项 → L2 二次确认
/// </summary>
public partial class ContextMenuViewModel : ViewModelBase
{
    private readonly IContextMenuService _contextMenu;
    private readonly ILogService _log;
    private readonly IConfigService _config;

    /// <summary>所有菜单项的完整列表（未过滤）</summary>
    private List<ContextMenuItem> _allItems = new();

    /// <summary>显示的菜单项列表（经过搜索过滤）</summary>
    [ObservableProperty]
    private ObservableCollection<ContextMenuItem> _items = new();

    /// <summary>搜索文本</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>位置过滤</summary>
    [ObservableProperty]
    private string _filterLocation = "全部";

    /// <summary>状态过滤</summary>
    [ObservableProperty]
    private string _filterStatus = "全部";

    /// <summary>统计信息</summary>
    [ObservableProperty]
    private string _statisticsText = "共 0 项";

    /// <summary>是否有待应用的更改（提示重启 Explorer）</summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    /// <summary>"今日不再提示"标记</summary>
    private DateTime? _noRemindToday;

    /// <summary>位置过滤选项列表</summary>
    public List<string> LocationFilters { get; } = new()
    {
        "全部", "所有文件", "文件夹", "文件夹背景", "驱动器", "所有对象",
        "快捷方式", "系统已批准", "图标叠加层"
    };

    /// <summary>状态过滤选项列表</summary>
    public List<string> StatusFilters { get; } = new()
    {
        "全部", "已启用", "已禁用"
    };

    /// <summary>
    /// 构造函数 — 通过 DI 注入服务
    /// </summary>
    public ContextMenuViewModel(
        IContextMenuService contextMenuService,
        ILogService logService,
        IConfigService configService)
    {
        _contextMenu = contextMenuService;
        _log = logService;
        _config = configService;
        StatusMessage = "就绪 — 点击「扫描菜单项」读取系统右键菜单";
    }

    // ==================== 扫描 ====================

    /// <summary>
    /// 扫描所有右键菜单项（9个Shell Extension 注册位置）
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        StatusMessage = "正在扫描右键菜单项...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                ProgressValue = p;
            });

            var items = await Task.Run(() =>
                _contextMenu.ScanAllContextMenuItems(progress, CancellationToken.None));

            _allItems = items;
            ApplyFilters();

            StatusMessage = $"扫描完成: 共 {items.Count} 项 (已扫描9个位置)";
            _log.LogInfo($"右键菜单扫描完成: {items.Count} 项");
        }
        catch (Exception ex)
        {
            _log.LogError("扫描右键菜单异常", ex);
            StatusMessage = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ==================== 启用/禁用 ====================

    /// <summary>
    /// 切换菜单项的启用/禁用状态
    /// 操作用前显示安全提醒（L1）+ 今天不提示/更新前不提示选项
    /// </summary>
    [RelayCommand]
    private void ToggleItem(ContextMenuItem? item)
    {
        if (item == null) return;

        try
        {
            if (!ShowSafetyReminder())
            {
                item.IsEnabled = !item.IsEnabled;
                return;
            }

            bool success;
            if (item.IsEnabled)
            {
                success = _contextMenu.EnableItem(item);
                if (success) StatusMessage = $"已启用: {item.Name}";
            }
            else
            {
                if (item.IsSystemItem)
                {
                    if (!ConfirmationHelper.RequestL2(
                        $"即将禁用系统右键菜单项:\n\n" +
                        $"名称: {item.Name}\n" +
                        $"CLSID: {item.Clsid}\n" +
                        $"位置: {item.LocationDisplay}\n" +
                        $"DLL: {item.DllPath}\n\n" +
                        $"禁用系统菜单项可能影响文件右键菜单功能，确定继续？"))
                    {
                        item.IsEnabled = true;
                        return;
                    }
                }
                else if (!ConfirmationHelper.RequestL1(
                    $"确定禁用右键菜单项 \"{item.Name}\" 吗？"))
                {
                    item.IsEnabled = true;
                    return;
                }

                success = _contextMenu.DisableItem(item);
                if (success) StatusMessage = $"已禁用: {item.Name}";
            }

            if (success)
            {
                HasPendingChanges = true;
                UpdateStatistics();
            }
            else
            {
                item.IsEnabled = !item.IsEnabled;
                StatusMessage = $"操作失败: {item.Name}";
            }
        }
        catch (Exception ex)
        {
            item.IsEnabled = !item.IsEnabled;
            _log.LogError($"切换右键菜单项状态失败: {item.Name}", ex);
            StatusMessage = $"操作失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 删除指定右键菜单项
    /// 系统项（Approved 列表）需要 L2 确认
    /// </summary>
    [RelayCommand]
    private void DeleteItem(ContextMenuItem? item)
    {
        if (item == null) return;

        try
        {
            if (!ShowSafetyReminder()) return;

            // 系统项需要 L2 确认
            if (item.IsSystemItem)
            {
                if (!ConfirmationHelper.RequestL2(
                    $"即将删除系统右键菜单项:\n\n" +
                    $"名称: {item.Name}\n" +
                    $"CLSID: {item.Clsid}\n" +
                    $"位置: {item.LocationDisplay}\n\n" +
                    $"此操作不可恢复，确定删除？"))
                    return;
            }
            else
            {
                if (!ConfirmationHelper.RequestL1(
                    $"确定删除右键菜单项 \"{item.Name}\" 吗？\n此操作不可恢复。"))
                    return;
            }

            bool success = _contextMenu.DeleteItem(item);
            if (success)
            {
                _allItems.Remove(item);
                Items.Remove(item);
                UpdateStatistics();
                HasPendingChanges = true;
                StatusMessage = $"已删除: {item.Name}";
            }
            else
            {
                StatusMessage = $"删除失败: {item.Name}";
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"删除右键菜单项失败: {item.Name}", ex);
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 显示安全提醒弹窗
    /// 支持"今天不再提示"功能
    /// </summary>
    private bool ShowSafetyReminder()
    {
        // 如果今天已勾选"不再提示"，跳过
        if (_noRemindToday.HasValue && _noRemindToday.Value.Date == DateTime.Today)
            return true;

        var result = MessageBox.Show(
            "修改右键菜单可能影响日常使用体验。\n\n" +
            "建议只禁用您确认不需要的菜单项。\n" +
            "如需恢复，可重新启用。\n\n" +
            "是否继续操作？\n\n" +
            "(点击「否」取消，点击「是」继续)",
            "安全提醒",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.No)
            return false;

        // 询问今天是否不再提示
        var dontAskResult = MessageBox.Show(
            "今天不再显示此提示？",
            "安全提醒",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (dontAskResult == MessageBoxResult.Yes)
            _noRemindToday = DateTime.Today;

        return true;
    }

    // ==================== 重启 Explorer ====================

    /// <summary>
    /// 重启 Explorer 以应用更改
    /// 提示用户保存工作
    /// </summary>
    [RelayCommand]
    private void RestartExplorer()
    {
        if (!HasPendingChanges) return;

        var result = MessageBox.Show(
            "需要重启 Windows 资源管理器以应用更改。\n\n" +
            "桌面和任务栏将暂时消失，预计 2-3 秒后恢复。\n" +
            "请确保已保存所有工作。\n\n" +
            "是否现在重启？\n(也可以选择「否」，手动重启)",
            "重启资源管理器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            _contextMenu.RestartExplorer();
            HasPendingChanges = false;
            StatusMessage = "资源管理器已重启，更改已生效";
        }
        else
        {
            StatusMessage = "更改已保存，请手动重启资源管理器或重新登录以生效";
        }
    }

    // ==================== 搜索和过滤 ====================

    /// <summary>
    /// 搜索文本变化时重新过滤
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// 位置过滤变化时重新过滤
    /// </summary>
    partial void OnFilterLocationChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// 状态过滤变化时重新过滤
    /// </summary>
    partial void OnFilterStatusChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// 应用所有过滤条件
    /// </summary>
    private void ApplyFilters()
    {
        var filtered = _allItems.AsEnumerable();

        // 搜索文本过滤
        if (!string.IsNullOrEmpty(SearchText))
        {
            var search = SearchText.ToLowerInvariant();
            filtered = filtered.Where(e =>
                e.Name.ToLowerInvariant().Contains(search) ||
                e.Clsid.ToLowerInvariant().Contains(search) ||
                e.DllPath.ToLowerInvariant().Contains(search) ||
                e.FileType.ToLowerInvariant().Contains(search));
        }

        // 位置过滤
        if (FilterLocation != "全部")
        {
            filtered = filtered.Where(e => e.LocationDisplay == FilterLocation);
        }

        // 状态过滤
        if (FilterStatus == "已启用")
        {
            filtered = filtered.Where(e => e.IsEnabled);
        }
        else if (FilterStatus == "已禁用")
        {
            filtered = filtered.Where(e => !e.IsEnabled);
        }

        Items = new ObservableCollection<ContextMenuItem>(filtered);
        UpdateStatistics();
    }

    /// <summary>
    /// 更新统计信息
    /// </summary>
    private void UpdateStatistics()
    {
        var total = _allItems.Count;
        var enabled = _allItems.Count(e => e.IsEnabled);
        var disabled = total - enabled;
        var systemItems = _allItems.Count(e => e.IsSystemItem);
        StatisticsText = $"共 {total} 项 | 已启用 {enabled} | 已禁用 {disabled} | 系统项 {systemItems}";
    }
}
