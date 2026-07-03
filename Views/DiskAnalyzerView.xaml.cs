using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Toolbox.Services;
using Toolbox.ViewModels;
using Path = System.Windows.Shapes.Path;

namespace Toolbox.Views;

public partial class DiskAnalyzerView : UserControl
{
    private const double TreemapWidth = 900;
    private const double TreemapHeight = 360;
    private const int MaxTreemapTiles = 1800;

    private DiskAnalyzerViewModel VM => (DiskAnalyzerViewModel)DataContext;
    private bool _chartsRendered;
    private TextBlock? _pieTooltip;

    public DiskAnalyzerView(DiskAnalyzerViewModel viewModel)
    {
        try
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += OnLoaded;

            // 饼图悬停提示标签（固定在画布底部）
            _pieTooltip = new TextBlock
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Background = (Brush)FindResource("CardBrush"),
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Canvas.SetLeft(_pieTooltip, 5);
            Canvas.SetTop(_pieTooltip, 210);
            _pieTooltip.Width = 230;
            TreemapCanvas.Children.Add(_pieTooltip);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiskAnalyzerView 初始化失败: {ex}");
            Content = new TextBlock
            {
                Text = $"磁盘分析页加载失败:\n{ex.Message}",
                FontSize = 14, Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiskAnalyzerViewModel.ChartData))
            {
                _chartsRendered = false;
                Dispatcher.BeginInvoke(new Action(RenderCharts),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
        if (!_chartsRendered && VM.ChartData.Count > 0)
            Dispatcher.BeginInvoke(new Action(RenderCharts),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ==================== 图表渲染 ====================

    private void RenderCharts()
    {
        if (_chartsRendered) return;
        _chartsRendered = true;
        DrawBarChart();
        DrawTreemap();
    }

    // ==================== 柱状图 ====================

    private void DrawBarChart()
    {
        var canvas = BarChartContainer.Child as Canvas ?? new Canvas();
        canvas.Children.Clear();
        BarChartContainer.Child = canvas;

        var data = VM.ChartData?.Where(d => d.TotalSize > 0).Take(8).ToList();
        if (data == null || data.Count == 0) return;

        const double chartW = 340;
        const double rowH = 25;
        const double labelW = 84;
        const double barW = 182;
        const double top = 8;
        var maxSize = data.Max(d => d.TotalSize);

        for (int i = 0; i < data.Count; i++)
        {
            var item = data[i];
            var y = top + i * rowH;
            var brush = DiskAnalyzerViewModel.GetChartBrush(item.ColorIndex);
            var width = Math.Max(2, barW * item.TotalSize / Math.Max(1d, maxSize));

            var name = new TextBlock
            {
                Text = item.Name,
                FontSize = 10,
                Foreground = FindResource("TextSecondaryBrush") as Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = labelW,
                ToolTip = item.FullPath
            };
            Canvas.SetLeft(name, 4);
            Canvas.SetTop(name, y + 4);
            canvas.Children.Add(name);

            var back = new System.Windows.Shapes.Rectangle
            {
                Width = barW,
                Height = 13,
                Fill = new SolidColorBrush(Color.FromRgb(232, 235, 239)),
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(back, labelW + 8);
            Canvas.SetTop(back, y + 5);
            canvas.Children.Add(back);

            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 0,
                Height = 13,
                Fill = brush,
                RadiusX = 2,
                RadiusY = 2,
                Cursor = item.IsOther ? Cursors.Arrow : Cursors.Hand,
                ToolTip = $"{item.Name}\n{item.SizeDisplay} ({item.PercentageDisplay})"
            };
            Canvas.SetLeft(bar, labelW + 8);
            Canvas.SetTop(bar, y + 5);
            canvas.Children.Add(bar);
            bar.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(width, TimeSpan.FromMilliseconds(220 + i * 25))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            bar.MouseLeftButtonDown += (_, _) => { if (!item.IsOther) VM.DrillDown(item); };

            var value = new TextBlock
            {
                Text = item.PercentageDisplay,
                FontSize = 10,
                Foreground = FindResource("TextMutedBrush") as Brush,
                Width = chartW - labelW - barW - 20,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(value, labelW + barW + 14);
            Canvas.SetTop(value, y + 3);
            canvas.Children.Add(value);
        }
    }


    private void DrawTreemap()
    {
        TreemapCanvas.Children.Clear();
        if (_pieTooltip != null)
            TreemapCanvas.Children.Add(_pieTooltip);

        if (VM.ChartRoot == null || VM.ChartRoot.TotalSize <= 0) return;

        TreemapCanvas.Background = new SolidColorBrush(Color.FromRgb(35, 35, 35));
        var rendered = 0;
        DrawFolderTreemap(VM.ChartRoot, new Rect(0, 0, TreemapWidth, TreemapHeight), 0, ref rendered);
    }

    private void DrawFolderTreemap(DiskFolderNode folder, Rect area, int depth, ref int rendered)
    {
        if (rendered >= MaxTreemapTiles || area.Width < 4 || area.Height < 4 || folder.TotalSize <= 0) return;

        AddTreemapContainer(folder, area, depth);

        var content = new Rect(area.X + 2, area.Y + 18, Math.Max(0, area.Width - 4), Math.Max(0, area.Height - 20));
        if (content.Width < 4 || content.Height < 4) return;

        var children = folder.ChildFolders
            .Where(f => f.TotalSize > 0)
            .Select(f => new TreemapEntry(f.Name, f.FullPath, f.TotalSize, f.SizeDisplay, f, null))
            .Concat(folder.ChildFiles
                .Where(f => f.Size > 0)
                .Select(f => new TreemapEntry(f.Name, f.FullPath, f.Size, f.SizeDisplay, null, f)))
            .OrderByDescending(e => e.Size)
            .Take(depth < 2 ? 90 : 36)
            .ToList();

        foreach (var (entry, rect) in SquarifyTreemap(children, content))
        {
            if (rendered >= MaxTreemapTiles) return;
            rendered++;

            if (entry.Folder != null && rect.Width > 90 && rect.Height > 70 && depth < 7)
                DrawFolderTreemap(entry.Folder, rect, depth + 1, ref rendered);
            else
                AddTreemapTile(entry, rect, depth);
        }
    }

    private static IEnumerable<(TreemapEntry Entry, Rect Rect)> SquarifyTreemap(IReadOnlyList<TreemapEntry> entries, Rect area)
    {
        if (entries.Count == 0 || area.Width <= 0 || area.Height <= 0) yield break;

        long total = entries.Sum(e => e.Size);
        if (total <= 0) yield break;

        var usableArea = area.Width * area.Height;
        var remaining = area;
        var scaled = entries
            .Select(e => new TreemapLayoutItem(e, Math.Max(0.5, usableArea * e.Size / total)))
            .Where(i => i.Area > 0)
            .ToList();
        var row = new List<TreemapLayoutItem>();

        foreach (var item in scaled)
        {
            if (row.Count == 0)
            {
                row.Add(item);
                continue;
            }

            var shortSide = Math.Max(1, Math.Min(remaining.Width, remaining.Height));
            var currentWorst = WorstAspect(row, shortSide);
            row.Add(item);
            var nextWorst = WorstAspect(row, shortSide);

            if (nextWorst <= currentWorst)
                continue;

            row.RemoveAt(row.Count - 1);
            foreach (var laidOut in LayoutTreemapRow(row, remaining, out remaining))
                yield return laidOut;
            row.Clear();
            row.Add(item);

            if (remaining.Width <= 0 || remaining.Height <= 0)
                yield break;
        }

        if (row.Count > 0)
        {
            foreach (var laidOut in LayoutTreemapRow(row, remaining, out remaining))
                yield return laidOut;
        }
    }

    private static double WorstAspect(IReadOnlyList<TreemapLayoutItem> row, double side)
    {
        if (row.Count == 0) return double.MaxValue;

        var sum = row.Sum(i => i.Area);
        var max = row.Max(i => i.Area);
        var min = row.Min(i => i.Area);
        if (sum <= 0 || min <= 0 || side <= 0) return double.MaxValue;

        var sideSquared = side * side;
        var sumSquared = sum * sum;
        return Math.Max(sideSquared * max / sumSquared, sumSquared / (sideSquared * min));
    }

    private static List<(TreemapEntry Entry, Rect Rect)> LayoutTreemapRow(
        IReadOnlyList<TreemapLayoutItem> row,
        Rect remaining,
        out Rect nextRemaining)
    {
        var result = new List<(TreemapEntry Entry, Rect Rect)>();
        nextRemaining = remaining;

        if (row.Count == 0 || remaining.Width <= 0 || remaining.Height <= 0)
            return result;

        var sum = row.Sum(i => i.Area);
        if (sum <= 0) return result;

        if (remaining.Width >= remaining.Height)
        {
            var rowWidth = Math.Min(remaining.Width, Math.Max(1, sum / Math.Max(1, remaining.Height)));
            var y = remaining.Y;
            var remainingHeight = remaining.Height;

            for (var i = 0; i < row.Count; i++)
            {
                var height = i == row.Count - 1
                    ? remainingHeight
                    : Math.Min(remainingHeight, Math.Max(1, row[i].Area / rowWidth));
                if (height <= 0) return result;

                result.Add((row[i].Entry, new Rect(remaining.X, y, rowWidth, height)));
                y += height;
                remainingHeight -= height;
            }

            nextRemaining = new Rect(
                remaining.X + rowWidth,
                remaining.Y,
                Math.Max(0, remaining.Width - rowWidth),
                remaining.Height);
        }
        else
        {
            var rowHeight = Math.Min(remaining.Height, Math.Max(1, sum / Math.Max(1, remaining.Width)));
            var x = remaining.X;
            var remainingWidth = remaining.Width;

            for (var i = 0; i < row.Count; i++)
            {
                var width = i == row.Count - 1
                    ? remainingWidth
                    : Math.Min(remainingWidth, Math.Max(1, row[i].Area / rowHeight));
                if (width <= 0) return result;

                result.Add((row[i].Entry, new Rect(x, remaining.Y, width, rowHeight)));
                x += width;
                remainingWidth -= width;
            }

            nextRemaining = new Rect(
                remaining.X,
                remaining.Y + rowHeight,
                remaining.Width,
                Math.Max(0, remaining.Height - rowHeight));
        }

        return result;
    }

    private void AddTreemapContainer(DiskFolderNode folder, Rect rect, int depth)
    {
        var border = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, rect.Width - 1),
            Height = Math.Max(0, rect.Height - 1),
            Fill = new SolidColorBrush(Color.FromRgb((byte)Math.Min(58, 34 + depth * 4), (byte)Math.Min(58, 34 + depth * 4), (byte)Math.Min(58, 34 + depth * 4))),
            Stroke = new SolidColorBrush(Color.FromRgb(74, 74, 74)),
            StrokeThickness = depth == 0 ? 1.5 : 0.8,
            Tag = folder,
            Cursor = Cursors.Hand
        };
        Canvas.SetLeft(border, rect.X);
        Canvas.SetTop(border, rect.Y);
        TreemapCanvas.Children.Add(border);

        border.MouseLeftButtonDown += (_, e) =>
        {
            DrillDownAndSelectFolder(folder);
            e.Handled = true;
        };

        if (rect.Width > 74 && rect.Height > 30)
        {
            var label = new Border
            {
                Width = Math.Max(0, rect.Width - 8),
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                IsHitTestVisible = false,
                ClipToBounds = true,
                Child = new TextBlock
                {
                    Text = $"{folder.Name} ({folder.SizeDisplay})",
                    Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    FontSize = depth == 0 ? 11 : 9,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Canvas.SetLeft(label, rect.X + 4);
            Canvas.SetTop(label, rect.Y + 2);
            TreemapCanvas.Children.Add(label);
        }
    }

    private void DrillDownAndSelectFolder(DiskFolderNode folder)
    {
        VM.DrillDown(new ChartItem
        {
            Name = folder.Name,
            FullPath = folder.FullPath,
            TotalSize = folder.TotalSize,
            Percentage = 100,
            SourceFolder = folder
        });
        SelectTreeItem(folder);
    }

    private void SelectTreeItem(object target)
    {
        if (VM.SelectedDisk == null) return;

        if (target is DiskFolderNode folder)
            ExpandFolderPath(VM.SelectedDisk, folder);
        else if (target is DiskFileItem file)
            ExpandFilePath(VM.SelectedDisk, file);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            FolderTree.UpdateLayout();
            var item = FindTreeViewItem(FolderTree, target);
            if (item == null) return;

            item.IsSelected = true;
            item.Focus();
            item.BringIntoView();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool ExpandFolderPath(DiskFolderNode current, DiskFolderNode target)
    {
        if (ReferenceEquals(current, target) ||
            string.Equals(current.FullPath, target.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            current.IsExpanded = true;
            return true;
        }

        foreach (var child in current.ChildFolders)
        {
            if (!ExpandFolderPath(child, target)) continue;
            current.IsExpanded = true;
            return true;
        }

        return false;
    }

    private static bool ExpandFilePath(DiskFolderNode current, DiskFileItem target)
    {
        if (current.ChildFiles.Any(file =>
                ReferenceEquals(file, target) ||
                string.Equals(file.FullPath, target.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            current.IsExpanded = true;
            return true;
        }

        foreach (var child in current.ChildFolders)
        {
            if (!ExpandFilePath(child, target)) continue;
            current.IsExpanded = true;
            return true;
        }

        return false;
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, object target)
    {
        parent.UpdateLayout();

        foreach (var item in parent.Items)
        {
            var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container == null) continue;

            if (ReferenceEquals(item, target) || SameDiskItem(item, target))
                return container;

            if (item is DiskFolderNode folder)
            {
                folder.IsExpanded = folder.IsExpanded || ContainsTarget(folder, target);
                container.IsExpanded = folder.IsExpanded;
                container.UpdateLayout();
            }

            var child = FindTreeViewItem(container, target);
            if (child != null) return child;
        }

        return null;
    }

    private static bool ContainsTarget(DiskFolderNode folder, object target)
    {
        if (SameDiskItem(folder, target)) return true;
        if (target is DiskFileItem targetFile &&
            folder.ChildFiles.Any(file => SameDiskItem(file, targetFile)))
            return true;
        return folder.ChildFolders.Any(child => ContainsTarget(child, target));
    }

    private static bool SameDiskItem(object left, object right)
    {
        return (left, right) switch
        {
            (DiskFolderNode a, DiskFolderNode b) => string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase),
            (DiskFileItem a, DiskFileItem b) => string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private void AddTreemapTile(TreemapEntry entry, Rect rect, int depth)
    {
        const double gap = 1.2;
        var tileRect = new Rect(rect.X + gap, rect.Y + gap, Math.Max(0, rect.Width - gap * 2), Math.Max(0, rect.Height - gap * 2));
        if (tileRect.Width < 1 || tileRect.Height < 1) return;

        var brush = GetTreemapBrush(entry, depth);
        var tile = new System.Windows.Shapes.Rectangle
        {
            Width = tileRect.Width,
            Height = tileRect.Height,
            Fill = brush,
            Stroke = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            StrokeThickness = 0.6,
            Opacity = 0.94,
            Cursor = entry.Folder == null && entry.File == null ? Cursors.Arrow : Cursors.Hand,
            Tag = entry
        };
        Canvas.SetLeft(tile, tileRect.X);
        Canvas.SetTop(tile, tileRect.Y);
        TreemapCanvas.Children.Add(tile);

        if (tileRect.Width >= 76 && tileRect.Height >= 34)
        {
            var showSize = tileRect.Width >= 120 && tileRect.Height >= 54;
            var label = new Border
            {
                Width = Math.Max(0, tileRect.Width - 8),
                Height = showSize ? 40 : 22,
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 2, 4, 2),
                IsHitTestVisible = false,
                ClipToBounds = true
            };

            if (showSize)
            {
                label.Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = entry.Name,
                            FontSize = 10,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.White,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = entry.SizeDisplay,
                            FontSize = 9,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                };
            }
            else
            {
                label.Child = new TextBlock
                {
                    Text = entry.Name,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            Canvas.SetLeft(label, tileRect.X + 4);
            Canvas.SetTop(label, tileRect.Y + 4);
            TreemapCanvas.Children.Add(label);
        }

        tile.ToolTip = $"{entry.Name}\n{entry.SizeDisplay}\n{entry.FullPath}";
        tile.MouseEnter += (_, _) =>
        {
            tile.Opacity = 1;
            ShowTreemapTooltip(entry, tileRect.X + 8, tileRect.Y + 8);
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Opacity = 0.92;
            HidePieTooltip();
        };
        tile.MouseLeftButtonDown += (_, _) =>
        {
            if (entry.Folder != null)
                DrillDownAndSelectFolder(entry.Folder);
            else if (entry.File != null)
                SelectTreeItem(entry.File);
        };
    }

    private static Brush GetTreemapBrush(TreemapEntry entry, int depth)
    {
        if (entry.File != null)
        {
            var ext = System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
            return ext switch
            {
                ".dll" or ".exe" or ".sys" => new SolidColorBrush(Color.FromRgb(72, 84, 255)),
                ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" => new SolidColorBrush(Color.FromRgb(52, 185, 76)),
                ".mp4" or ".mov" or ".mkv" or ".avi" => new SolidColorBrush(Color.FromRgb(190, 60, 180)),
                ".zip" or ".rar" or ".7z" or ".pak" => new SolidColorBrush(Color.FromRgb(198, 116, 16)),
                ".log" or ".txt" or ".json" or ".xml" => new SolidColorBrush(Color.FromRgb(172, 172, 22)),
                _ => new SolidColorBrush(Color.FromRgb(94, 94, 94))
            };
        }

        var palette = new[]
        {
            Color.FromRgb(28, 150, 218),
            Color.FromRgb(25, 150, 45),
            Color.FromRgb(132, 58, 210),
            Color.FromRgb(195, 58, 58),
            Color.FromRgb(184, 112, 20)
        };
        return new SolidColorBrush(palette[Math.Abs(entry.FullPath.GetHashCode()) % palette.Length]);
    }

    private readonly record struct TreemapEntry(
        string Name,
        string FullPath,
        long Size,
        string SizeDisplay,
        DiskFolderNode? Folder,
        DiskFileItem? File);

    private readonly record struct TreemapLayoutItem(TreemapEntry Entry, double Area);

    private void BuildSlice(double cx, double cy, double r, double start, double sweep, ChartItem item, int index)
    {
        double sRad = start * Math.PI / 180, eRad = (start + sweep) * Math.PI / 180;
        double midAngle = (start + sweep / 2) * Math.PI / 180;
        double x1 = cx + r * Math.Cos(sRad), y1 = cy + r * Math.Sin(sRad);
        double x2 = cx + r * Math.Cos(eRad), y2 = cy + r * Math.Sin(eRad);

        var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(x1, y1), true));
        fig.Segments.Add(new ArcSegment(new Point(x2, y2),
            new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        var brush = DiskAnalyzerViewModel.GetChartBrush(item.ColorIndex);

        var slice = new Path
        {
            Data = geo, Fill = brush, Stroke = Brushes.White,
            StrokeThickness = 1.5, Tag = item, Cursor = Cursors.Hand
        };

        // 悬停：显示提示 + 扇区放大
        slice.MouseEnter += (_, e) =>
        {
            item.IsHovered = true;
            ShowPieTooltip(item, cx + r * 0.6 * Math.Cos(midAngle) - 30,
                           cy + r * 0.6 * Math.Sin(midAngle) - 15);
            ScaleSlice(slice, cx, cy, 1.12);
        };
        slice.MouseLeave += (_, _) =>
        {
            item.IsHovered = false;
            HidePieTooltip();
            ScaleSlice(slice, cx, cy, 1.0);
        };
        slice.MouseLeftButtonDown += (_, _) => { if (!item.IsOther) VM.DrillDown(item); };

        // 入场动画
        slice.Opacity = 0;
        slice.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(300 + index * 60)));

        TreemapCanvas.Children.Add(slice);
    }

    private void ShowPieTooltip(ChartItem item, double x, double y)
    {
        if (_pieTooltip == null) return;
        _pieTooltip.Text = $"{item.Name}\n{item.SizeDisplay}  ({item.PercentageDisplay})";
        _pieTooltip.Visibility = Visibility.Visible;
    }

    private void ShowTreemapTooltip(TreemapEntry item, double x, double y)
    {
        if (_pieTooltip == null) return;
        _pieTooltip.Text = $"{item.Name}\n{item.SizeDisplay}";
        _pieTooltip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_pieTooltip, Math.Min(Math.Max(4, x), TreemapWidth - _pieTooltip.Width - 4));
        Canvas.SetTop(_pieTooltip, Math.Min(Math.Max(4, y), TreemapHeight - 48));
    }

    private void HidePieTooltip()
    {
        if (_pieTooltip != null) _pieTooltip.Visibility = Visibility.Collapsed;
    }

    private static void ScaleSlice(Path slice, double cx, double cy, double target)
    {
        var s = new ScaleTransform(1, 1) { CenterX = cx, CenterY = cy };
        slice.RenderTransform = s;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(150);
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, dur) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, dur) { EasingFunction = ease });
    }

    // ==================== 工具方法 ====================

    private static string TruncateName(string name, int maxLen)
    {
        if (name.Length <= maxLen) return name;
        return name[..(maxLen - 1)] + "…";
    }

    // ==================== 磁盘标签页 ====================

    private void OnDiskTabClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is DiskFolderNode node)
            (DataContext as DiskAnalyzerViewModel)!.SelectedDisk = node;
    }

    // ==================== 右键菜单 ====================

    private void OnOpenFolderLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is DiskFolderNode node)
            OpenInExplorer(node.FullPath);
    }

    private void OnOpenFileLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is DiskFileItem file)
            Process.Start("explorer.exe", $"/select,\"{file.FullPath}\"");
        else if (sender is MenuItem m && m.DataContext is DiskFolderNode folder)
            OpenInExplorer(folder.FullPath);
    }

    private void OnDeleteFile(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem m || m.DataContext is not DiskFileItem file) return;
        if (IsSystemProtected(file.FullPath))
        {
            MessageBox.Show($"「{file.Name}」位于系统保护目录中，不允许删除。\n\n路径: {file.FullPath}",
                "受保护的文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var r = MessageBox.Show(
            $"确认删除文件？\n\n「{file.Name}」\n大小: {file.SizeDisplay}\n路径: {file.FullPath}\n\n此操作不可恢复。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try { File.Delete(file.FullPath); }
        catch (Exception ex) { MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void OpenInExplorer(string p)
    {
        try { Process.Start("explorer.exe", $"\"{p}\""); }
        catch (Exception ex) { MessageBox.Show($"无法打开资源管理器: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static bool IsSystemProtected(string fp)
    {
        if (string.IsNullOrEmpty(fp)) return false;
        var n = fp.Replace("/", "\\").TrimEnd('\\') + "\\";
        return new[] { @"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\", @"C:\ProgramData\" }
            .Any(x => n.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }
}
