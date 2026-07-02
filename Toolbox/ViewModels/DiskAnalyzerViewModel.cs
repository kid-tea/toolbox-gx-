using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 磁盘模型 — 表示一个可扫描的磁盘驱动器
/// </summary>
public partial class DiskModel : ObservableObject
{
    /// <summary>驱动器名称，如 "C:\"</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>驱动器标签，如 "系统盘"</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>总容量（字节）</summary>
    [ObservableProperty]
    private long _totalSize;

    /// <summary>可用空间（字节）</summary>
    [ObservableProperty]
    private long _availableSize;

    /// <summary>已用空间（字节）</summary>
    [ObservableProperty]
    private long _usedSize;

    /// <summary>用户是否选中此磁盘进行扫描</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>格式化总容量</summary>
    public string TotalSizeDisplay => DiskFolderNode.FormatSize(TotalSize);

    /// <summary>格式化可用空间</summary>
    public string AvailableSizeDisplay => DiskFolderNode.FormatSize(AvailableSize);

    /// <summary>格式化已用空间</summary>
    public string UsedSizeDisplay => DiskFolderNode.FormatSize(UsedSize);

    /// <summary>使用率百分比</summary>
    public double UsagePercent => TotalSize > 0 ? (double)UsedSize / TotalSize * 100 : 0;
}

/// <summary>
/// 磁盘空间分析 ViewModel
/// 支持多磁盘选择、标签页切换、树形目录展示、饼图数据
/// </summary>
public partial class DiskAnalyzerViewModel : ViewModelBase
{
    private readonly IDiskAnalyzerService _service;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    /// <summary>可用磁盘列表（多选）</summary>
    public ObservableCollection<DiskModel> Disks { get; } = new();

    /// <summary>所有已扫描磁盘的根节点（用于标签页切换）</summary>
    public ObservableCollection<DiskFolderNode> ScannedDisks { get; } = new();

    /// <summary>当前选中的标签页（磁盘根节点）</summary>
    [ObservableProperty]
    private DiskFolderNode? _selectedDisk;

    /// <summary>当前标签页下的饼图数据（空间分布列表）</summary>
    [ObservableProperty]
    private ObservableCollection<DiskFolderNode> _pieData = new();

    /// <summary>是否正在扫描</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>
    /// 构造函数，通过 DI 注入分析服务和日志服务
    /// </summary>
    public DiskAnalyzerViewModel(IDiskAnalyzerService service, ILogService log)
    {
        _service = service;
        _log = log;
        StatusMessage = "就绪 — 先勾选左侧磁盘，再点击「开始扫描」";
        LoadDisks();
    }

    /// <summary>
    /// 加载系统所有固定磁盘
    /// </summary>
    private void LoadDisks()
    {
        Disks.Clear();
        try
        {
            var drives = _service.GetFixedDrives();
            foreach (var drive in drives)
            {
                var model = new DiskModel
                {
                    Name = drive.Name,
                    Label = string.IsNullOrEmpty(drive.VolumeLabel)
                        ? "本地磁盘"
                        : drive.VolumeLabel,
                    TotalSize = drive.TotalSize,
                    AvailableSize = drive.AvailableFreeSpace,
                    UsedSize = drive.TotalSize - drive.AvailableFreeSpace,
                    IsSelected = false
                };
                Disks.Add(model);
            }
            _log.LogInfo($"加载了 {Disks.Count} 个磁盘驱动器");
        }
        catch (Exception ex)
        {
            _log.LogError("加载磁盘列表失败", ex);
        }
    }

    /// <summary>
    /// 开始扫描选中的磁盘
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        var selectedDisks = Disks.Where(d => d.IsSelected).ToList();
        if (!selectedDisks.Any())
        {
            StatusMessage = "请先选择要分析的磁盘";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsScanning = true;
        IsBusy = true;
        IsProgressIndeterminate = true;
        ScannedDisks.Clear();
        ProgressValue = 0;
        ProgressMax = selectedDisks.Count;

        try
        {
            foreach (var disk in selectedDisks)
            {
                token.ThrowIfCancellationRequested();
                StatusMessage = $"正在分析 {disk.Name} ...";

                var progress = new Progress<string>(msg =>
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusMessage = msg;
                    });
                });

                var rootNode = await _service.ScanDiskAsync(disk.Name, progress, token);
                if (rootNode == null)
                    continue;

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    rootNode.Name = $"{disk.Label} ({disk.Name})";
                    ScannedDisks.Add(rootNode);
                    ProgressValue++;

                    if (ScannedDisks.Count == 1)
                        SelectedDisk = rootNode;
                });
            }

            IsProgressIndeterminate = false;
            ProgressValue = ProgressMax;
            StatusMessage = ScannedDisks.Count > 0
                ? $"分析完成，共扫描 {ScannedDisks.Count} 个磁盘"
                : "分析完成，但没有可显示的数据";
            _log.LogInfo($"磁盘分析完成: {ScannedDisks.Count} 个磁盘");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
            IsProgressIndeterminate = false;
            _log.LogInfo("磁盘分析被取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
            IsProgressIndeterminate = false;
            _log.LogError("磁盘分析失败", ex);
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 取消当前扫描
    /// </summary>
    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
        IsScanning = false;
        IsBusy = false;
        IsProgressIndeterminate = false;
        StatusMessage = "扫描已取消";
        _log.LogInfo("用户取消了磁盘分析");
    }

    /// <summary>
    /// TreeView 安全绑定用 — 当 SelectedDisk 为 null 时返回空集合避免 NullReferenceException
    /// </summary>
    public ObservableCollection<object> SafeDiskChildren =>
        SelectedDisk?.Children ?? new ObservableCollection<object>();

    /// <summary>
    /// 当选中磁盘变化时，更新饼图数据和 OxyPlot 模型
    /// 取当前根节点的前 N 个子节点作为饼图切片
    /// </summary>
    partial void OnSelectedDiskChanged(DiskFolderNode? value)
    {
        OnPropertyChanged(nameof(SafeDiskChildren));
        PieData.Clear();

        if (value == null || value.TotalSize <= 0)
            return;

        const int maxSlices = 8;
        var topChildren = value.ChildFolders
            .Where(c => c.TotalSize > 0)
            .Take(maxSlices)
            .ToList();
        long accountedSize = 0;

        foreach (var child in topChildren)
        {
            PieData.Add(new DiskFolderNode
            {
                Name = child.Name,
                TotalSize = child.TotalSize,
                Percentage = child.Percentage
            });
            accountedSize += child.TotalSize;
        }

        var remaining = value.TotalSize - accountedSize;
        if (remaining > 0)
        {
            PieData.Add(new DiskFolderNode
            {
                Name = "其他",
                TotalSize = remaining,
                Percentage = (double)remaining / value.TotalSize * 100
            });
        }

        if (PieData.Count > 0)
            _log.LogInfo($"切换到磁盘: {value.Name}, 空间分布: {PieData.Count} 项");
    }

    /// <summary>
    /// 获取选中磁盘的子目录列表（按大小降序）
    /// </summary>
    public IEnumerable<DiskFolderNode> GetFolderTree(DiskFolderNode root)
    {
        return root.ChildFolders.OrderByDescending(c => c.TotalSize);
    }

    /// <summary>
    /// 获取空间分布数据列表
    /// </summary>
    public ObservableCollection<DiskFolderNode> GetPieChartData()
    {
        return PieData;
    }

    /// <summary>
    /// 切换标签页到指定磁盘
    /// </summary>
    [RelayCommand]
    private void SelectDiskTab(DiskFolderNode? disk)
    {
        if (disk != null)
            SelectedDisk = disk;
    }
}
