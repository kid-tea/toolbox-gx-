using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Toolbox.Views;

/// <summary>
/// 屏幕区域选择窗口
/// 全屏透明窗口，用户鼠标拖拽选择截图区域
/// 按 Esc 取消，选区完成时 Enter 确认或双击确认
/// </summary>
public partial class ScreenshotSelectionWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting;
    private Rectangle? _selectionRect;

    /// <summary>用户选择的区域（屏幕坐标）</summary>
    public Rect SelectedRegion { get; private set; }

    /// <summary>
    /// 构造函数 — 初始化全屏透明覆盖窗口
    /// </summary>
    public ScreenshotSelectionWindow()
    {
        InitializeComponent();

        // 捕获所有鼠标事件
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseDoubleClick += (_, _) => ConfirmSelection();
    }

    /// <summary>
    /// 窗口加载时手动设置全屏尺寸（避免 Maximized + AllowsTransparency 的 WPF bug）
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 虚拟桌面坐标 — 覆盖所有显示器
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Activate();
        Focus();
    }

    /// <summary>
    /// 右键取消选区
    /// </summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        CancelSelection();
        base.OnMouseRightButtonDown(e);
    }

    /// <summary>
    /// 键盘事件：Esc取消，Enter确认
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelSelection();
        }
        else if (e.Key == Key.Enter && _selectionRect != null)
        {
            ConfirmSelection();
        }
    }

    /// <summary>
    /// 鼠标按下 — 开始选区
    /// </summary>
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        _isSelecting = true;

        if (_selectionRect == null)
        {
            _selectionRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Colors.DodgerBlue),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 30, 144, 255))
            };
            OverlayCanvas.Children.Add(_selectionRect);
        }

        _selectionRect.Margin = new Thickness(_startPoint.X, _startPoint.Y, 0, 0);
        _selectionRect.Width = 0;
        _selectionRect.Height = 0;
        _selectionRect.Visibility = Visibility.Visible;

        // 显示提示文字
        var tipText = OverlayCanvas.Children.OfType<TextBlock>().FirstOrDefault();
        if (tipText != null)
            tipText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 鼠标移动 — 更新选区矩形
    /// </summary>
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || _selectionRect == null) return;

        var currentPoint = e.GetPosition(this);

        // 计算选区矩形
        double x = Math.Min(_startPoint.X, currentPoint.X);
        double y = Math.Min(_startPoint.Y, currentPoint.Y);
        double w = Math.Abs(currentPoint.X - _startPoint.X);
        double h = Math.Abs(currentPoint.Y - _startPoint.Y);

        _selectionRect.Margin = new Thickness(x, y, 0, 0);
        _selectionRect.Width = w;
        _selectionRect.Height = h;
    }

    /// <summary>
    /// 鼠标释放 — 完成选区
    /// </summary>
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSelecting = false;

        if (_selectionRect != null && _selectionRect.Width > 10 && _selectionRect.Height > 10)
        {
            SelectedRegion = BuildSelectedRegion();
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// 确认选区，关闭窗口并返回成功
    /// </summary>
    private void ConfirmSelection()
    {
        if (_selectionRect != null && _selectionRect.Width > 5 && _selectionRect.Height > 5)
        {
            SelectedRegion = BuildSelectedRegion();
            DialogResult = true;
        }
        Close();
    }

    /// <summary>
    /// 取消选区，关闭窗口
    /// </summary>
    private void CancelSelection()
    {
        SelectedRegion = Rect.Empty;
        DialogResult = false;
        Close();
    }

    private Rect BuildSelectedRegion()
    {
        if (_selectionRect == null)
            return Rect.Empty;

        var topLeft = PointToScreen(new Point(
            _selectionRect.Margin.Left,
            _selectionRect.Margin.Top));
        var bottomRight = PointToScreen(new Point(
            _selectionRect.Margin.Left + _selectionRect.Width,
            _selectionRect.Margin.Top + _selectionRect.Height));

        double left = Math.Floor(Math.Min(topLeft.X, bottomRight.X));
        double top = Math.Floor(Math.Min(topLeft.Y, bottomRight.Y));
        double right = Math.Ceiling(Math.Max(topLeft.X, bottomRight.X));
        double bottom = Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y));

        return new Rect(left, top, right - left, bottom - top);
    }
}
