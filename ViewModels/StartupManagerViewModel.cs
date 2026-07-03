using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 开机启动项管理 ViewModel
/// 功能：扫描所有启动项、启用/禁用开关、删除（系统关键L2确认）、搜索过滤、导出CSV
/// 启动来源：注册表 Run 键（HKLM/HKCU/WOW64）、计划任务、启动文件夹
/// 影响程度自动判定：🟢低/🟡中/🔴高/🟠文件丢失
/// </summary>
public partial class StartupManagerViewModel : ViewModelBase
{
    private readonly IStartupService _startup;
    private readonly ILogService _log;
    private readonly IConfigService _config;

    /// <summary>所有启动项的完整列表（未过滤）</summary>
    private List<StartupEntry> _allEntries = new();

    /// <summary>显示的启动项列表（经过搜索过滤）</summary>
    [ObservableProperty]
    private ObservableCollection<StartupEntry> _entries = new();

    /// <summary>搜索文本</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>按来源过滤：All / RegistryHKLM / RegistryHKCU / TaskScheduler / StartupFolder</summary>
    [ObservableProperty]
    private string _filterSource = "全部";

    /// <summary>按影响程度过滤：All / Low / Medium / High / Unavailable / FileMissing</summary>
    [ObservableProperty]
    private string _filterImpact = "全部";

    /// <summary>按状态过滤：All / Enabled / Disabled</summary>
    [ObservableProperty]
    private string _filterStatus = "全部";

    /// <summary>统计信息显示</summary>
    [ObservableProperty]
    private string _statisticsText = "共 0 项";

    /// <summary>来源过滤选项列表</summary>
    public List<string> SourceFilters { get; } = new()
    {
        "全部", "HKLM注册表", "HKCU注册表", "HKLM注册表(32位)", "计划任务", "用户启动文件夹", "公用启动文件夹"
    };

    /// <summary>影响程度过滤选项列表</summary>
    public List<string> ImpactFilters { get; } = new()
    {
        "全部", "低", "中", "高", "不可用", "文件丢失"
    };

    /// <summary>状态过滤选项列表</summary>
    public List<string> StatusFilters { get; } = new()
    {
        "全部", "已启用", "已禁用"
    };

    /// <summary>
    /// 构造函数 — 通过 DI 注入服务
    /// </summary>
    public StartupManagerViewModel(
        IStartupService startupService,
        ILogService logService,
        IConfigService configService)
    {
        _startup = startupService;
        _log = logService;
        _config = configService;
        StatusMessage = "就绪 — 点击「扫描启动项」读取系统自启动项";
    }

    // ==================== 扫描 ====================

    /// <summary>
    /// 扫描所有启动项
    /// 后台执行，显示进度
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        StatusMessage = "正在扫描启动项...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                ProgressValue = p;
            });

            var items = await Task.Run(() =>
                _startup.ScanAllStartupItems(progress, CancellationToken.None));

            _allEntries = items;
            ApplyFilters();

            StatusMessage = $"扫描完成: 共 {items.Count} 项";
            _log.LogInfo($"启动项扫描完成: {items.Count}项");
        }
        catch (Exception ex)
        {
            _log.LogError("扫描启动项异常", ex);
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
    /// 切换启动项的启用/禁用状态
    /// </summary>
    [RelayCommand]
    private void ToggleItem(StartupEntry? entry)
    {
        if (entry == null) return;

        try
        {
            bool success;
            if (entry.IsEnabled)
            {
                success = _startup.EnableItem(entry);
                if (success) StatusMessage = $"已启用: {entry.Name}";
            }
            else
            {
                success = _startup.DisableItem(entry);
                if (success) StatusMessage = $"已禁用: {entry.Name}";
            }

            if (!success)
            {
                entry.IsEnabled = !entry.IsEnabled;
                StatusMessage = $"操作失败: {entry.Name}";
            }

            UpdateStatistics();
        }
        catch (Exception ex)
        {
            entry.IsEnabled = !entry.IsEnabled;
            _log.LogError($"切换启动项状态失败: {entry.Name}", ex);
            StatusMessage = $"操作失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 删除指定启动项
    /// 系统关键项（High影响 + 系统目录）使用 L2 二次确认
    /// </summary>
    [RelayCommand]
    private void DeleteItem(StartupEntry? entry)
    {
        if (entry == null) return;

        try
        {
            // 确认级别选择
            bool confirmed;
            if (entry.Impact == ImpactLevel.High)
            {
                // 高风险：L2 确认
                confirmed = ConfirmationHelper.RequestL2(
                    $"即将删除系统关键启动项:\n\n" +
                    $"名称: {entry.Name}\n" +
                    $"路径: {entry.FilePath}\n" +
                    $"来源: {entry.SourceDisplay}\n\n" +
                    $"此操作可能影响系统稳定性，确定继续？");
            }
            else
            {
                // 普通：L1 确认
                confirmed = ConfirmationHelper.RequestL1(
                    $"确定删除启动项 \"{entry.Name}\" 吗？\n路径: {entry.FilePath}");
            }

            if (!confirmed) return;

            bool success = _startup.DeleteItem(entry);
            if (success)
            {
                _allEntries.Remove(entry);
                Entries.Remove(entry);
                UpdateStatistics();
                StatusMessage = $"已删除: {entry.Name}";
            }
            else
            {
                StatusMessage = $"删除失败: {entry.Name}";
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"删除启动项失败: {entry.Name}", ex);
            StatusMessage = $"删除失败: {ex.Message}";
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
    /// 来源过滤变化时重新过滤
    /// </summary>
    partial void OnFilterSourceChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// 影响程度过滤变化时重新过滤
    /// </summary>
    partial void OnFilterImpactChanged(string value)
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
    /// 应用所有过滤条件（搜索文本 + 来源 + 影响程度 + 状态）
    /// </summary>
    private void ApplyFilters()
    {
        var filtered = _allEntries.AsEnumerable();

        // 搜索文本过滤
        if (!string.IsNullOrEmpty(SearchText))
        {
            var search = SearchText.ToLowerInvariant();
            filtered = filtered.Where(e =>
                e.Name.ToLowerInvariant().Contains(search) ||
                e.FilePath.ToLowerInvariant().Contains(search) ||
                e.Publisher.ToLowerInvariant().Contains(search));
        }

        // 来源过滤
        if (FilterSource != "全部")
        {
            filtered = filtered.Where(e => e.SourceDisplay == FilterSource);
        }

        // 影响程度过滤
        if (FilterImpact != "全部")
        {
            filtered = filtered.Where(e => e.ImpactDisplay == FilterImpact);
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

        Entries = new ObservableCollection<StartupEntry>(filtered);
        UpdateStatistics();
    }

    /// <summary>
    /// 更新统计信息显示
    /// </summary>
    private void UpdateStatistics()
    {
        var total = _allEntries.Count;
        var enabled = _allEntries.Count(e => e.IsEnabled);
        var disabled = total - enabled;
        StatisticsText = $"共 {total} 项 | 已启用 {enabled} | 已禁用 {disabled}";
    }

    // ==================== 导出 CSV ====================

    /// <summary>
    /// 导出当前显示的启动项列表为 CSV 文件
    /// 保存到文档目录
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var fileName = $"启动项列表_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var filePath = Path.Combine(documents, fileName);

            using var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
            // 写入 BOM
            writer.WriteLine("名称,发布者,路径,参数,来源,影响程度,状态");

            var items = string.IsNullOrEmpty(SearchText) &&
                        FilterSource == "全部" &&
                        FilterImpact == "全部" &&
                        FilterStatus == "全部"
                ? _allEntries
                : Entries.ToList();

            foreach (var entry in items)
            {
                var name = EscapeCsv(entry.Name);
                var publisher = EscapeCsv(entry.Publisher);
                var csvPath = EscapeCsv(entry.FilePath);
                var args = EscapeCsv(entry.Arguments);
                var source = EscapeCsv(entry.SourceDisplay);
                var impact = EscapeCsv(entry.ImpactDisplay);
                var status = entry.IsEnabled ? "已启用" : "已禁用";

                writer.WriteLine($"{name},{publisher},{csvPath},{args},{source},{impact},{status}");
            }

            StatusMessage = $"已导出: {filePath}";

            // 询问是否打开文件所在目录
            if (ConfirmationHelper.RequestL1($"已导出到: {filePath}\n\n是否打开文件所在目录？"))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
        }
        catch (Exception ex)
        {
            _log.LogError("导出CSV失败", ex);
            StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 转义 CSV 字段中的特殊字符（引号和逗号）
    /// </summary>
    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ==================== 打开文件位置 ====================

    /// <summary>
    /// 在资源管理器中打开启动项对应的文件所在位置
    /// </summary>
    [RelayCommand]
    private void OpenFileLocation(StartupEntry? entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.FilePath)) return;

        try
        {
            var dir = Path.GetDirectoryName(entry.FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start("explorer.exe", dir);
                return;
            }
        }
        catch { }

        // 尝试打开快捷方式所在目录
        if (!string.IsNullOrEmpty(entry.ShortcutPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(entry.ShortcutPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start("explorer.exe", dir);
                }
            }
            catch { }
        }
    }
}
