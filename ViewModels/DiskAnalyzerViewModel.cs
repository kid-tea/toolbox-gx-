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
/// 图表数据项 — 带颜色和交互状态
/// </summary>
public partial class ChartItem : ObservableObject
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long TotalSize { get; set; }
    public double Percentage { get; set; }
    public string SizeDisplay => DiskFolderNode.FormatSize(TotalSize);
    public string PercentageDisplay => $"{Percentage:0.#}%";

    [ObservableProperty] private bool _isHovered;

    /// <summary>颜色索引（0-7）</summary>
    public int ColorIndex { get; set; }
    /// <summary>是否为"其他"汇总项（不可钻取）</summary>
    public bool IsOther { get; set; }
    /// <summary>原始文件夹节点（用于钻取）</summary>
    public DiskFolderNode? SourceFolder { get; set; }
}

/// <summary>
/// 磁盘空间分析 ViewModel
/// 支持多磁盘选择、标签页切换、树形目录展示、柱状图+饼图可视化与钻取
/// </summary>
public partial class DiskAnalyzerViewModel : ViewModelBase
{
    private readonly IDiskAnalyzerService _service;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    // 图表用 8 色调色板（现代柔和色系）
    private static readonly string[] ChartColors =
        { "#5B9BD5", "#6AAF6F", "#ED7D31", "#C0504D", "#9B59B6", "#E67E80", "#3BAFDA", "#F6C043" };

    /// <summary>可用磁盘列表（多选）</summary>
    public ObservableCollection<DiskModel> Disks { get; } = new();

    /// <summary>所有已扫描磁盘的根节点（用于标签页切换）</summary>
    public ObservableCollection<DiskFolderNode> ScannedDisks { get; } = new();

    /// <summary>当前选中的标签页（磁盘根节点）</summary>
    [ObservableProperty]
    private DiskFolderNode? _selectedDisk;

    /// <summary>图表数据（驱动柱状图和饼图）</summary>
    [ObservableProperty]
    private ObservableCollection<ChartItem> _chartData = new();

    /// <summary>图表是否可见</summary>
    [ObservableProperty]
    private bool _hasChartData;

    /// <summary>当前钻取层级的根节点</summary>
    [ObservableProperty]
    private DiskFolderNode? _chartRoot;

    /// <summary>面包屑路径</summary>
    [ObservableProperty]
    private string _breadcrumbText = "";

    /// <summary>是否可返回上级</summary>
    [ObservableProperty]
    private bool _canNavigateUp;

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
    /// 当选中磁盘变化时，重置钻取状态并渲染图表
    /// </summary>
    partial void OnSelectedDiskChanged(DiskFolderNode? value)
    {
        OnPropertyChanged(nameof(SafeDiskChildren));
        if (value != null)
            NavigateToFolder(value);
    }

    /// <summary>
    /// 钻取到指定目录，更新图表和面包屑
    /// </summary>
    private void NavigateToFolder(DiskFolderNode folder)
    {
        ChartRoot = folder;
        BuildChartData(folder);
        BreadcrumbText = folder.FullPath;
        CanNavigateUp = folder.Depth > 0;
    }

    /// <summary>
    /// 从指定目录构建图表数据
    /// </summary>
    private void BuildChartData(DiskFolderNode root)
    {
        ChartData.Clear();
        if (root == null || root.TotalSize <= 0)
        {
            HasChartData = false;
            return;
        }

        const int maxItems = 8;
        var folders = root.ChildFolders
            .Where(c => c.TotalSize > 0)
            .Take(maxItems)
            .ToList();

        long accounted = 0;
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            ChartData.Add(new ChartItem
            {
                Name = f.Name,
                FullPath = f.FullPath,
                TotalSize = f.TotalSize,
                Percentage = f.Percentage,
                ColorIndex = i % ChartColors.Length,
                SourceFolder = f
            });
            accounted += f.TotalSize;
        }

        var remaining = root.TotalSize - accounted;
        if (remaining > 0)
        {
            ChartData.Add(new ChartItem
            {
                Name = "其他",
                TotalSize = remaining,
                Percentage = (double)remaining / root.TotalSize * 100,
                ColorIndex = folders.Count % ChartColors.Length,
                IsOther = true
            });
        }

        HasChartData = ChartData.Count > 0;
        OnPropertyChanged(nameof(ChartData));
        _log.LogInfo($"图表数据: {ChartData.Count} 项, 根={root.Name}");
    }

    /// <summary>
    /// 返回上级目录
    /// </summary>
    [RelayCommand]
    private void NavigateUp()
    {
        if (SelectedDisk == null || ChartRoot == null) return;

        // 在选中磁盘的树中查找 ChartRoot 的父节点
        var parent = FindParent(SelectedDisk, ChartRoot);
        if (parent != null)
            NavigateToFolder(parent);
        else
            NavigateToFolder(SelectedDisk); // 回到根
    }

    private static DiskFolderNode? FindParent(DiskFolderNode root, DiskFolderNode target)
    {
        foreach (var child in root.ChildFolders)
        {
            if (child == target) return root;
            var found = FindParent(child, target);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 钻取到指定图表项对应的文件夹
    /// </summary>
    public void DrillDown(ChartItem item)
    {
        if (item.IsOther || item.SourceFolder == null) return;
        NavigateToFolder(item.SourceFolder);
    }

    /// <summary>
    /// 获取选中磁盘的子目录列表（按大小降序）
    /// </summary>
    public IEnumerable<DiskFolderNode> GetFolderTree(DiskFolderNode root)
    {
        return root.ChildFolders.OrderByDescending(c => c.TotalSize);
    }

    /// <summary>
    /// 获取指定颜色索引对应的画刷
    /// </summary>
    public static System.Windows.Media.SolidColorBrush GetChartBrush(int colorIndex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            ChartColors[colorIndex % ChartColors.Length]);
        return new System.Windows.Media.SolidColorBrush(c);
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
