using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 截图模式枚举
/// </summary>
public enum ScreenshotMode
{
    /// <summary>全屏截图 — 捕获所有显示器</summary>
    FullScreen,
    /// <summary>区域截图 — 鼠标拖拽选择区域</summary>
    Region,
    /// <summary>窗口截图 — 选择特定窗口</summary>
    Window,
    /// <summary>延迟截图 — N秒后自动截图</summary>
    Delayed
}

/// <summary>
/// 标注工具类型枚举
/// </summary>
public enum AnnotationTool
{
    /// <summary>选择/移动工具</summary>
    Select,
    /// <summary>矩形框工具</summary>
    Rectangle,
    /// <summary>箭头工具</summary>
    Arrow,
    /// <summary>文字工具</summary>
    Text,
    /// <summary>马赛克工具</summary>
    Mosaic
}

/// <summary>
/// 标注项数据模型
/// </summary>
public partial class AnnotationItem : ObservableObject
{
    /// <summary>标注类型</summary>
    [ObservableProperty]
    private AnnotationTool _type;

    /// <summary>左上角 X 坐标（相对于截图左上角）</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>左上角 Y 坐标（相对于截图左上角）</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>宽度</summary>
    [ObservableProperty]
    private double _width;

    /// <summary>高度</summary>
    [ObservableProperty]
    private double _height;

    /// <summary>标注颜色</summary>
    [ObservableProperty]
    private Color _color = Colors.Red;

    /// <summary>文字内容（文字工具使用）</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>线条粗细</summary>
    [ObservableProperty]
    private double _strokeThickness = 2;

    /// <summary>是否被选中</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>箭头终点 X（相对于截图左上角）</summary>
    [ObservableProperty]
    private double _endX;

    /// <summary>箭头终点 Y（相对于截图左上角）</summary>
    [ObservableProperty]
    private double _endY;

    /// <summary>
    /// 创建标注项的深拷贝
    /// </summary>
    public AnnotationItem Clone() => new()
    {
        Type = Type,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Color = Color,
        Text = Text,
        StrokeThickness = StrokeThickness,
        IsSelected = IsSelected,
        EndX = EndX,
        EndY = EndY
    };
}

/// <summary>
/// 截屏 ViewModel
/// 管理截图模式切换、标注状态（当前工具、撤销栈20步）、保存/复制/放弃
/// </summary>
public partial class ScreenshotViewModel : ViewModelBase
{
    private readonly ScreenshotService _screenshotService;
    private readonly IConfigService _config;
    private readonly ILogService _log;

    // ==================== 截图模式 ====================

    /// <summary>当前截图模式</summary>
    [ObservableProperty]
    private ScreenshotMode _currentMode = ScreenshotMode.FullScreen;

    /// <summary>延迟截图秒数（仅 Delayed 模式）</summary>
    [ObservableProperty]
    private int _delaySeconds = 3;

    /// <summary>是否正在倒计时</summary>
    [ObservableProperty]
    private bool _isCountingDown;

    /// <summary>倒计时剩余秒数</summary>
    [ObservableProperty]
    private int _countdownRemaining;

    // ==================== 截图结果 ====================

    /// <summary>捕获的截图（原始，不含标注）</summary>
    [ObservableProperty]
    private BitmapSource? _capturedImage;

    /// <summary>截图文件路径（保存后设置）</summary>
    [ObservableProperty]
    private string? _savedFilePath;

    /// <summary>是否有截图结果</summary>
    [ObservableProperty]
    private bool _hasResult;

    // ==================== 标注状态 ====================

    /// <summary>所有标注项的集合</summary>
    [ObservableProperty]
    private ObservableCollection<AnnotationItem> _annotations = new();

    /// <summary>当前选中的标注工具</summary>
    [ObservableProperty]
    private AnnotationTool _currentTool = AnnotationTool.Select;

    /// <summary>当前标注颜色</summary>
    [ObservableProperty]
    private Color _annotationColor = Colors.Red;

    /// <summary>当前文字输入内容</summary>
    [ObservableProperty]
    private string _annotationText = "标注文字";

    /// <summary>撤销栈（最多 20 步）</summary>
    private readonly Stack<List<AnnotationItem>> _undoStack = new();

    /// <summary>重做栈</summary>
    private readonly Stack<List<AnnotationItem>> _redoStack = new();

    /// <summary>是否可撤销</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>是否可重做</summary>
    [ObservableProperty]
    private bool _canRedo;

    /// <summary>总标注数量</summary>
    [ObservableProperty]
    private int _annotationCount;

    // ==================== 窗口模式特有 ====================

    /// <summary>可用窗口列表（窗口截图模式）</summary>
    [ObservableProperty]
    private ObservableCollection<WindowItem> _availableWindows = new();

    /// <summary>选中的窗口</summary>
    [ObservableProperty]
    private WindowItem? _selectedWindow;

    private CancellationTokenSource? _delayCts;

    /// <summary>
    /// 窗口列表项
    /// </summary>
    public class WindowItem
    {
        /// <summary>窗口句柄</summary>
        public IntPtr Handle { get; set; }
        /// <summary>窗口标题</summary>
        public string Title { get; set; } = "";
        /// <summary>显示用的标题（含句柄前缀）</summary>
        public string DisplayText => $"{Handle.ToString("X8")} - {Title}";
    }

    /// <summary>
    /// 构造函数 — 依赖注入服务
    /// </summary>
    public ScreenshotViewModel(ScreenshotService screenshotService, IConfigService config, ILogService log)
    {
        _screenshotService = screenshotService;
        _config = config;
        _log = log;
        StatusMessage = "就绪 — 选择截屏模式后点击「截屏」";
    }

    /// <summary>
    /// 切换截图模式
    /// </summary>
    [RelayCommand]
    private void SetMode(string modeName)
    {
        CurrentMode = modeName switch
        {
            "FullScreen" => ScreenshotMode.FullScreen,
            "Region" => ScreenshotMode.Region,
            "Window" => ScreenshotMode.Window,
            "Delayed" => ScreenshotMode.Delayed,
            _ => ScreenshotMode.FullScreen
        };

        if (CurrentMode == ScreenshotMode.Window)
            RefreshWindowList();
        else
            AvailableWindows.Clear();

        StatusMessage = $"当前模式: {modeName}";
        _log.LogInfo($"截图模式切换: {modeName}");
    }

    /// <summary>
    /// 执行截图
    /// </summary>
    [RelayCommand]
    private async Task CaptureAsync()
    {
        try
        {
            IsBusy = true;
            HasResult = false;
            ClearAnnotations();

            if (CurrentMode == ScreenshotMode.Delayed)
            {
                await CaptureDelayedAsync();
            }
            else if (CurrentMode == ScreenshotMode.Region)
            {
                // 区域模式：最小化主窗口，让用户选择区域
                await CaptureRegionAsync();
            }
            else if (CurrentMode == ScreenshotMode.Window)
            {
                await CaptureSelectedWindowAsync();
            }
            else
            {
                // 全屏模式
                await CaptureFullScreenAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "截图已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"截图失败: {ex.Message}";
            _log.LogError("截图操作失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 快捷键入口 — 直接进入全屏区域选择截图
    /// 不依赖当前页面模式，按下快捷键立即启动
    /// </summary>
    [RelayCommand]
    private async Task StartRegionCaptureFromHotkeyAsync()
    {
        try
        {
            // 强制切到区域模式并启动
            CurrentMode = ScreenshotMode.Region;
            IsBusy = true;
            HasResult = false;
            ClearAnnotations();
            await CaptureRegionAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "截图已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"截图失败: {ex.Message}";
            _log.LogError("快捷键截图失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 全屏截图
    /// </summary>
    private async Task CaptureFullScreenAsync()
    {
        StatusMessage = "正在截取全屏...";
        IsProgressIndeterminate = true;

        await Task.Run(() =>
        {
            CapturedImage = _screenshotService.CaptureAllMonitors();
        });

        if (CapturedImage != null)
        {
            HasResult = true;
            StatusMessage = $"全屏截图完成 ({CapturedImage.PixelWidth}x{CapturedImage.PixelHeight})";
        }
        else
        {
            StatusMessage = "截图失败：未能获取屏幕数据";
        }

        IsProgressIndeterminate = false;
    }

    /// <summary>
    /// 延迟截图
    /// </summary>
    private async Task CaptureDelayedAsync()
    {
        _delayCts = new CancellationTokenSource();
        IsCountingDown = true;

        for (int i = DelaySeconds; i > 0; i--)
        {
            _delayCts.Token.ThrowIfCancellationRequested();
            CountdownRemaining = i;
            StatusMessage = $"截图倒计时: {i} 秒...";
            await Task.Delay(1000, _delayCts.Token);
        }

        CountdownRemaining = 0;
        IsCountingDown = false;
        await CaptureFullScreenAsync();
    }

    /// <summary>
    /// 区域截图 — 隐藏主窗口，让用户在全屏上用鼠标选择区域
    /// 支持通过全局快捷键在窗口最小化时启动
    /// </summary>
    private async Task CaptureRegionAsync()
    {
        StatusMessage = "请用鼠标拖拽选择截图区域...";

        var mainWindow = Application.Current.MainWindow;
        var previousState = mainWindow?.WindowState ?? WindowState.Normal;
        bool wasHidden = false;

        if (mainWindow != null)
        {
            // 完全隐藏主窗口，确保全屏选区不被遮挡
            mainWindow.Hide();
            wasHidden = true;
            await Task.Delay(200); // 等待窗口隐藏生效
        }

        try
        {
            var selectionWindow = new Views.ScreenshotSelectionWindow();
            // 设置 Owner 确保选区窗口正确显示（即使主窗口已隐藏）
            if (mainWindow != null)
                selectionWindow.Owner = mainWindow;
            selectionWindow.WindowStartupLocation = WindowStartupLocation.Manual;

            bool? result = selectionWindow.ShowDialog();

            if (result == true && selectionWindow.SelectedRegion.Width > 0 && selectionWindow.SelectedRegion.Height > 0)
            {
                await Task.Run(() =>
                {
                    var nativeRect = new Native.NativeMethods.RECT
                    {
                        Left = (int)selectionWindow.SelectedRegion.Left,
                        Top = (int)selectionWindow.SelectedRegion.Top,
                        Right = (int)selectionWindow.SelectedRegion.Right,
                        Bottom = (int)selectionWindow.SelectedRegion.Bottom
                    };
                    CapturedImage = _screenshotService.CaptureRegion(nativeRect);
                });

                if (CapturedImage != null)
                {
                    HasResult = true;
                    StatusMessage = $"区域截图完成 ({CapturedImage.PixelWidth}x{CapturedImage.PixelHeight})";
                }
            }
            else
            {
                StatusMessage = "区域选择已取消";
            }
        }
        finally
        {
            if (wasHidden && mainWindow != null)
            {
                // 总是恢复到正常状态，不保留之前的最小化状态
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }
    }

    /// <summary>
    /// 窗口截图 — 捕获用户选中的窗口
    /// </summary>
    private async Task CaptureSelectedWindowAsync()
    {
        if (SelectedWindow == null || SelectedWindow.Handle == IntPtr.Zero)
        {
            StatusMessage = "请先刷新窗口列表并选择一个窗口";
            return;
        }

        StatusMessage = $"正在捕获窗口: {SelectedWindow.Title}...";
        IsProgressIndeterminate = true;

        await Task.Run(() =>
        {
            CapturedImage = _screenshotService.CaptureWindow(SelectedWindow.Handle, SelectedWindow.Title);
        });

        if (CapturedImage != null)
        {
            HasResult = true;
            StatusMessage = $"窗口截图完成 ({CapturedImage.PixelWidth}x{CapturedImage.PixelHeight})";
        }

        IsProgressIndeterminate = false;
    }

    /// <summary>
    /// 取消延迟截图
    /// </summary>
    [RelayCommand]
    private void CancelDelay()
    {
        _delayCts?.Cancel();
        _delayCts?.Dispose();
        _delayCts = null;
        IsCountingDown = false;
        CountdownRemaining = 0;
        StatusMessage = "截图已取消";
    }

    // ==================== 保存/复制/放弃 ====================

    /// <summary>
    /// 保存截图到文件
    /// </summary>
    [RelayCommand]
    private void SaveScreenshot(string format)
    {
        if (CapturedImage == null) return;

        try
        {
            // 渲染标注到截图
            var renderedImage = RenderAnnotationsToImage();

            string path = _screenshotService.SaveToFile(renderedImage, format);
            SavedFilePath = path;
            StatusMessage = $"已保存到: {path}";
            _log.LogInfo($"截图已保存 ({format.ToUpper()}): {path}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
            _log.LogError("保存截图失败", ex);
        }
    }

    /// <summary>
    /// 复制截图到剪贴板
    /// </summary>
    [RelayCommand]
    private void CopyScreenshot()
    {
        if (CapturedImage == null) return;

        var renderedImage = RenderAnnotationsToImage();
        bool success = _screenshotService.CopyToClipboard(renderedImage);

        StatusMessage = success ? "截图已复制到剪贴板" : "复制到剪贴板失败，请重试";
    }

    /// <summary>
    /// 放弃当前截图
    /// </summary>
    [RelayCommand]
    private void DiscardScreenshot()
    {
        CapturedImage = null;
        HasResult = false;
        SavedFilePath = null;
        ClearAnnotations();
        StatusMessage = "当前截图已放弃";
    }

    // ==================== 标注管理 ====================

    /// <summary>
    /// 切换标注工具
    /// </summary>
    [RelayCommand]
    private void SetAnnotationTool(string toolName)
    {
        CurrentTool = toolName switch
        {
            "Select" => AnnotationTool.Select,
            "Rectangle" => AnnotationTool.Rectangle,
            "Arrow" => AnnotationTool.Arrow,
            "Text" => AnnotationTool.Text,
            "Mosaic" => AnnotationTool.Mosaic,
            _ => AnnotationTool.Select
        };
        StatusMessage = $"当前标注工具: {toolName}";
    }

    /// <summary>
    /// 添加标注项
    /// </summary>
    /// <param name="annotation">要添加的标注项</param>
    public void AddAnnotation(AnnotationItem annotation)
    {
        // 保存当前状态到撤销栈
        PushUndoState();

        Annotations.Add(annotation);
        UpdateAnnotationState();
    }

    /// <summary>
    /// 删除选中的标注项
    /// </summary>
    [RelayCommand]
    private void DeleteSelectedAnnotations()
    {
        var selected = Annotations.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;

        PushUndoState();
        foreach (var item in selected)
        {
            Annotations.Remove(item);
        }
        UpdateAnnotationState();
    }

    /// <summary>
    /// 撤销标注操作
    /// </summary>
    [RelayCommand]
    private void UndoAnnotation()
    {
        if (_undoStack.Count == 0) return;

        // 保存当前状态到重做栈
        var currentState = Annotations.Select(a => a.Clone()).ToList();
        _redoStack.Push(currentState);

        // 恢复撤销栈中的状态
        var previousState = _undoStack.Pop();
        Annotations.Clear();
        foreach (var item in previousState)
        {
            Annotations.Add(item);
        }

        UpdateAnnotationState();
        StatusMessage = "已撤销标注操作";
    }

    /// <summary>
    /// 重做标注操作
    /// </summary>
    [RelayCommand]
    private void RedoAnnotation()
    {
        if (_redoStack.Count == 0) return;

        // 保存当前状态到撤销栈
        var currentState = Annotations.Select(a => a.Clone()).ToList();
        _undoStack.Push(currentState);

        // 恢复重做栈中的状态
        var nextState = _redoStack.Pop();
        Annotations.Clear();
        foreach (var item in nextState)
        {
            Annotations.Add(item);
        }

        UpdateAnnotationState();
        StatusMessage = "已重做标注操作";
    }

    /// <summary>
    /// 清除所有标注
    /// </summary>
    [RelayCommand]
    private void ClearAllAnnotations()
    {
        if (Annotations.Count == 0) return;
        PushUndoState();
        Annotations.Clear();
        UpdateAnnotationState();
        StatusMessage = "已清除所有标注";
    }

    /// <summary>
    /// 选中所有标注项（取消选中状态）
    /// </summary>
    public void DeselectAllAnnotations()
    {
        foreach (var item in Annotations)
        {
            item.IsSelected = false;
        }
    }

    /// <summary>
    /// 将当前标注状态推入撤销栈
    /// 撤销栈最多保留 20 步，超出时移除最旧的步骤
    /// </summary>
    private void PushUndoState()
    {
        var snapshot = Annotations.Select(a => a.Clone()).ToList();
        _undoStack.Push(snapshot);
        _redoStack.Clear(); // 新操作清空重做栈

        // 限制撤销栈大小
        if (_undoStack.Count > 20)
        {
            // 移除栈底的旧状态（保留最近 20 步）
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = Math.Min(items.Length - 1, 19); i >= 0; i--)
            {
                _undoStack.Push(items[i]);
            }
        }
    }

    /// <summary>
    /// 更新标注相关 UI 状态
    /// </summary>
    private void UpdateAnnotationState()
    {
        AnnotationCount = Annotations.Count;
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }

    /// <summary>
    /// 清除所有标注状态
    /// </summary>
    private void ClearAnnotations()
    {
        Annotations.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateAnnotationState();
    }

    // ==================== 窗口列表 ====================

    /// <summary>
    /// 刷新可用窗口列表
    /// 排除工具箱自身和不可见窗口
    /// </summary>
    [RelayCommand]
    private void RefreshWindowList()
    {
        AvailableWindows.Clear();
        var currentHwnd = Application.Current.MainWindow != null
            ? new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle
            : IntPtr.Zero;

        Native.NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == currentHwnd) return true; // 排除工具箱自身

            if (!Native.NativeMethods.IsWindowVisible(hWnd)) return true;

            int titleLen = Native.NativeMethods.GetWindowTextLength(hWnd);
            if (titleLen == 0) return true; // 跳过无标题窗口

            var sb = new System.Text.StringBuilder(titleLen + 1);
            Native.NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            AvailableWindows.Add(new WindowItem { Handle = hWnd, Title = title });
            return true;
        }, IntPtr.Zero);

        StatusMessage = $"已刷新窗口列表，共 {AvailableWindows.Count} 个可见窗口";

        // 按标题排序
        var sorted = AvailableWindows.OrderBy(w => w.Title).ToList();
        AvailableWindows.Clear();
        foreach (var w in sorted) AvailableWindows.Add(w);
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 将标注渲染到截图图像上
    /// </summary>
    /// <returns>含标注的合成图像</returns>
    private BitmapSource RenderAnnotationsToImage()
    {
        if (CapturedImage == null)
            throw new InvalidOperationException("没有截图可渲染");

        if (Annotations.Count == 0)
            return CapturedImage;

        // 创建 DrawingVisual 进行合成渲染
        int w = CapturedImage.PixelWidth;
        int h = CapturedImage.PixelHeight;
        double dpiX = CapturedImage.DpiX;
        double dpiY = CapturedImage.DpiY;

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            // 先绘制原始截图
            ctx.DrawImage(CapturedImage, new Rect(0, 0, w, h));

            // 再绘制所有标注
            foreach (var ann in Annotations)
            {
                DrawAnnotation(ctx, ann);
            }
        }

        var renderTarget = new RenderTargetBitmap(w, h, dpiX, dpiY, PixelFormats.Pbgra32);
        renderTarget.Render(visual);
        renderTarget.Freeze();
        return renderTarget;
    }

    /// <summary>
    /// 在绘制上下文中绘制单个标注项
    /// </summary>
    private static void DrawAnnotation(DrawingContext ctx, AnnotationItem ann)
    {
        var brush = new SolidColorBrush(ann.Color);
        var pen = new Pen(brush, ann.StrokeThickness);

        switch (ann.Type)
        {
            case AnnotationTool.Rectangle:
                // 绘制矩形框
                ctx.DrawRectangle(null, pen, new Rect(ann.X, ann.Y, ann.Width, ann.Height));
                break;

            case AnnotationTool.Arrow:
                // 绘制箭头
                double startX = ann.X;
                double startY = ann.Y;
                double endX = ann.EndX;
                double endY = ann.EndY;
                ctx.DrawLine(pen, new Point(startX, startY), new Point(endX, endY));

                // 绘制箭头尖端
                double angle = Math.Atan2(endY - startY, endX - startX);
                double arrowLen = 12;
                double arrowAngle = Math.PI / 6;
                var p1 = new Point(
                    endX - arrowLen * Math.Cos(angle - arrowAngle),
                    endY - arrowLen * Math.Sin(angle - arrowAngle));
                var p2 = new Point(
                    endX - arrowLen * Math.Cos(angle + arrowAngle),
                    endY - arrowLen * Math.Sin(angle + arrowAngle));
                ctx.DrawLine(pen, new Point(endX, endY), p1);
                ctx.DrawLine(pen, new Point(endX, endY), p2);
                break;

            case AnnotationTool.Text:
                // 绘制文本
                var text = new FormattedText(ann.Text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Microsoft YaHei"),
                    14, brush, 96);
                ctx.DrawText(text, new Point(ann.X, ann.Y));
                break;

            case AnnotationTool.Mosaic:
                // 绘制马赛克效果（用随机色块覆盖）
                int blockSize = Math.Max(4, (int)(Math.Min(ann.Width, ann.Height) / 10));
                var mosaicRect = new Rect(ann.X, ann.Y, ann.Width, ann.Height);

                // 先绘制半透明覆盖
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null, mosaicRect);

                // 再绘制方块网格
                var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
                for (double x = ann.X; x < ann.X + ann.Width; x += blockSize)
                {
                    ctx.DrawLine(gridPen, new Point(x, ann.Y), new Point(x, ann.Y + ann.Height));
                }
                for (double y = ann.Y; y < ann.Y + ann.Height; y += blockSize)
                {
                    ctx.DrawLine(gridPen, new Point(ann.X, y), new Point(ann.X + ann.Width, y));
                }
                break;
        }
    }
}
