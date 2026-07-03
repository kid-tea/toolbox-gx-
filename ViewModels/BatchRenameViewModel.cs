using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Toolbox.Helpers;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 批量重命名功能的 ViewModel
/// 支持6种重命名规则、多规则组合、实时预览、会话内撤销
/// 预览安全检测：Windows保留名、非法字符、空文件名、目标已存在、路径过长
/// </summary>
public partial class BatchRenameViewModel : ViewModelBase
{
    private readonly ILogService _log;

    /// <summary>Windows 保留文件名（不分大小写，不含扩展名时检查）</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>文件名中不允许的非法字符</summary>
    private static readonly char[] IllegalChars = Path.GetInvalidFileNameChars();

    /// <summary>文件列表</summary>
    public ObservableCollection<RenameFileItem> FileItems { get; } = new();

    /// <summary>重命名规则列表（支持拖拽排序）</summary>
    public ObservableCollection<RenameRule> Rules { get; } = new();

    /// <summary>撤销历史：记录已完成的重命名操作（原路径 → 新路径），仅当前会话有效</summary>
    private readonly Stack<List<(string OldPath, string NewPath)>> _undoStack = new();

    /// <summary>是否有文件</summary>
    [ObservableProperty]
    private bool _hasFiles;

    /// <summary>是否正在执行预览</summary>
    [ObservableProperty]
    private bool _isPreviewing;

    /// <summary>预览统计信息</summary>
    [ObservableProperty]
    private string _previewSummary = "";

    /// <summary>选定的规则类型（用于添加规则时）</summary>
    [ObservableProperty]
    private RenameRuleType _selectedRuleType = RenameRuleType.Prefix;

    /// <summary>规则类型选择索引（0=Prefix, 1=Suffix, 2=FindReplace, ...）</summary>
    [ObservableProperty]
    private int _selectedRuleTypeIndex;

    /// <summary>规则参数1</summary>
    [ObservableProperty]
    private string _ruleParam1 = "";

    /// <summary>规则参数2</summary>
    [ObservableProperty]
    private string _ruleParam2 = "1";

    /// <summary>
    /// 规则类型索引变更时同步枚举值
    /// </summary>
    partial void OnSelectedRuleTypeIndexChanged(int value)
    {
        if (value >= 0 && value <= 8)
            SelectedRuleType = (RenameRuleType)value;
    }

    /// <summary>
    /// 规则类型枚举变更时同步索引
    /// </summary>
    partial void OnSelectedRuleTypeChanged(RenameRuleType value)
    {
        SelectedRuleTypeIndex = (int)value;
    }

    /// <summary>进度文本</summary>
    [ObservableProperty]
    private string _progressText = "就绪";

    /// <summary>成功/失败统计文本</summary>
    [ObservableProperty]
    private string _resultSummary = "";

    /// <summary>是否显示撤销按钮（有撤销历史时）</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>构造函数，通过 DI 注入日志服务</summary>
    public BatchRenameViewModel(ILogService log)
    {
        _log = log;

        // 默认添加一个前缀规则
        Rules.CollectionChanged += (_, _) => OnRulesChanged();
    }

    /// <summary>
    /// 规则列表变更时自动刷新预览
    /// </summary>
    private void OnRulesChanged()
    {
        ExecuteRenameCommand.NotifyCanExecuteChanged();
        if (FileItems.Count > 0 && Rules.Count > 0)
            RefreshPreview();
    }

    /// <summary>
    /// 添加文件或文件夹到列表
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var normalizedPath = path.Trim('"');
            if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath)) continue;
            if (FileItems.Any(f => string.Equals(f.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            var originalName = Path.GetFileName(normalizedPath);
            var ext = File.Exists(normalizedPath) ? Path.GetExtension(originalName) : "";
            var nameWithoutExt = File.Exists(normalizedPath) ? Path.GetFileNameWithoutExtension(originalName) : originalName;

            var item = new RenameFileItem
            {
                FilePath = normalizedPath,
                OriginalName = originalName,
                Directory = Path.GetDirectoryName(normalizedPath) ?? "",
                Extension = ext,
                NameWithoutExtension = nameWithoutExt,
                NewName = originalName,
                Status = RenameItemStatus.Pending
            };

            FileItems.Add(item);
        }
        UpdateStates();
        ExecuteRenameCommand.NotifyCanExecuteChanged();
        _log.LogInfo($"Added items to rename list, total: {FileItems.Count}");

        if (Rules.Count > 0)
            RefreshPreview();
    }

    /// <summary>
    /// 浏览添加文件
    /// </summary>
    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要重命名的文件",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            AddFiles(dialog.FileNames);
    }

    /// <summary>
    /// 移除单个文件
    /// </summary>
    [RelayCommand]
    private void RemoveFile(RenameFileItem? item)
    {
        if (item == null) return;
        FileItems.Remove(item);
        UpdateStates();
        if (Rules.Count > 0) RefreshPreview();
    }

    /// <summary>
    /// 清空所有文件
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        FileItems.Clear();
        UpdateStates();
        PreviewSummary = "";
        ResultSummary = "";
        ProgressText = "就绪";
    }

    /// <summary>
    /// 添加一条重命名规则
    /// </summary>
    [RelayCommand]
    private void AddRule()
    {
        var rule = new RenameRule
        {
            RuleType = SelectedRuleType,
            Parameter = RuleParam1,
            Parameter2 = RuleParam2
        };

        // 监听规则参数变更以自动刷新预览
        rule.PropertyChanged += (_, _) =>
        {
            if (FileItems.Count > 0)
                RefreshPreview();
        };

        Rules.Add(rule);
        _log.LogInfo($"Added rename rule: {rule.Description}");
    }

    /// <summary>
    /// 移除选定的规则
    /// </summary>
    [RelayCommand]
    private void RemoveRule(RenameRule? rule)
    {
        if (rule == null) return;
        Rules.Remove(rule);
    }

    /// <summary>
    /// 上移规则（调整优先级）
    /// </summary>
    [RelayCommand]
    private void MoveRuleUp(RenameRule? rule)
    {
        if (rule == null) return;
        int idx = Rules.IndexOf(rule);
        if (idx > 0)
        {
            Rules.Move(idx, idx - 1);
            RefreshPreview();
        }
    }

    /// <summary>
    /// 下移规则（调整优先级）
    /// </summary>
    [RelayCommand]
    private void MoveRuleDown(RenameRule? rule)
    {
        if (rule == null) return;
        int idx = Rules.IndexOf(rule);
        if (idx < Rules.Count - 1)
        {
            Rules.Move(idx, idx + 1);
            RefreshPreview();
        }
    }

    /// <summary>
    /// 刷新预览 — 对所有文件应用所有规则并检测安全问题
    /// </summary>
    [RelayCommand]
    private void RefreshPreview()
    {
        if (FileItems.Count == 0) return;

        IsPreviewing = true;
        int warningCount = 0;
        int errorCount = 0;

        try
        {
            foreach (var item in FileItems)
            {
                if (item.Status == RenameItemStatus.Success)
                    continue; // 已成功重命名的跳过预览

                // 应用所有规则
                string newName = item.NameWithoutExtension;

                string newExtension = item.Extension;
                int itemIndex = FileItems.IndexOf(item) + 1;

                foreach (var rule in Rules)
                {
                    if (rule.RuleType == RenameRuleType.Extension)
                        newExtension = NormalizeExtension(rule.Parameter, newExtension);
                    else
                        newName = ApplyRule(newName, rule, item, itemIndex);
                }

                // 还原扩展名
                string fullNewName = newName + item.Extension;
                fullNewName = newName + newExtension;
                item.NewName = fullNewName;

                // 安全检测
                CheckPreviewSafety(item, newName, fullNewName);

                if (item.Warning == RenameWarningType.IllegalChars ||
                    item.Warning == RenameWarningType.EmptyName ||
                    item.Warning == RenameWarningType.ReservedName)
                    errorCount++;
                else if (item.Warning != RenameWarningType.None)
                    warningCount++;
            }

            PreviewSummary = errorCount > 0
                ? $"预览完成: {FileItems.Count} 个文件, {errorCount} 个错误, {warningCount} 个警告"
                : warningCount > 0
                    ? $"预览完成: {FileItems.Count} 个文件, {warningCount} 个警告"
                    : $"预览完成: {FileItems.Count} 个文件, 无问题";

            _log.LogInfo(PreviewSummary);
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    /// <summary>是否可以执行重命名（有文件且有规则）</summary>
    public bool CanExecuteRename => HasFiles && Rules.Count > 0 && !IsBusy;

    /// <summary>
    /// 执行重命名
    /// 流程：L1确认 → 执行前检测文件存在 → 执行 → 统计 → 保留撤销信息
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteRename))]
    private Task ExecuteRename()
    {
        if (FileItems.Count == 0 || Rules.Count == 0) return Task.CompletedTask;

        // 确认操作
        if (!ConfirmationHelper.RequestL1(
            $"确认对 {FileItems.Count} 个文件执行重命名？\n\n规则数: {Rules.Count}\n\n此操作可以撤销（仅当前会话有效）。"))
            return Task.CompletedTask;

        // 执行前重新检测文件存在
        var missingFiles = new List<RenameFileItem>();
        foreach (var item in FileItems.Where(f => f.Status == RenameItemStatus.Pending))
        {
            if (!File.Exists(item.FilePath))
            {
                missingFiles.Add(item);
                item.Warning = RenameWarningType.FileLocked;
                item.WarningMessage = "文件已不存在";
                item.IsErrorWarning = true;
            }
        }

        if (missingFiles.Count > 0)
        {
            if (MessageBox.Show(
                $"发现 {missingFiles.Count} 个文件已不存在，将跳过。\n是否继续？",
                "文件不存在", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return Task.CompletedTask;
        }

        IsBusy = true;
        ProgressMax = FileItems.Count(f => f.Status == RenameItemStatus.Pending);
        ProgressValue = 0;

        var undoList = new List<(string OldPath, string NewPath)>();
        int successCount = 0;
        var failedItems = new List<(RenameFileItem Item, string Reason)>();

        try
        {
            foreach (var item in FileItems.Where(f => f.Status == RenameItemStatus.Pending))
            {
                ProgressText = $"正在重命名: {item.OriginalName} → {item.NewName}";

                // 跳过有严重错误的项
                if (item.Warning == RenameWarningType.IllegalChars ||
                    item.Warning == RenameWarningType.EmptyName ||
                    item.Warning == RenameWarningType.ReservedName ||
                    item.Warning == RenameWarningType.FileLocked)
                {
                    failedItems.Add((item, item.WarningMessage));
                    ProgressValue++;
                    continue;
                }

                string newPath = Path.Combine(item.Directory, item.NewName);

                try
                {
                    // 如果目标已存在，询问用户
                    if (File.Exists(newPath) && !string.Equals(item.FilePath, newPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var choice = MessageBox.Show(
                            $"目标文件已存在:\n  {item.NewName}\n\n是否覆盖？",
                            "文件冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                        if (choice == MessageBoxResult.No)
                        {
                            failedItems.Add((item, "目标已存在，用户跳过"));
                            ProgressValue++;
                            continue;
                        }

                        // 覆盖：先删除目标
                        File.Delete(newPath);
                    }

                    File.Move(item.FilePath, newPath);
                    undoList.Add((item.FilePath, newPath));

                    item.FilePath = newPath;
                    item.OriginalName = item.NewName;
                    item.Directory = Path.GetDirectoryName(newPath) ?? "";
                    item.Status = RenameItemStatus.Success;
                    item.Warning = RenameWarningType.None;
                    item.WarningMessage = "";
                    item.IsErrorWarning = false;
                    item.IsCautionWarning = false;
                    successCount++;
                }
                catch (PathTooLongException)
                {
                    item.Warning = RenameWarningType.PathTooLong;
                    item.WarningMessage = "路径过长（>260字符）";
                    item.IsCautionWarning = true;
                    failedItems.Add((item, "路径过长"));
                }
                catch (IOException ex)
                {
                    item.Warning = RenameWarningType.FileLocked;
                    item.WarningMessage = $"文件被占用: {ex.Message}";
                    item.IsErrorWarning = true;
                    failedItems.Add((item, $"文件被占用: {ex.Message}"));
                }
                catch (UnauthorizedAccessException ex)
                {
                    item.Warning = RenameWarningType.AccessDenied;
                    item.WarningMessage = $"权限不足: {ex.Message}";
                    item.IsErrorWarning = true;
                    failedItems.Add((item, $"权限不足: {ex.Message}"));
                }
                catch (Exception ex)
                {
                    item.WarningMessage = $"重命名失败: {ex.Message}";
                    item.IsErrorWarning = true;
                    failedItems.Add((item, $"重命名失败: {ex.Message}"));
                }

                ProgressValue++;
            }

            // 记录撤销信息
            if (undoList.Count > 0)
            {
                _undoStack.Push(undoList);
                CanUndo = true;
            }

            // 统计结果
            ResultSummary = $"完成: 成功 {successCount}, 失败 {failedItems.Count}";
            ProgressText = ResultSummary;
            _log.LogInfo($"Batch rename completed: {successCount} succeeded, {failedItems.Count} failed");

            // 失败项弹窗
            if (failedItems.Count > 0)
            {
                var failGroups = failedItems
                    .GroupBy(f => f.Reason)
                    .Select(g => $"{g.Key}: {g.Count()} 个文件");

                string failMsg = $"重命名完成，{failedItems.Count} 个文件失败:\n\n" +
                    string.Join("\n", failGroups) + "\n\n是否仅重试失败项？";

                if (MessageBox.Show(failMsg, "部分失败", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    RetryFailed();
                }
            }
            else if (successCount > 0)
            {
                MessageBox.Show($"所有 {successCount} 个文件重命名成功！",
                    "操作完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            IsBusy = false;
            if (undoList.Count > 0)
                RefreshPreview();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 仅重试失败项
    /// </summary>
    [RelayCommand]
    private void RetryFailed()
    {
        // 将失败项重置为待处理状态
        foreach (var item in FileItems.Where(f =>
            f.Warning == RenameWarningType.FileLocked ||
            f.Warning == RenameWarningType.AccessDenied ||
            f.Warning == RenameWarningType.PathTooLong))
        {
            item.Warning = RenameWarningType.None;
            item.WarningMessage = "";
            item.IsErrorWarning = false;
            item.IsCautionWarning = false;
        }

        RefreshPreview();
        // Re-trigger the rename
        _ = ExecuteRename();
    }

    /// <summary>
    /// 撤销全部重命名（仅当前会话有效）
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAll()
    {
        if (_undoStack.Count == 0) return Task.CompletedTask;

        if (!ConfirmationHelper.RequestL1(
            $"确认撤销最近一次重命名操作？\n\n将恢复 {_undoStack.Peek().Count} 个文件的原始名称。"))
            return Task.CompletedTask;

        IsBusy = true;
        int reverted = 0;
        int failed = 0;

        try
        {
            var undoList = _undoStack.Pop();
            // 反转顺序以便撤销（后改的先恢复）

            foreach (var (oldPath, newPath) in undoList.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(newPath))
                    {
                        File.Move(newPath, oldPath);
                        reverted++;
                    }

                    // 更新文件项
                    var item = FileItems.FirstOrDefault(f =>
                        string.Equals(f.FilePath, newPath, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        item.FilePath = oldPath;
                        item.OriginalName = Path.GetFileName(oldPath);
                        item.NewName = item.OriginalName;
                        item.Directory = Path.GetDirectoryName(oldPath) ?? "";
                        item.Status = RenameItemStatus.Pending;
                        item.Warning = RenameWarningType.None;
                        item.WarningMessage = "";
                    }
                }
                catch
                {
                    failed++;
                }
            }

            CanUndo = _undoStack.Count > 0;
            ProgressText = $"撤销完成: 成功 {reverted}, 失败 {failed}";
            _log.LogInfo($"Undo completed: {reverted} reverted, {failed} failed");
            RefreshPreview();
        }
        finally
        {
            IsBusy = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理拖放文件
    /// </summary>
    public void HandleDrop(IDataObject dropData)
    {
        if (dropData.GetDataPresent(DataFormats.FileDrop))
        {
            var files = dropData.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
                AddFiles(files);
        }
    }

    // ==================== 私有辅助方法 ====================

    /// <summary>
    /// 更新状态属性
    /// </summary>
    private void UpdateStates()
    {
        HasFiles = FileItems.Count > 0;
    }

    /// <summary>
    /// 应用单个规则到文件名（不含扩展名）
    /// </summary>
    private static string ApplyRule(string name, RenameRule rule, RenameFileItem item, int index)
    {
        try
        {
            return rule.RuleType switch
            {
                RenameRuleType.Prefix => rule.Parameter + name,
                RenameRuleType.Suffix => name + rule.Parameter,
                RenameRuleType.FindReplace => name.Replace(rule.Parameter, rule.Parameter2),
                RenameRuleType.RemoveChars => RemoveCharsByName(name, rule.Parameter, rule.Parameter2),
                RenameRuleType.Case => ApplyCase(name, rule.Parameter),
                RenameRuleType.Numbering => ApplyNumbering(name, rule.Parameter, rule.Parameter2, index),
                RenameRuleType.Regex => ApplyRegex(name, rule.Parameter, rule.Parameter2),
                RenameRuleType.Template => ApplyTemplate(rule.Parameter, item, index),
                _ => name
            };
        }
        catch
        {
            return name; // 规则应用失败，返回原名
        }
    }

    /// <summary>
    /// 移除指定位置的字符
    /// Parameter = 起始位置（1-based）, Parameter2 = 要移除的字符数
    /// </summary>
    private static string ApplyNumbering(string name, string startParam, string stepParam, int index)
    {
        int start = int.TryParse(startParam, out var parsedStart) ? parsedStart : 1;
        int step = int.TryParse(stepParam, out var parsedStep) && parsedStep > 0 ? parsedStep : 1;
        int number = start + ((index - 1) * step);
        int width = Math.Max(2, Math.Max(startParam.Length, number.ToString().Length));
        return $"{name}_{number.ToString().PadLeft(width, '0')}";
    }

    private static string ApplyTemplate(string template, RenameFileItem item, int index)
    {
        if (string.IsNullOrWhiteSpace(template))
            return item.NameWithoutExtension;

        return template
            .Replace("{name}", item.NameWithoutExtension, StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", index.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{nn}", index.ToString("00"), StringComparison.OrdinalIgnoreCase)
            .Replace("{nnn}", index.ToString("000"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTime.Now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string value, string currentExtension)
    {
        if (string.IsNullOrWhiteSpace(value))
            return currentExtension;

        var extension = value.Trim();
        if (extension == ".")
            return "";

        return extension.StartsWith('.') ? extension : "." + extension;
    }

    private static string RemoveCharsByName(string name, string startParam, string countParam)
    {
        if (!int.TryParse(startParam, out int start) || start < 1) return name;
        if (!int.TryParse(countParam, out int count) || count < 0) return name;

        start -= 1; // 转为 0-based
        if (start >= name.Length) return name;

        int actualCount = Math.Min(count, name.Length - start);
        return name.Remove(start, actualCount);
    }

    /// <summary>
    /// 应用大小写规则
    /// Parameter: "大写" / "小写" / "首字母大写" / "每个单词首字母大写"
    /// </summary>
    private static string ApplyCase(string name, string caseType)
    {
        return caseType switch
        {
            "大写" => name.ToUpperInvariant(),
            "小写" => name.ToLowerInvariant(),
            "首字母大写" => name.Length > 0
                ? char.ToUpperInvariant(name[0]) + (name.Length > 1 ? name[1..] : "")
                : name,
            "每个单词首字母大写" => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
            _ => name
        };
    }

    /// <summary>
    /// 应用正则表达式替换（带1秒超时）
    /// </summary>
    private static string ApplyRegex(string name, string pattern, string replacement)
    {
        if (string.IsNullOrEmpty(pattern)) return name;
        try
        {
            var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return regex.Replace(name, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return name; // 超时，返回原名
        }
        catch (ArgumentException)
        {
            return name; // 正则语法错误
        }
    }

    /// <summary>
    /// 检查预览结果的安全性并设置警告
    /// </summary>
    private void CheckPreviewSafety(RenameFileItem item, string newNameWithoutExt, string fullNewName)
    {
        item.Warning = RenameWarningType.None;
        item.WarningMessage = "";
        item.IsErrorWarning = false;
        item.IsCautionWarning = false;

        // 检测1：空文件名
        if (string.IsNullOrWhiteSpace(newNameWithoutExt))
        {
            item.Warning = RenameWarningType.EmptyName;
            item.WarningMessage = "文件名为空，无法重命名";
            item.IsErrorWarning = true;
            return;
        }

        // 检测2：Windows 保留名
        if (ReservedNames.Contains(newNameWithoutExt))
        {
            item.Warning = RenameWarningType.ReservedName;
            item.WarningMessage = $"\"{newNameWithoutExt}\" 是Windows保留文件名，可能导致文件无法访问";
            item.IsErrorWarning = true;
            return;
        }

        // 检测3：非法字符
        if (fullNewName.IndexOfAny(IllegalChars) >= 0)
        {
            var illegal = fullNewName.Where(c => IllegalChars.Contains(c)).Distinct();
            item.Warning = RenameWarningType.IllegalChars;
            item.WarningMessage = $"包含非法字符: {string.Join(" ", illegal)}";
            item.IsErrorWarning = true;
            return;
        }

        // 检测4：目标已存在
        string newPath = Path.Combine(item.Directory, fullNewName);
        if (File.Exists(newPath) && !string.Equals(newPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            item.Warning = RenameWarningType.TargetExists;
            item.WarningMessage = "目标文件名已存在";
            item.IsCautionWarning = true;
        }

        // 检测5：路径过长
        if (newPath.Length > 260)
        {
            item.Warning = RenameWarningType.PathTooLong;
            item.WarningMessage = $"路径过长 ({newPath.Length} > 260字符)";
            item.IsCautionWarning = true;
        }
    }
}
