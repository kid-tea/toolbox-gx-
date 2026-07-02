using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 注册表扫描项复选框数据模型
/// </summary>
public partial class ScanCategoryItem : ObservableObject
{
    /// <summary>扫描分类</summary>
    public RegistryScanCategory Category { get; set; }

    /// <summary>显示名称</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>是否选中</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>描述</summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// 注册表清理 ViewModel
/// 功能：6类注册表问题扫描、分页显示（200条/页）、备份历史管理、
///       还原前 SHA256 校验、高级电源选项
/// 安全铁律：备份写入失败 → 不执行修复；还原前 SHA256 校验
/// </summary>
public partial class RegistryCleanerViewModel : ViewModelBase
{
    private readonly IRegistryCleanerService _cleaner;
    private readonly ILogService _log;
    private readonly IConfigService _config;

    // ==================== 扫描分类 ====================

    /// <summary>扫描分类复选框列表</summary>
    [ObservableProperty]
    private ObservableCollection<ScanCategoryItem> _scanCategories = new();

    /// <summary>扫描结果的完整列表（未分页）</summary>
    private List<RegistryIssue> _allIssues = new();

    /// <summary>当前页显示的结果</summary>
    [ObservableProperty]
    private ObservableCollection<RegistryIssue> _issues = new();

    /// <summary>当前页码</summary>
    [ObservableProperty]
    private int _currentPage = 1;

    /// <summary>总页数</summary>
    [ObservableProperty]
    private int _totalPages = 1;

    /// <summary>每页条数</summary>
    [ObservableProperty]
    private int _pageSize = 200;

    /// <summary>按严重程度过滤</summary>
    [ObservableProperty]
    private string _filterSeverity = "全部";

    /// <summary>全选/取消全选</summary>
    [ObservableProperty]
    private bool _isSelectAll = true;

    /// <summary>统计信息</summary>
    [ObservableProperty]
    private string _statisticsText = "就绪";

    /// <summary>选中的问题数量</summary>
    [ObservableProperty]
    private int _selectedCount;

    // ==================== 备份管理 ====================

    /// <summary>备份历史列表</summary>
    [ObservableProperty]
    private ObservableCollection<RegistryBackup> _backupHistory = new();

    /// <summary>选中的备份</summary>
    [ObservableProperty]
    private RegistryBackup? _selectedBackup;

    /// <summary>是否显示备份面板</summary>
    [ObservableProperty]
    private bool _isBackupPanelOpen;

    // ==================== 高级电源选项 ====================

    /// <summary>高级电源选项是否可见</summary>
    [ObservableProperty]
    private bool _isPowerOptionsVisible = true;

    /// <summary>电源选项操作状态</summary>
    [ObservableProperty]
    private string _powerOptionsStatus = "就绪";

    // ==================== 过滤选项 ====================

    public List<string> SeverityFilters { get; } = new()
    {
        "全部", "高", "中", "低"
    };

    /// <summary>
    /// 构造函数 — 通过 DI 注入服务
    /// 初始化扫描分类复选框
    /// </summary>
    public RegistryCleanerViewModel(
        IRegistryCleanerService cleanerService,
        ILogService logService,
        IConfigService configService)
    {
        _cleaner = cleanerService;
        _log = logService;
        _config = configService;

        // 初始化扫描分类
        ScanCategories = new ObservableCollection<ScanCategoryItem>
        {
            new() { Category = RegistryScanCategory.InvalidFileAssociation,
                DisplayName = "无效文件关联", Description = "扩展名指向不存在的程序" },
            new() { Category = RegistryScanCategory.UninstallResidue,
                DisplayName = "卸载残留", Description = "已卸载程序留下的注册表项" },
            new() { Category = RegistryScanCategory.InvalidShortcut,
                DisplayName = "无效快捷方式", Description = "开始菜单/桌面指向不存在文件的快捷方式" },
            new() { Category = RegistryScanCategory.EmptyKey,
                DisplayName = "空注册表键", Description = "没有值和子键的空键" },
            new() { Category = RegistryScanCategory.StartupResidue,
                DisplayName = "启动项残留", Description = "自启动项指向已删除的程序" },
            new() { Category = RegistryScanCategory.InvalidComOle,
                DisplayName = "无效 COM/OLE", Description = "COM 组件 DLL/EXE 文件不存在 (采样1000)" },
        };

        StatusMessage = "就绪 — 选择扫描分类后点击「扫描」";

        // 加载备份历史
        RefreshBackupHistory();
    }

    // ==================== 扫描 ====================

    /// <summary>
    /// 执行注册表扫描
    /// 仅扫描用户选中的分类
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        // 检查是否有管理员权限
        if (!ConfirmationHelper.IsAdministrator())
        {
            if (!ConfirmationHelper.RequestAdmin())
            {
                StatusMessage = "注册表清理需要管理员权限";
                return;
            }

            StatusMessage = "正在请求管理员权限，请在提权后的新窗口中重试";
            return;
        }

        var selectedCategories = ScanCategories
            .Where(c => c.IsSelected)
            .Select(c => c.Category)
            .ToList();

        if (selectedCategories.Count == 0)
        {
            StatusMessage = "请至少选择一项扫描分类";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "正在扫描注册表...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                ProgressValue = p;
            });

            var issues = await Task.Run(() =>
                _cleaner.ScanIssues(selectedCategories, progress, CancellationToken.None));

            _allIssues = issues;
            CurrentPage = 1;
            ApplyFilters();

            var lowCount = issues.Count(i => i.Severity == IssueSeverity.Low);
            var medCount = issues.Count(i => i.Severity == IssueSeverity.Medium);
            var highCount = issues.Count(i => i.Severity == IssueSeverity.High);

            StatisticsText = $"共 {issues.Count} 个问题 (高:{highCount} 中:{medCount} 低:{lowCount})";
            StatusMessage = $"扫描完成: 发现 {issues.Count} 个问题";
            _log.LogInfo($"注册表扫描完成: {issues.Count} 个问题");
        }
        catch (Exception ex)
        {
            _log.LogError("注册表扫描异常", ex);
            StatusMessage = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ==================== 分页 ====================

    /// <summary>
    /// 上一页
    /// </summary>
    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            ApplyFilters();
        }
    }

    /// <summary>
    /// 下一页
    /// </summary>
    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            ApplyFilters();
        }
    }

    /// <summary>
    /// 全选/取消全选
    /// </summary>
    partial void OnIsSelectAllChanged(bool value)
    {
        if (_updatingSelectAll) return;

        foreach (var issue in _allIssues)
        {
            issue.IsSelected = value;
        }
        // 刷新当前页显示
        ApplyFilters();
        UpdateSelectedCount();
    }

    /// <summary>
    /// 更新选中数量并同步全选框状态
    /// </summary>
    [RelayCommand]
    private void UpdateSelection()
    {
        UpdateSelectedCount();
    }

    /// <summary>是否正在通过代码更新 IsSelectAll（防止循环触发）</summary>
    private bool _updatingSelectAll;

    private void UpdateSelectedCount()
    {
        SelectedCount = _allIssues.Count(i => i.IsSelected);
        // 避免触发 OnIsSelectAllChanged 的循环
        _updatingSelectAll = true;
        if (SelectedCount == 0)
            IsSelectAll = false;
        else if (SelectedCount == _allIssues.Count)
            IsSelectAll = true;
        else
            IsSelectAll = false;
        _updatingSelectAll = false;
    }

    // ==================== 修复 ====================

    /// <summary>
    /// 执行修复
    /// 安全铁律：先创建备份 → 备份成功才执行修复
    /// 修复前 L1 + L2 双重确认
    /// </summary>
    [RelayCommand]
    private async Task FixAsync()
    {
        if (IsBusy) return;

        if (!ConfirmationHelper.IsAdministrator())
        {
            if (!ConfirmationHelper.RequestAdmin())
            {
                StatusMessage = "注册表修复需要管理员权限";
                return;
            }

            StatusMessage = "正在请求管理员权限，请在提权后的新窗口中重试";
            return;
        }

        var selectedIssues = _allIssues.Where(i => i.IsSelected).ToList();
        if (selectedIssues.Count == 0)
        {
            StatusMessage = "请先选择要修复的问题";
            return;
        }

        // 确认
        if (!ConfirmationHelper.RequestL1(
            $"即将修复 {selectedIssues.Count} 个注册表问题。\n\n" +
            $"此操作将修改系统注册表，建议先创建备份。\n\n确定继续？"))
            return;

        // L2 高频问题二次确认
        var highIssues = selectedIssues.Count(i => i.Severity == IssueSeverity.High);
        if (highIssues > 0)
        {
            if (!ConfirmationHelper.RequestL2(
                $"其中包含 {highIssues} 个高风险问题。\n" +
                $"高风险问题修复可能导致部分软件功能异常。\n\n确定继续？"))
                return;
        }

        IsBusy = true;
        StatusMessage = "正在创建备份...";

        try
        {
            // 1. 创建备份（安全铁律：备份失败不修复）
            var backup = await Task.Run(() => _cleaner.CreateBackup(selectedIssues));

            if (backup == null)
            {
                StatusMessage = "备份创建失败，已取消修复操作。请检查磁盘空间和权限。";
                _log.LogError("备份创建失败，修复操作已中止");
                IsBusy = false;
                return;
            }

            StatusMessage = $"备份已创建: {backup.FileName}，正在修复...";
            IsProgressIndeterminate = true;

            // 2. 执行修复
            var progress = new Progress<int>(p =>
            {
                ProgressValue = p;
            });

            int fixedCount = await Task.Run(() =>
                _cleaner.FixIssues(selectedIssues, progress));

            // 3. 更新结果
            _allIssues.RemoveAll(i => i.IsSelected);
            ApplyFilters();
            UpdateSelectedCount();
            RefreshBackupHistory();

            StatisticsText = $"剩余 {_allIssues.Count} 个问题";
            StatusMessage = $"修复完成: 成功修复 {fixedCount}/{selectedIssues.Count} 个问题";

            _log.LogOperation("registry", "fix_complete",
                $"修复 {fixedCount}/{selectedIssues.Count} 问题, 备份: {backup.FileName}");
        }
        catch (Exception ex)
        {
            _log.LogError("修复注册表异常", ex);
            StatusMessage = $"修复失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ==================== 备份管理 ====================

    /// <summary>
    /// 刷新备份历史列表
    /// </summary>
    [RelayCommand]
    private void RefreshBackupHistory()
    {
        var history = _cleaner.GetBackupHistory();
        BackupHistory = new ObservableCollection<RegistryBackup>(history);
    }

    /// <summary>
    /// 切换备份面板显示
    /// </summary>
    [RelayCommand]
    private void ToggleBackupPanel()
    {
        IsBackupPanelOpen = !IsBackupPanelOpen;
        if (IsBackupPanelOpen)
            RefreshBackupHistory();
    }

    /// <summary>
    /// 从备份还原
    /// 安全铁律：还原前 SHA256 校验
    /// L2 确认
    /// </summary>
    [RelayCommand]
    private void RestoreBackup(RegistryBackup? backup)
    {
        if (backup == null) return;

        if (!ConfirmationHelper.IsAdministrator())
        {
            if (!ConfirmationHelper.RequestAdmin())
            {
                StatusMessage = "还原备份需要管理员权限";
                return;
            }

            StatusMessage = "正在请求管理员权限，请在提权后的新窗口中重试";
            return;
        }

        // SHA256 校验
        if (!_cleaner.ValidateBackup(backup))
        {
            StatusMessage = "备份文件校验失败，文件可能已损坏或被修改";
            _log.LogError($"备份 SHA256 校验失败: {backup.FileName}");
            return;
        }

        // L2 确认
        if (!ConfirmationHelper.RequestL2(
            $"即将还原注册表备份:\n\n" +
            $"文件名: {backup.FileName}\n" +
            $"创建时间: {backup.CreatedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"描述: {backup.Description}\n\n" +
            $"还原操作将覆盖当前的注册表设置，确定继续？"))
            return;

        IsBusy = true;
        StatusMessage = "正在还原备份...";
        IsProgressIndeterminate = true;

        try
        {
            bool success = _cleaner.RestoreFromBackup(backup);

            if (success)
            {
                StatusMessage = $"已从备份还原: {backup.FileName}";
                RefreshBackupHistory();
                _log.LogOperation("registry", "restore", $"还原成功: {backup.FileName}");
            }
            else
            {
                StatusMessage = "还原失败，请检查注册表权限";
                _log.LogError($"备份还原失败: {backup.FileName}");
            }
        }
        catch (Exception ex)
        {
            _log.LogError("还原备份异常", ex);
            StatusMessage = $"还原失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// 删除备份文件
    /// </summary>
    [RelayCommand]
    private void DeleteBackup(RegistryBackup? backup)
    {
        if (backup == null) return;

        if (!ConfirmationHelper.RequestL1($"确定删除备份文件 \"{backup.FileName}\" 吗？"))
            return;

        try
        {
            if (File.Exists(backup.FilePath))
            {
                File.Delete(backup.FilePath);
                BackupHistory.Remove(backup);
                StatusMessage = $"已删除备份: {backup.FileName}";
                _log.LogOperation("registry", "delete_backup", backup.FileName);
            }
            else
            {
                BackupHistory.Remove(backup);
                StatusMessage = $"备份文件不存在，已从列表移除: {backup.FileName}";
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"删除备份失败: {backup.FileName}", ex);
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    // ==================== 高级电源选项 ====================

    /// <summary>
    /// 显示高级电源选项（将 Attributes 设为 2）
    /// </summary>
    [RelayCommand]
    private void ShowPowerOptions()
    {
        if (!ConfirmationHelper.IsAdministrator())
        {
            if (!ConfirmationHelper.RequestAdmin())
            {
                PowerOptionsStatus = "需要管理员权限";
                return;
            }

            PowerOptionsStatus = "正在请求管理员权限，请在提权后的新窗口中重试";
            return;
        }

        try
        {
            if (_cleaner.SetPowerOptionsVisibility(true))
            {
                IsPowerOptionsVisible = true;
                PowerOptionsStatus = "高级电源选项已显示";
                _log.LogOperation("registry", "power", "显示高级电源选项");
            }
            else
            {
                PowerOptionsStatus = "操作失败";
            }
        }
        catch (Exception ex)
        {
            _log.LogError("显示电源选项失败", ex);
            PowerOptionsStatus = $"失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 隐藏高级电源选项（将 Attributes 设为 1）
    /// </summary>
    [RelayCommand]
    private void HidePowerOptions()
    {
        if (!ConfirmationHelper.IsAdministrator())
        {
            if (!ConfirmationHelper.RequestAdmin())
            {
                PowerOptionsStatus = "需要管理员权限";
                return;
            }

            PowerOptionsStatus = "正在请求管理员权限，请在提权后的新窗口中重试";
            return;
        }

        try
        {
            if (_cleaner.SetPowerOptionsVisibility(false))
            {
                IsPowerOptionsVisible = false;
                PowerOptionsStatus = "高级电源选项已隐藏（恢复默认）";
                _log.LogOperation("registry", "power", "隐藏高级电源选项");
            }
            else
            {
                PowerOptionsStatus = "操作失败";
            }
        }
        catch (Exception ex)
        {
            _log.LogError("隐藏电源选项失败", ex);
            PowerOptionsStatus = $"失败: {ex.Message}";
        }
    }

    // ==================== 过滤 ====================

    /// <summary>
    /// 严重程度过滤变化时重新应用过滤
    /// </summary>
    partial void OnFilterSeverityChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// 应用过滤和分页
    /// </summary>
    private void ApplyFilters()
    {
        var filtered = _allIssues.AsEnumerable();

        // 按严重程度过滤
        if (FilterSeverity == "高")
        {
            filtered = filtered.Where(i => i.Severity == IssueSeverity.High);
        }
        else if (FilterSeverity == "中")
        {
            filtered = filtered.Where(i => i.Severity == IssueSeverity.Medium);
        }
        else if (FilterSeverity == "低")
        {
            filtered = filtered.Where(i => i.Severity == IssueSeverity.Low);
        }

        var filteredList = filtered.ToList();
        TotalPages = Math.Max(1, (int)Math.Ceiling(filteredList.Count / (double)PageSize));

        if (CurrentPage > TotalPages)
            CurrentPage = TotalPages;

        var page = filteredList
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        Issues = new ObservableCollection<RegistryIssue>(page);
        UpdateSelectedCount();
    }
}
