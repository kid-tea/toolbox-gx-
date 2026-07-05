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
/// 文件强制删除功能的 ViewModel
/// 提供拖拽添加、浏览添加、批量删除、L1确认、结果中英文反馈等功能
/// </summary>
public partial class ForceDeleteViewModel : ViewModelBase
{
    private readonly ILogService _log;
    private readonly IFileUnlockService _fileUnlock;

    /// <summary>文件删除项列表（用于 UI 绑定）</summary>
    public ObservableCollection<FileDeleteItem> FileItems { get; } = new();

    /// <summary>
    /// 是否已选择文件（用于启用/禁用删除按钮）
    /// </summary>
    [NotifyCanExecuteChangedFor(nameof(ExecuteDeleteCommand))]
    [ObservableProperty]
    private bool _hasFiles;

    /// <summary>
    /// 当前操作的进度文本
    /// </summary>
    [ObservableProperty]
    private string _progressText = "就绪";

    /// <summary>
    /// 构造函数，通过 DI 注入日志和文件解锁服务
    /// </summary>
    public ForceDeleteViewModel(ILogService log, IFileUnlockService fileUnlock)
    {
        _log = log;
        _fileUnlock = fileUnlock;
    }

    /// <summary>
    /// 当文件列表变化时更新 HasFiles 状态
    /// </summary>
    private void UpdateHasFiles()
    {
        HasFiles = FileItems.Count > 0;
    }

    /// <summary>
    /// 从剪贴板或参数添加文件路径到列表
    /// 支持批量添加，自动去重
    /// </summary>
    /// <param name="paths">文件/目录路径列表</param>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var normalizedPath = path.Trim('"'); // 去掉拖拽时可能带的引号

            // 去重：已存在则跳过
            if (FileItems.Any(f => string.Equals(f.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            // 判断文件类型
            bool isDir = Directory.Exists(normalizedPath);
            bool isFile = File.Exists(normalizedPath);
            if (!isDir && !isFile) continue;

            // 创建文件删除项
            var item = new FileDeleteItem
            {
                FilePath = normalizedPath,
                FileName = Path.GetFileName(normalizedPath),
                FileSize = FormatFileSize(normalizedPath, isDir),
                IsShortcut = normalizedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
                Status = DeleteItemStatus.Pending
            };

            // 检查是否位于系统受保护路径
            if (ProtectedPaths.IsProtectedPath(normalizedPath))
            {
                item.IsProtected = true;
            }

            // 如果是快捷方式，解析目标
            if (item.IsShortcut)
            {
                item.ShortcutTarget = _fileUnlock.ResolveShortcutTarget(normalizedPath);
            }

            FileItems.Add(item);
        }
        UpdateHasFiles();
        _log.LogInfo($"Added {paths.Count()} files to force-delete list, total: {FileItems.Count}");
    }

    /// <summary>
    /// 浏览添加文件（打开文件选择对话框）
    /// </summary>
    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要强制删除的文件",
            Multiselect = true,
            Filter = "所有文件|*.*|快捷方式|*.lnk"
        };

        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    /// <summary>
    /// 浏览添加文件夹（打开文件夹选择对话框）
    /// </summary>
    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要强制删除的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            AddFiles(new[] { dialog.FolderName });
        }
    }

    /// <summary>
    /// 从文件列表中移除指定项
    /// </summary>
    [RelayCommand]
    private void RemoveFile(FileDeleteItem? item)
    {
        if (item == null) return;
        FileItems.Remove(item);
        UpdateHasFiles();
    }

    /// <summary>
    /// 清空整个文件列表
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        FileItems.Clear();
        UpdateHasFiles();
        ProgressText = "就绪";
    }

    /// <summary>
    /// 执行强制删除操作
    /// 流程：L1确认 → 检查.lnk文件 → Enum占用进程 → 终止 → 删除 → 结果反馈
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFiles))]
    private async Task ExecuteDelete()
    {
        if (FileItems.Count == 0) return;

        // L1 级别确认
        var fileNames = FileItems.Take(5).Select(f => f.FileName);
        string nameList = string.Join("\n", fileNames);
        if (FileItems.Count > 5) nameList += $"\n... 等共 {FileItems.Count} 个文件/文件夹";

        string confirmMessage = $"确认强制删除以下文件/文件夹？\n\n{nameList}\n\n" +
            $"强制删除会尝试终止占用进程，可能导致正在使用这些文件的程序关闭。";
        string confirmMessageEn = $"Confirm force delete?\n\n{nameList}\n\n" +
            $"Force delete will attempt to terminate locking processes.";

        if (!ConfirmationHelper.RequestL1(confirmMessage))
            return;

        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressMax = FileItems.Count;
        ProgressValue = 0;

        try
        {
            // 处理 .lnk 快捷方式（询问删快捷方式还是目标）
            await HandleShortcuts();

            // 收集所有待处理路径（包括快捷方式目标和文件夹内文件）
            var allPaths = ResolveAllPaths();

            int total = allPaths.Count;
            int processed = 0;

            foreach (var item in FileItems)
            {
                ProgressText = $"正在删除: {item.FileName}";
                _log.LogInfo($"Force deleting: {item.FilePath}");

                // 文件夹递归删除（已在 ResolveAllPaths 和 FileUnlockService 中处理）
                var result = _fileUnlock.ForceDelete(item.FilePath);

                if (result.Success)
                {
                    item.Status = DeleteItemStatus.Success;
                    item.ResultIcon = "✅";
                    item.ErrorMessage = "";
                    item.ErrorMessageEn = "";
                    _log.LogOperation("forcedelete", "deleted", item.FilePath);
                }
                else
                {
                    item.Status = DeleteItemStatus.Failed;
                    item.ResultIcon = "❌";
                    item.ErrorMessage = result.ErrorMessage;
                    item.ErrorMessageEn = result.ErrorMessageEn;
                    item.LockingProcessNames = string.Join(", ",
                        result.LockingProcesses.Select(p =>
                            $"{p.ProcessName}{(p.IsNonTerminable ? "(系统关键进程)" : "")}"));
                    _log.LogWarning($"Failed to delete: {item.FilePath}, reason: {result.ErrorMessage}");
                }

                processed++;
                ProgressValue = processed * 100 / Math.Max(1, FileItems.Count);
            }

            int successCount = FileItems.Count(f => f.Status == DeleteItemStatus.Success);
            int failCount = FileItems.Count(f => f.Status == DeleteItemStatus.Failed);

            ProgressText = $"删除完成：成功 {successCount} 项，失败 {failCount} 项";
            _log.LogInfo($"Force delete completed: {successCount} succeeded, {failCount} failed");
        }
        catch (Exception ex)
        {
            _log.LogError("Force delete operation failed", ex);
            MessageBox.Show($"删除操作异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// 处理快捷方式(.lnk)文件的确认
    /// 询问用户是删除快捷方式本身还是删除其指向的目标文件
    /// </summary>
    private Task HandleShortcuts()
    {
        var shortcutItems = FileItems.Where(f => f.IsShortcut && !string.IsNullOrEmpty(f.ShortcutTarget)).ToList();
        if (shortcutItems.Count == 0) return Task.CompletedTask;

        foreach (var item in shortcutItems)
        {
            string msg = $"检测到快捷方式:\n" +
                $"  快捷方式: {item.FileName}\n" +
                $"  目标文件: {item.ShortcutTarget}\n\n" +
                $"请选择要删除的对象:";
            string msgEn = $"Shortcut detected:\n" +
                $"  Shortcut: {item.FileName}\n" +
                $"  Target: {item.ShortcutTarget}\n\n" +
                $"Choose what to delete:";

            var result = MessageBox.Show(
                $"{msg}\n\n[是(Y)] = 删除快捷方式本身\n[否(N)] = 删除目标文件\n[取消] = 跳过此文件",
                "快捷方式处理",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // 删除快捷方式本身，保持原路径
                item.ShortcutTarget = ""; // 清除目标，不再询问
            }
            else if (result == MessageBoxResult.No)
            {
                // 删除目标文件，替换路径
                if (File.Exists(item.ShortcutTarget))
                {
                    item.FilePath = item.ShortcutTarget;
                    item.FileName = Path.GetFileName(item.ShortcutTarget);
                    item.FileSize = FormatFileSize(item.ShortcutTarget, Directory.Exists(item.ShortcutTarget));
                    item.IsShortcut = false;
                    item.ShortcutTarget = "";
                }
                else
                {
                    // 目标不存在，设置为失败
                    item.Status = DeleteItemStatus.Failed;
                    item.ResultIcon = "❌";
                    item.ErrorMessage = $"快捷方式目标不存在: {item.ShortcutTarget}";
                    item.ErrorMessageEn = $"Shortcut target does not exist: {item.ShortcutTarget}";
                }
            }
            else
            {
                // 跳过
                item.Status = DeleteItemStatus.Failed;
                item.ResultIcon = "⏭️";
                item.ErrorMessage = "用户跳过";
                item.ErrorMessageEn = "Skipped by user";
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 解析所有待删除路径
    /// 对于目录，将目录本身加入删除列表（FileUnlockService 会递归删除）
    /// </summary>
    private List<string> ResolveAllPaths()
    {
        var paths = new List<string>();
        foreach (var item in FileItems.Where(f => f.Status != DeleteItemStatus.Failed))
        {
            paths.Add(item.FilePath);
        }
        return paths;
    }

    /// <summary>
    /// 格式化文件/文件夹大小学友好显示
    /// </summary>
    /// <param name="path">路径</param>
    /// <param name="isDirectory">是否为目录</param>
    /// <returns>格式化后的大小字符串，如 "1.5 MB" 或 "目录"</returns>
    private static string FormatFileSize(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                // 计算目录总大小（仅第一层，避免耗时过长）
                var dirInfo = new DirectoryInfo(path);
                long size = 0;
                try
                {
                    foreach (var file in dirInfo.GetFiles("*", SearchOption.TopDirectoryOnly))
                        size += file.Length;
                }
                catch { /* 无法访问时忽略 */ }
                return $"文件夹 ({FormatBytes(size)})";
            }
            else
            {
                var fileInfo = new FileInfo(path);
                return FormatBytes(fileInfo.Length);
            }
        }
        catch
        {
            return isDirectory ? "文件夹" : "未知大小";
        }
    }

    /// <summary>
    /// 将字节数格式化为人类可读的字符串
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// 处理拖放文件（从资源管理器拖入）
    /// </summary>
    /// <param name="dropData">拖放数据对象</param>
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
