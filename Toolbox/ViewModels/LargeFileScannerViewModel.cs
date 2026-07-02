using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Native;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 大文件信息模型
/// 表示扫描到的一个大文件
/// </summary>
public partial class LargeFileInfo : ObservableObject
{
    /// <summary>文件名</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>文件完整路径</summary>
    [ObservableProperty]
    private string _fullPath = "";

    /// <summary>所在文件夹路径</summary>
    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? "";

    /// <summary>文件大小（字节）</summary>
    [ObservableProperty]
    private long _size;

    /// <summary>格式化大小</summary>
    public string SizeDisplay => FormatSize(Size);

    /// <summary>最后修改时间</summary>
    [ObservableProperty]
    private DateTime _lastModified;

    /// <summary>格式化日期</summary>
    public string DateDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");

    /// <summary>是否在受保护路径下（不可删除）</summary>
    [ObservableProperty]
    private bool _isProtected;

    /// <summary>是否被用户选中</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 格式化字节数为可读字符串
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }
}

/// <summary>
/// 大文件扫描 ViewModel
/// 支持多磁盘选择、阈值筛选（100MB/1GB/自定义）、按大小降序展示
/// 受保护路径下的文件删除按钮置灰
/// </summary>
public partial class LargeFileScannerViewModel : ViewModelBase
{
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    /// <summary>可用磁盘列表</summary>
    public ObservableCollection<DiskModel> Disks { get; } = new();

    /// <summary>扫描到的大文件列表</summary>
    public ObservableCollection<LargeFileInfo> Files { get; } = new();

    /// <summary>显示的阈值选项列表</summary>
    public string[] ThresholdOptions { get; } = { "100 MB", "500 MB", "1 GB", "5 GB", "10 GB", "自定义" };

    /// <summary>当前选中的阈值索引</summary>
    [ObservableProperty]
    private int _selectedThresholdIndex;

    /// <summary>自定义阈值（MB），当选择"自定义"时使用</summary>
    [ObservableProperty]
    private double _customThresholdMB = 100;

    /// <summary>自定义阈值是否可见</summary>
    [ObservableProperty]
    private bool _isCustomThresholdVisible;

    /// <summary>是否正在扫描</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>扫描到的文件总数量</summary>
    [ObservableProperty]
    private int _totalFileCount;

    /// <summary>扫描到的文件总大小（格式化）</summary>
    [ObservableProperty]
    private string _totalSizeDisplay = "0 B";

    /// <summary>选中文件总大小（格式化）</summary>
    [ObservableProperty]
    private string _selectedSizeDisplay = "0 B";

    /// <summary>
    /// 构造函数，通过 DI 注入日志服务
    /// </summary>
    public LargeFileScannerViewModel(ILogService log)
    {
        _log = log;
        LoadDisks();
        SelectedThresholdIndex = 0; // 默认 100MB
    }

    /// <summary>
    /// 加载系统所有固定磁盘
    /// </summary>
    private void LoadDisks()
    {
        Disks.Clear();
        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                try
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    {
                        Disks.Add(new DiskModel
                        {
                            Name = drive.Name,
                            Label = string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel,
                            TotalSize = drive.TotalSize,
                            AvailableSize = drive.AvailableFreeSpace,
                            UsedSize = drive.TotalSize - drive.AvailableFreeSpace,
                            IsSelected = false
                        });
                    }
                }
                catch { }
            }
            _log.LogInfo($"加载了 {Disks.Count} 个磁盘供大文件扫描");
        }
        catch (Exception ex)
        {
            _log.LogError("加载磁盘列表失败", ex);
        }
    }

    /// <summary>
    /// 阈值选择变化时处理
    /// "自定义"选项时显示自定义输入框
    /// </summary>
    partial void OnSelectedThresholdIndexChanged(int value)
    {
        IsCustomThresholdVisible = value == ThresholdOptions.Length - 1;
    }

    /// <summary>
    /// 获取当前选中的阈值（字节）
    /// </summary>
    private long GetSelectedThresholdBytes()
    {
        return SelectedThresholdIndex switch
        {
            0 => 100L * 1024 * 1024,           // 100 MB
            1 => 500L * 1024 * 1024,           // 500 MB
            2 => 1L * 1024 * 1024 * 1024,      // 1 GB
            3 => 5L * 1024 * 1024 * 1024,      // 5 GB
            4 => 10L * 1024 * 1024 * 1024,     // 10 GB
            5 => (long)(CustomThresholdMB * 1024 * 1024), // 自定义 (MB)
            _ => 100L * 1024 * 1024
        };
    }

    /// <summary>
    /// 开始扫描大文件命令
    /// 扫描选中的磁盘，查找大于阈值的文件
    /// 结果按大小降序排列
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        var selectedDisks = Disks.Where(d => d.IsSelected).ToList();
        if (!selectedDisks.Any())
        {
            StatusMessage = "请先选择要扫描的磁盘";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thresholdBytes = GetSelectedThresholdBytes();

        IsScanning = true;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Files.Clear();
        StatusMessage = $"正在扫描大于 {LargeFileInfo.FormatSize(thresholdBytes)} 的文件...";

        try
        {
            var foundFiles = new List<LargeFileInfo>();

            await Task.Run(() =>
            {
                foreach (var disk in selectedDisks)
                {
                    token.ThrowIfCancellationRequested();

                    ScanDirectory(disk.Name, thresholdBytes, foundFiles, token, disk.Name);
                }

                // 按大小降序排列
                foundFiles.Sort((a, b) => b.Size.CompareTo(a.Size));

            }, token);

            // 更新 UI
            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var file in foundFiles)
                    Files.Add(file);

                TotalFileCount = Files.Count;
                TotalSizeDisplay = LargeFileInfo.FormatSize(Files.Sum(f => f.Size));
                IsProgressIndeterminate = false;
                StatusMessage = $"扫描完成，找到 {TotalFileCount} 个大文件，总计 {TotalSizeDisplay}";
            });

            _log.LogInfo($"大文件扫描完成: {Files.Count} 个文件, 总计 {TotalSizeDisplay}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
            IsProgressIndeterminate = false;
            _log.LogInfo("大文件扫描被取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
            IsProgressIndeterminate = false;
            _log.LogError("大文件扫描失败", ex);
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 递归扫描目录，查找大于阈值的大文件
    /// 跳过符号链接、受保护路径中的受保护扩展名
    /// </summary>
    private void ScanDirectory(
        string path, long thresholdBytes,
        List<LargeFileInfo> foundFiles,
        CancellationToken token,
        string rootPath)
    {
        try
        {
            // 流式枚举文件
            var files = Directory.EnumerateFiles(
                path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                });

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var fileInfo = new FileInfo(file);

                    // 跳过符号链接
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    // 跳过小于阈值的文件
                    if (fileInfo.Length < thresholdBytes)
                        continue;

                    // 跳过受保护扩展名
                    if (Helpers.ProtectedPaths.IsProtectedExtension(file))
                        continue;

                    var isProtected = Helpers.ProtectedPaths.IsProtectedPath(file);

                    foundFiles.Add(new LargeFileInfo
                    {
                        Name = fileInfo.Name,
                        FullPath = file,
                        Size = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        IsProtected = isProtected,
                        IsSelected = false
                    });
                }
                catch
                {
                    // 跳过无法访问的文件
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _log.LogWarning($"无权访问目录: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            _log.LogWarning($"目录不存在: {path}");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"扫描目录 {path} 时出错: {ex.Message}");
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
    /// 删除选中的文件
    /// 受保护路径下的文件不可删除
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selectedFiles = Files.Where(f => f.IsSelected && !f.IsProtected).ToList();
        if (!selectedFiles.Any())
        {
            StatusMessage = "没有可删除的文件（受保护文件已排除）";
            return;
        }

        // L1 确认
        var confirmMsg = string.Join("\n", selectedFiles.Take(5).Select(f => $"  - {f.Name} ({f.SizeDisplay})"));
        if (selectedFiles.Count > 5)
            confirmMsg += $"\n  ... 还有 {selectedFiles.Count - 5} 个文件";
        if (!Helpers.ConfirmationHelper.RequestL1($"确认删除以下 {selectedFiles.Count} 个文件？\n\n{confirmMsg}"))
            return;

        IsBusy = true;
        StatusMessage = "正在删除文件...";

        int deleted = 0;
        int failed = 0;

        await Task.Run(() =>
        {
            foreach (var file in selectedFiles)
            {
                try
                {
                    // 再次确认不受保护
                    if (Helpers.ProtectedPaths.IsProtectedPath(file.FullPath))
                        continue;

                    var fileInfo = new FileInfo(file.FullPath);
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                        fileInfo.Attributes &= ~FileAttributes.ReadOnly;

                    File.Delete(file.FullPath);
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }
        });

        // 从列表中移除已删除的文件
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var file in selectedFiles.Where(f => File.Exists(f.FullPath) == false).ToList())
                Files.Remove(file);

            TotalFileCount = Files.Count;
            TotalSizeDisplay = LargeFileInfo.FormatSize(Files.Sum(f => f.Size));
        });

        IsBusy = false;
        StatusMessage = $"删除完成: {deleted} 成功, {failed} 失败";
        _log.LogOperation("largefile", "delete", $"删除 {deleted} 个大文件, 失败 {failed}");
    }

    /// <summary>
    /// 在文件资源管理器中打开选中的文件
    /// 如果选中多个，打开第一个所在的文件夹
    /// </summary>
    [RelayCommand]
    private void OpenFileLocation(LargeFileInfo? file)
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

    /// <summary>
    /// 文件选中状态变化时的处理
    /// </summary>
    public void OnFileSelectionChanged()
    {
        var selectedFiles = Files.Where(f => f.IsSelected).ToList();
        SelectedSizeDisplay = LargeFileInfo.FormatSize(selectedFiles.Sum(f => f.Size));
    }
}
