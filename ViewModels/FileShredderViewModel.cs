using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Toolbox.Helpers;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 文件粉碎功能的 ViewModel
/// 提供拖拽添加、算法选择、四重防护确认、粉碎执行、操作日志等功能
/// 四级确认流程：
///   L3 — 风险警示 + 5秒冷却
///   文件清单 + 输入"确认粉碎"
///   执行前再次检测文件存在
///   最终执行
/// </summary>
public partial class FileShredderViewModel : ViewModelBase
{
    private readonly ILogService _log;
    private readonly IFileShredderService _shredder;
    private CancellationTokenSource? _cts;

    /// <summary>文件粉碎项列表</summary>
    public ObservableCollection<FileShredderItem> FileItems { get; } = new();

    /// <summary>操作日志列表</summary>
    public ObservableCollection<string> OperationLogs { get; } = new();

    /// <summary>是否有文件</summary>
    [NotifyCanExecuteChangedFor(nameof(ExecuteShredCommand))]
    [ObservableProperty]
    private bool _hasFiles;

    /// <summary>是否有操作日志</summary>
    [ObservableProperty]
    private bool _hasLogs;

    /// <summary>选定的覆写算法</summary>
    [ObservableProperty]
    private ShredderAlgorithm _selectedAlgorithm = ShredderAlgorithm.Secure;

    /// <summary>算法选择索引（0=Fast, 1=Secure, 2=Thorough）</summary>
    [ObservableProperty]
    private int _selectedAlgorithmIndex = 1;

    /// <summary>算法说明文本</summary>
    [ObservableProperty]
    private string _algorithmDescription = "3次覆写（随机+互补+随机），符合安全标准";

    /// <summary>进度文本</summary>
    [ObservableProperty]
    private string _progressText = "就绪";

    /// <summary>构造函数，通过 DI 注入日志和粉碎服务</summary>
    public FileShredderViewModel(ILogService log, IFileShredderService shredder)
    {
        _log = log;
        _shredder = shredder;
    }

    /// <summary>
    /// 算法选择变更时更新说明文字和索引
    /// </summary>
    partial void OnSelectedAlgorithmChanged(ShredderAlgorithm value)
    {
        AlgorithmDescription = value switch
        {
            ShredderAlgorithm.Fast => "1次覆写（随机数据），适用于SSD或快速清理",
            ShredderAlgorithm.Secure => "3次覆写（随机+互补+随机），符合安全标准",
            ShredderAlgorithm.Thorough => "7次覆写（Gutmann简化版），最高安全级别（HDD推荐）",
            _ => ""
        };
        SelectedAlgorithmIndex = (int)value;
    }

    /// <summary>
    /// 算法索引变更时更新枚举值
    /// </summary>
    partial void OnSelectedAlgorithmIndexChanged(int value)
    {
        if (value >= 0 && value <= 2)
            SelectedAlgorithm = (ShredderAlgorithm)value;
    }

    /// <summary>
    /// 更新 HasFiles 和 HasLogs 状态
    /// </summary>
    private void UpdateStates()
    {
        HasFiles = FileItems.Count > 0;
        HasLogs = OperationLogs.Count > 0;
    }

    /// <summary>
    /// 添加文件路径到列表，自动检查安全特性
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var normalizedPath = path.Trim('"');
            if (FileItems.Any(f => string.Equals(f.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
                continue;

            var item = new FileShredderItem
            {
                FilePath = normalizedPath,
                FileName = Path.GetFileName(normalizedPath),
                FileSize = FormatFileSize(normalizedPath)
            };

            // 检查安全特性
            try
            {
                item.IsOnSSD = _shredder.IsSSD(normalizedPath);
                item.HardLinkCount = _shredder.GetHardLinkCount(normalizedPath);
                item.HasHardLinks = item.HardLinkCount > 1;
                item.IsEFSEncrypted = _shredder.IsEFSEncrypted(normalizedPath);
                item.IsProtectedPath = ProtectedPaths.IsProtectedPath(normalizedPath);

                // 构建后果说明
                var warnings = new List<string>();
                if (item.IsOnSSD)
                    warnings.Add("SSD 仅1次覆写（SSD磨损均衡限制）");
                if (item.HasHardLinks)
                    warnings.Add($"硬链接({item.HardLinkCount}个)指向同一数据");
                if (item.IsEFSEncrypted)
                    warnings.Add("EFS加密文件");
                if (item.IsProtectedPath)
                    warnings.Add("位于系统受保护路径（高风险操作！）");

                item.Consequence = warnings.Count > 0
                    ? "警告: " + string.Join("; ", warnings)
                    : "粉碎后不可恢复！";
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to check file safety: {normalizedPath}, {ex.Message}");
            }

            FileItems.Add(item);
        }
        UpdateStates();
        _log.LogInfo($"Added {paths.Count()} files to shredder list, total: {FileItems.Count}");
    }

    /// <summary>
    /// 浏览添加文件
    /// </summary>
    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要粉碎的文件",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            AddFiles(dialog.FileNames);
    }

    /// <summary>
    /// 移除文件
    /// </summary>
    [RelayCommand]
    private void RemoveFile(FileShredderItem? item)
    {
        if (item == null) return;
        FileItems.Remove(item);
        UpdateStates();
    }

    /// <summary>
    /// 清空列表
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        FileItems.Clear();
        OperationLogs.Clear();
        UpdateStates();
        ProgressText = "就绪";
    }

    /// <summary>
    /// 执行粉碎 — 四重防护确认流程
    /// L3: 风险警示 + 5秒冷却
    /// 文件清单 + 输入"确认粉碎"
    /// 再次检测文件存在
    /// 执行粉碎
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFiles))]
    private async Task ExecuteShred()
    {
        if (FileItems.Count == 0) return;

        // ===== 第1步：L3 风险警示 + 5秒冷却 =====
        var fileList = string.Join("\n", FileItems.Take(10).Select(f =>
            $"  {f.FileName} ({f.FileSize}){(f.IsOnSSD ? " [SSD]" : "")}"));
        if (FileItems.Count > 10)
            fileList += $"\n  ... 等共 {FileItems.Count} 个文件";

        string l3Message = "⚠️ 危险操作 — 文件粉碎\n\n" +
            "粉碎后的文件将无法通过任何方式恢复！\n\n" +
            "即将粉碎以下文件:\n" + fileList + "\n\n" +
            "SSD 上的文件仅执行 1 次覆写（受限于 SSD 磨损均衡机制）。\n" +
            "请确认您确实要永久销毁这些文件。";

        if (!ConfirmationHelper.RequestL3(l3Message, "确认粉碎"))
        {
            _log.LogInfo("Shredding cancelled by user at L3 confirmation");
            return;
        }

        // ===== 第2步：文件清单 + 输入"确认粉碎" =====
        var confirmList = string.Join("\n", FileItems.Select(f =>
            $"  {f.FileName}"));

        string l3InputMessage = "请核对以下文件清单:\n\n" + confirmList + "\n\n" +
            "请输入 \"确认粉碎\" 以执行最终操作:";

        if (!ConfirmationHelper.RequestL3(l3InputMessage, "确认粉碎"))
        {
            _log.LogInfo("Shredding cancelled at file list confirmation");
            return;
        }

        // ===== 第3步：执行前再次检测文件存在 =====
        var missingFiles = new List<string>();
        foreach (var item in FileItems)
        {
            if (!File.Exists(item.FilePath) && !Directory.Exists(item.FilePath))
            {
                missingFiles.Add(item.FileName);
                item.Status = ShredderItemStatus.Skipped;
                item.OperationLog = "文件已不存在，跳过";
            }
        }

        if (missingFiles.Count > 0)
        {
            string missingMsg = "以下文件已不存在，将跳过:\n" +
                string.Join("\n", missingFiles.Select(f => $"  {f}")) +
                "\n\n是否继续粉碎其余文件？";

            if (MessageBox.Show(missingMsg, "文件不存在", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        // ===== 第4步：执行粉碎 =====
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressMax = 100;
        ProgressValue = 0;

        _cts = new CancellationTokenSource();
        OperationLogs.Clear();

        try
        {
            var pendingItems = FileItems.Where(f => f.Status != ShredderItemStatus.Skipped).ToList();
            int total = pendingItems.Count;
            int completed = 0;
            int successCount = 0;
            int failCount = 0;

            foreach (var item in pendingItems)
            {
                if (_cts.Token.IsCancellationRequested) break;

                ProgressText = $"正在粉碎: {item.FileName}";
                AddLog($"开始粉碎: {item.FileName}");

                var progress = new Progress<int>(p =>
                    ProgressValue = Math.Min(100, (completed * 100 + p) / Math.Max(1, total)));

                var result = await Task.Run(() =>
                    _shredder.ShredFile(item.FilePath, SelectedAlgorithm, progress, _cts.Token),
                    _cts.Token);

                if (result.Success)
                {
                    item.Status = ShredderItemStatus.Success;
                    item.OperationLog = result.OperationLog;
                    AddLog($"  ✅ {result.OperationLog}");
                    successCount++;
                }
                else
                {
                    item.Status = ShredderItemStatus.Failed;
                    item.OperationLog = result.ErrorMessage;
                    AddLog($"  ❌ {result.ErrorMessage}");
                    failCount++;
                }

                completed++;
                ProgressValue = completed * 100 / total;
            }

            ProgressText = $"粉碎完成：成功 {successCount} 项，失败 {failCount} 项";
            AddLog($"===== 粉碎完成 ===== 成功: {successCount}, 失败: {failCount}");
            _log.LogInfo($"Shredding completed: {successCount} succeeded, {failCount} failed");

            MessageBox.Show($"粉碎完成！\n成功: {successCount} 项\n失败: {failCount} 项",
                "操作完成", MessageBoxButton.OK,
                successCount == total ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "操作已取消";
            AddLog("操作已取消");
        }
        catch (Exception ex)
        {
            _log.LogError("Shredding operation failed", ex);
            AddLog($"错误: {ex.Message}");
            MessageBox.Show($"粉碎操作异常: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            UpdateStates();
        }
    }

    /// <summary>
    /// 取消粉碎操作
    /// </summary>
    [RelayCommand]
    private void CancelShred()
    {
        _cts?.Cancel();
        AddLog("用户取消操作...");
    }

    /// <summary>
    /// 清空操作日志
    /// </summary>
    [RelayCommand]
    private void ClearLogs()
    {
        OperationLogs.Clear();
        UpdateStates();
    }

    /// <summary>
    /// 添加操作日志项
    /// </summary>
    private void AddLog(string message)
    {
        OperationLogs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        UpdateStates();
    }

    /// <summary>
    /// 格式化文件大小
    /// </summary>
    private static string FormatFileSize(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return "文件夹";
            }
            else if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                long bytes = fi.Length;
                string[] sizes = { "B", "KB", "MB", "GB", "TB" };
                int order = 0;
                double size = bytes;
                while (size >= 1024 && order < sizes.Length - 1)
                { order++; size /= 1024; }
                return $"{size:0.##} {sizes[order]}";
            }
        }
        catch { }
        return "未知大小";
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
            {
                AddFiles(files);
            }
        }
    }
}
