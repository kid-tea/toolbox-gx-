using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.ComponentModel;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 截屏视图代码后置
/// 处理标注画布的鼠标事件（拖拽绘制矩形/箭头/文字/马赛克）
/// 以及标注项的选中、移动、删除交互
/// </summary>
public partial class ScreenshotView : UserControl
{
    private ScreenshotViewModel VM => (ScreenshotViewModel)DataContext;

    // ==================== 标注绘制状态 ====================

    /// <summary>当前正在绘制的标注项</summary>
    private AnnotationItem? _currentAnnotation;

    /// <summary>标注开始点（相对于截图）</summary>
    private Point _annotationStartPoint;

    /// <summary>拖拽移动的偏移量</summary>
    private Point _dragOffset;
    private Point _dragEndOffset;

    /// <summary>是否正在拖拽移动选中标注</summary>
    private bool _isDragging;
    private bool _vmEventsAttached;
    private bool _isUserZooming;
    private double _previewZoom = 1.0;

    /// <summary>当前标注的 WPF 预览形状（绘制过程中）</summary>
    private Shape? _previewShape;

    /// <summary>
    /// 构造函数
    /// 通过 DI 注入 ViewModel，确保截图命令和预览绑定正常工作
    /// </summary>
    public ScreenshotView(ScreenshotViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 加载后初始化 DataContext
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScreenshotViewModel vm)
        {
            if (!_vmEventsAttached)
            {
                vm.Annotations.CollectionChanged += (_, _) => RefreshAnnotationCanvas();
                vm.PropertyChanged += OnViewModelPropertyChanged;
                _vmEventsAttached = true;
            }

            ResetPreviewZoomToFit();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScreenshotViewModel vm && _vmEventsAttached)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vmEventsAttached = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenshotViewModel.CapturedImage) ||
            e.PropertyName == nameof(ScreenshotViewModel.HasResult))
        {
            _isUserZooming = false;
            Dispatcher.BeginInvoke(new Action(ResetPreviewZoomToFit));
        }
    }

    private void OnPreviewScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isUserZooming)
            ResetPreviewZoomToFit();
    }

    private void ResetPreviewZoomToFit()
    {
        if (VM.CapturedImage == null || PreviewScrollViewer.ActualWidth <= 0 || PreviewScrollViewer.ActualHeight <= 0)
            return;

        double availableWidth = Math.Max(1, PreviewScrollViewer.ActualWidth - 18);
        double availableHeight = Math.Max(1, PreviewScrollViewer.ActualHeight - 18);
        double fit = Math.Min(availableWidth / VM.CapturedImage.PixelWidth, availableHeight / VM.CapturedImage.PixelHeight);
        _previewZoom = Math.Clamp(fit, 0.05, 1.0);
        ApplyPreviewZoom(_previewZoom);
        PreviewScrollViewer.ScrollToHorizontalOffset(0);
        PreviewScrollViewer.ScrollToVerticalOffset(0);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (VM.CapturedImage == null)
            return;

        double oldZoom = _previewZoom;
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        double newZoom = Math.Clamp(oldZoom * factor, 0.05, 6.0);
        if (Math.Abs(newZoom - oldZoom) < 0.001)
            return;

        var mouse = e.GetPosition(PreviewScrollViewer);
        double imageX = (PreviewScrollViewer.HorizontalOffset + mouse.X) / oldZoom;
        double imageY = (PreviewScrollViewer.VerticalOffset + mouse.Y) / oldZoom;

        _isUserZooming = true;
        _previewZoom = newZoom;
        ApplyPreviewZoom(newZoom);
        PreviewSurface.UpdateLayout();

        PreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, imageX * newZoom - mouse.X));
        PreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, imageY * newZoom - mouse.Y));
        e.Handled = true;
    }

    private void ApplyPreviewZoom(double zoom)
    {
        PreviewScaleTransform.ScaleX = zoom;
        PreviewScaleTransform.ScaleY = zoom;
    }

    // ==================== 标注画布鼠标事件 ====================

    /// <summary>
    /// 鼠标按下 — 开始绘制标注或选中/移动标注
    /// </summary>
    private void OnAnnotationCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var canvas = (Canvas)sender;
        var pos = e.GetPosition(canvas);

        if (VM.CurrentTool == AnnotationTool.Select)
        {
            // 切换到选择模式，取消所有选中，然后判断点击位置是否在标注上
            HandleSelectMouseDown(canvas, pos);
        }
        else
        {
            // 开始绘制标注
            StartDrawingAnnotation(pos);
        }
    }

    /// <summary>
    /// 鼠标移动 — 更新正在绘制的标注，或移动选中标注
    /// </summary>
    private void OnAnnotationCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var canvas = (Canvas)sender;
        var pos = e.GetPosition(canvas);

        if (_isDragging && _currentAnnotation != null)
        {
            // 拖拽移动选中标注
            HandleDragMove(pos);
        }
        else if (_currentAnnotation != null && _previewShape != null)
        {
            // 更新正在绘制的标注形状
            UpdatePreviewShape(pos);
        }
    }

    /// <summary>
    /// 鼠标释放 — 完成标注绘制或结束拖拽
    /// </summary>
    private void OnAnnotationCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        var canvas = (Canvas)sender;
        var pos = e.GetPosition(canvas);

        if (_isDragging)
        {
            // 结束拖拽移动
            _isDragging = false;
            Mouse.Capture(null);
            return;
        }

        if (_currentAnnotation != null)
        {
            // 完成标注绘制
            FinishDrawingAnnotation(pos);
        }
    }

    // ==================== 选择模式处理 ====================

    /// <summary>
    /// 选择模式：判断点击位置，执行选中或移动操作
    /// </summary>
    private void HandleSelectMouseDown(Canvas canvas, Point pos)
    {
        VM.DeselectAllAnnotations();

        // 检测点击是否在某个标注上（反向遍历，上层优先）
        var hitAnnotation = VM.Annotations.Reverse()
            .FirstOrDefault(a => IsPointInAnnotation(a, pos));

        if (hitAnnotation != null)
        {
            hitAnnotation.IsSelected = true;
            _currentAnnotation = hitAnnotation;
            _isDragging = true;
            _dragOffset = new Point(pos.X - hitAnnotation.X, pos.Y - hitAnnotation.Y);
            _dragEndOffset = new Point(pos.X - hitAnnotation.EndX, pos.Y - hitAnnotation.EndY);
            Mouse.Capture(canvas);
        }
    }

    /// <summary>
    /// 拖拽移动选中的标注
    /// </summary>
    private void HandleDragMove(Point pos)
    {
        if (_currentAnnotation == null) return;

        double newX = pos.X - _dragOffset.X;
        double newY = pos.Y - _dragOffset.Y;
        double dx = newX - _currentAnnotation.X;
        double dy = newY - _currentAnnotation.Y;

        _currentAnnotation.X = newX;
        _currentAnnotation.Y = newY;

        // 如果是箭头，同时移动终点
        if (_currentAnnotation.Type == AnnotationTool.Arrow)
        {
            _currentAnnotation.EndX = pos.X - _dragEndOffset.X;
            _currentAnnotation.EndY = pos.Y - _dragEndOffset.Y;
        }
        else if (_currentAnnotation.Type == AnnotationTool.Pen)
        {
            for (int i = 0; i < _currentAnnotation.Points.Count; i++)
            {
                var p = _currentAnnotation.Points[i];
                _currentAnnotation.Points[i] = new Point(p.X + dx, p.Y + dy);
            }
        }

        RefreshAnnotationCanvas();
    }

    // ==================== 标注绘制 ====================

    /// <summary>
    /// 开始绘制新标注
    /// </summary>
    private void StartDrawingAnnotation(Point pos)
    {
        _annotationStartPoint = pos;
        _currentAnnotation = new AnnotationItem
        {
            Type = VM.CurrentTool,
            Color = VM.AnnotationColor,
            Text = VM.AnnotationText,
            X = pos.X,
            Y = pos.Y,
            Width = 0,
            Height = 0,
            EndX = pos.X,
            EndY = pos.Y
        };
        if (VM.CurrentTool == AnnotationTool.Pen)
            _currentAnnotation.Points.Add(pos);

        // 创建预览形状
        _previewShape = VM.CurrentTool switch
        {
            AnnotationTool.Rectangle => new Rectangle
            {
                Stroke = new SolidColorBrush(VM.AnnotationColor),
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            },
            AnnotationTool.Arrow => new Line
            {
                Stroke = new SolidColorBrush(VM.AnnotationColor),
                StrokeThickness = 2,
                X1 = pos.X,
                Y1 = pos.Y,
                X2 = pos.X,
                Y2 = pos.Y
            },
            AnnotationTool.Pen => new Polyline
            {
                Stroke = new SolidColorBrush(VM.AnnotationColor),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection { pos }
            },
            AnnotationTool.Text => null, // 文字工具不显示预览
            AnnotationTool.Mosaic => new Rectangle
            {
                Stroke = new SolidColorBrush(VM.AnnotationColor),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0))
            },
            _ => null
        };

        if (_previewShape != null)
        {
            if (_previewShape is not Line && _previewShape is not Polyline)
            {
                Canvas.SetLeft(_previewShape, pos.X);
                Canvas.SetTop(_previewShape, pos.Y);
            }
            AnnotationCanvas.Children.Add(_previewShape);
        }
    }

    /// <summary>
    /// 更新预览形状
    /// </summary>
    private void UpdatePreviewShape(Point pos)
    {
        if (_currentAnnotation == null) return;

        double x = Math.Min(_annotationStartPoint.X, pos.X);
        double y = Math.Min(_annotationStartPoint.Y, pos.Y);
        double w = Math.Abs(pos.X - _annotationStartPoint.X);
        double h = Math.Abs(pos.Y - _annotationStartPoint.Y);

        if (_currentAnnotation.Type == AnnotationTool.Arrow)
        {
            _currentAnnotation.X = _annotationStartPoint.X;
            _currentAnnotation.Y = _annotationStartPoint.Y;
            _currentAnnotation.Width = w;
            _currentAnnotation.Height = h;
            _currentAnnotation.EndX = pos.X;
            _currentAnnotation.EndY = pos.Y;
        }
        else if (_currentAnnotation.Type == AnnotationTool.Pen)
        {
            if (_currentAnnotation.Points.Count == 0 || Distance(_currentAnnotation.Points[^1], pos) >= 1.5)
                _currentAnnotation.Points.Add(pos);

            UpdatePenBounds(_currentAnnotation);
        }
        else
        {
            _currentAnnotation.X = x;
            _currentAnnotation.Y = y;
            _currentAnnotation.Width = w;
            _currentAnnotation.Height = h;
            _currentAnnotation.EndX = pos.X;
            _currentAnnotation.EndY = pos.Y;
        }

        if (_previewShape != null)
        {
            if (_previewShape is Line line)
            {
                line.X1 = _annotationStartPoint.X;
                line.Y1 = _annotationStartPoint.Y;
                line.X2 = pos.X;
                line.Y2 = pos.Y;
            }
            else if (_previewShape is Rectangle rect)
            {
                Canvas.SetLeft(_previewShape, x);
                Canvas.SetTop(_previewShape, y);
                rect.Width = w;
                rect.Height = h;
            }
            else if (_previewShape is Polyline polyline)
            {
                polyline.Points = new PointCollection(_currentAnnotation.Points);
            }
        }
    }

    /// <summary>
    /// 完成标注绘制，校验并添加到 ViewModel
    /// </summary>
    private void FinishDrawingAnnotation(Point pos)
    {
        if (_currentAnnotation == null) return;

        // 移除预览形状
        if (_previewShape != null)
        {
            AnnotationCanvas.Children.Remove(_previewShape);
            _previewShape = null;
        }

        // 文字标注：弹出输入框
        if (_currentAnnotation.Type == AnnotationTool.Text)
        {
            _currentAnnotation.X = pos.X;
            _currentAnnotation.Y = pos.Y;
            _currentAnnotation.Width = 100;
            _currentAnnotation.Height = 20;

            var inputDialog = new TextInputDialog(VM.AnnotationText);
            if (inputDialog.ShowDialog() == true)
            {
                _currentAnnotation.Text = inputDialog.InputText;
                VM.AddAnnotation(_currentAnnotation);
            }
            _currentAnnotation = null;
            RefreshAnnotationCanvas();
            return;
        }

        // 矩形/箭头/马赛克：检查最小尺寸
        if (_currentAnnotation.Type == AnnotationTool.Pen)
        {
            UpdatePenBounds(_currentAnnotation);
            if (_currentAnnotation.Points.Count < 2 || (_currentAnnotation.Width < 3 && _currentAnnotation.Height < 3))
            {
                _currentAnnotation = null;
                return;
            }

            VM.AddAnnotation(_currentAnnotation);
            _currentAnnotation = null;
            RefreshAnnotationCanvas();
            return;
        }

        if (_currentAnnotation.Width < 5 && _currentAnnotation.Height < 5)
        {
            _currentAnnotation = null;
            return; // 太小的标注忽略
        }

        VM.AddAnnotation(_currentAnnotation);
        _currentAnnotation = null;
        RefreshAnnotationCanvas();
    }

    // ==================== 标注画布刷新 ====================

    /// <summary>
    /// 完全刷新标注画布，根据 Annotations 集合重新绘制所有标注
    /// </summary>
    private void RefreshAnnotationCanvas()
    {
        AnnotationCanvas.Children.Clear();

        if (VM.Annotations.Count == 0) return;

        foreach (var ann in VM.Annotations)
        {
            DrawAnnotationOnCanvas(ann);
        }
    }

    /// <summary>
    /// 在画布上绘制单个标注
    /// </summary>
    private void DrawAnnotationOnCanvas(AnnotationItem ann)
    {
        var brush = new SolidColorBrush(ann.Color);
        var penThickness = ann.IsSelected ? 3 : ann.StrokeThickness;

        switch (ann.Type)
        {
            case AnnotationTool.Rectangle:
                var rect = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = penThickness,
                    Fill = Brushes.Transparent,
                    Width = ann.Width,
                    Height = ann.Height
                };
                Canvas.SetLeft(rect, ann.X);
                Canvas.SetTop(rect, ann.Y);

                if (ann.IsSelected)
                {
                    rect.StrokeDashArray = new DoubleCollection { 4, 2 };
                }
                AnnotationCanvas.Children.Add(rect);
                break;

            case AnnotationTool.Arrow:
                // 主线段
                var mainLine = new Line
                {
                    Stroke = brush,
                    StrokeThickness = penThickness,
                    X1 = ann.X,
                    Y1 = ann.Y,
                    X2 = ann.EndX,
                    Y2 = ann.EndY
                };
                AnnotationCanvas.Children.Add(mainLine);

                // 箭头尖端两条线段
                double angle = Math.Atan2(ann.EndY - ann.Y, ann.EndX - ann.X);
                double arrowLen = 12;
                double arrowAngle = Math.PI / 6;

                var line1 = new Line
                {
                    Stroke = brush,
                    StrokeThickness = penThickness,
                    X1 = ann.EndX,
                    Y1 = ann.EndY,
                    X2 = ann.EndX - arrowLen * Math.Cos(angle - arrowAngle),
                    Y2 = ann.EndY - arrowLen * Math.Sin(angle - arrowAngle)
                };
                var line2 = new Line
                {
                    Stroke = brush,
                    StrokeThickness = penThickness,
                    X1 = ann.EndX,
                    Y1 = ann.EndY,
                    X2 = ann.EndX - arrowLen * Math.Cos(angle + arrowAngle),
                    Y2 = ann.EndY - arrowLen * Math.Sin(angle + arrowAngle)
                };
                AnnotationCanvas.Children.Add(line1);
                AnnotationCanvas.Children.Add(line2);
                break;

            case AnnotationTool.Pen:
                if (ann.Points.Count > 1)
                {
                    var polyline = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = penThickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Points = new PointCollection(ann.Points)
                    };
                    AnnotationCanvas.Children.Add(polyline);
                }
                break;

            case AnnotationTool.Text:
                var textBlock = new System.Windows.Controls.TextBlock
                {
                    Text = ann.Text,
                    Foreground = brush,
                    FontSize = 14,
                    FontFamily = new FontFamily("Microsoft YaHei")
                };
                Canvas.SetLeft(textBlock, ann.X);
                Canvas.SetTop(textBlock, ann.Y);
                AnnotationCanvas.Children.Add(textBlock);
                break;

            case AnnotationTool.Mosaic:
                // 马赛克覆盖矩形
                var mosaicRect = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = penThickness,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    Width = ann.Width,
                    Height = ann.Height
                };
                Canvas.SetLeft(mosaicRect, ann.X);
                Canvas.SetTop(mosaicRect, ann.Y);
                AnnotationCanvas.Children.Add(mosaicRect);

                // 马赛克网格效果
                int blockSize = Math.Max(4, (int)(Math.Min(ann.Width, ann.Height) / 12));
                var gridPen = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                for (double x = 0; x < ann.Width; x += blockSize)
                {
                    var vLine = new Line
                    {
                        Stroke = gridPen,
                        StrokeThickness = 0.5,
                        X1 = ann.X + x,
                        Y1 = ann.Y,
                        X2 = ann.X + x,
                        Y2 = ann.Y + ann.Height
                    };
                    AnnotationCanvas.Children.Add(vLine);
                }
                for (double y = 0; y < ann.Height; y += blockSize)
                {
                    var hLine = new Line
                    {
                        Stroke = gridPen,
                        StrokeThickness = 0.5,
                        X1 = ann.X,
                        Y1 = ann.Y + y,
                        X2 = ann.X + ann.Width,
                        Y2 = ann.Y + y
                    };
                    AnnotationCanvas.Children.Add(hLine);
                }
                break;
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 判断指定坐标是否在标注区域内
    /// </summary>
    private static bool IsPointInAnnotation(AnnotationItem ann, Point pos)
    {
        double margin = 4; // 点击容差

        switch (ann.Type)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Mosaic:
                return pos.X >= ann.X - margin && pos.X <= ann.X + ann.Width + margin &&
                       pos.Y >= ann.Y - margin && pos.Y <= ann.Y + ann.Height + margin;

            case AnnotationTool.Arrow:
                // 检测是否在线段附近
                return DistanceToLine(pos, new Point(ann.X, ann.Y), new Point(ann.EndX, ann.EndY)) < 10;

            case AnnotationTool.Pen:
                for (int i = 1; i < ann.Points.Count; i++)
                {
                    if (DistanceToLine(pos, ann.Points[i - 1], ann.Points[i]) < 10)
                        return true;
                }
                return false;

            case AnnotationTool.Text:
                return pos.X >= ann.X - margin && pos.X <= ann.X + ann.Width + margin &&
                       pos.Y >= ann.Y - margin && pos.Y <= ann.Y + ann.Height + margin;

            default:
                return false;
        }
    }

    /// <summary>
    /// 计算点到线段的距离
    /// </summary>
    private static double DistanceToLine(Point p, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSq = dx * dx + dy * dy;

        if (lengthSq < 0.001) return Distance(p, a);

        double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSq));
        Point projection = new Point(a.X + t * dx, a.Y + t * dy);
        return Distance(p, projection);
    }

    /// <summary>
    /// 计算两点之间的距离
    /// </summary>
    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void UpdatePenBounds(AnnotationItem ann)
    {
        if (ann.Points.Count == 0)
            return;

        double minX = ann.Points.Min(p => p.X);
        double minY = ann.Points.Min(p => p.Y);
        double maxX = ann.Points.Max(p => p.X);
        double maxY = ann.Points.Max(p => p.Y);
        ann.X = minX;
        ann.Y = minY;
        ann.Width = maxX - minX;
        ann.Height = maxY - minY;
    }

    // ==================== 颜色按钮点击 ====================

    /// <summary>
    /// 颜色按钮点击事件 — 设置标注颜色
    /// </summary>
    private void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string colorName)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorName);
                VM.AnnotationColor = color;
            }
            catch { /* 颜色解析失败，忽略 */ }
        }
    }

    private void OnPaletteButtonClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPaletteDialog(VM.AnnotationColor)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
            VM.AnnotationColor = dialog.SelectedColor;
    }
}

/// <summary>
/// 文字输入对话框 — 用于文字标注工具
/// </summary>
public class TextInputDialog : Window
{
    private readonly TextBox _textBox;

    /// <summary>用户输入的文本</summary>
    public string InputText => _textBox.Text;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="defaultText">默认文本</param>
    public TextInputDialog(string defaultText = "标注文字")
    {
        Title = "输入标注文字";
        Width = 300;
        Height = 140;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _textBox = new TextBox
        {
            Text = defaultText,
            Margin = new Thickness(12, 12, 12, 6),
            Height = 28,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.Children.Add(_textBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 12)
        };
        Grid.SetRow(btnPanel, 1);

        var okBtn = new Button
        {
            Content = "确定",
            Width = 70,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0)
        };
        okBtn.Click += (_, _) => { DialogResult = true; Close(); };

        var cancelBtn = new Button
        {
            Content = "取消",
            Width = 70,
            Height = 28
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        grid.Children.Add(btnPanel);

        Content = grid;
        _textBox.Focus();
        _textBox.SelectAll();
        _textBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; Close(); }
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }
}

public class ColorPaletteDialog : Window
{
    private readonly Slider _redSlider;
    private readonly Slider _greenSlider;
    private readonly Slider _blueSlider;
    private readonly Border _preview;
    private readonly TextBlock _hexText;

    public Color SelectedColor { get; private set; }

    public ColorPaletteDialog(Color initialColor)
    {
        Title = "选择标注颜色";
        Width = 340;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SelectedColor = initialColor;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _preview = new Border
        {
            Width = 56,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10)
        };
        _hexText = new TextBlock
        {
            Margin = new Thickness(68, 6, 0, 0),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top
        };
        var previewGrid = new Grid();
        previewGrid.Children.Add(_preview);
        previewGrid.Children.Add(_hexText);
        root.Children.Add(previewGrid);

        _redSlider = CreateSlider(initialColor.R);
        _greenSlider = CreateSlider(initialColor.G);
        _blueSlider = CreateSlider(initialColor.B);
        AddSliderRow(root, "R", _redSlider, 1);
        AddSliderRow(root, "G", _greenSlider, 2);
        AddSliderRow(root, "B", _blueSlider, 3);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(buttons, 4);
        var ok = new Button { Content = "确定", Width = 72, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "取消", Width = 72, Height = 30 };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        UpdateColor();
    }

    private Slider CreateSlider(byte value)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 255,
            Value = value,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Width = 230
        };
        slider.ValueChanged += (_, _) => UpdateColor();
        return slider;
    }

    private static void AddSliderRow(Grid root, string label, Slider slider, int row)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 0)
        };
        Grid.SetRow(panel, row);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(slider);
        root.Children.Add(panel);
    }

    private void UpdateColor()
    {
        SelectedColor = Color.FromRgb(
            (byte)_redSlider.Value,
            (byte)_greenSlider.Value,
            (byte)_blueSlider.Value);
        _preview.Background = new SolidColorBrush(SelectedColor);
        _hexText.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
    }
}
