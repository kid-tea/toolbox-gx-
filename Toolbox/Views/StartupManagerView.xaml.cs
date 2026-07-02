using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// StartupManagerView 代码后台 — 开机启动项管理页面
/// </summary>
public partial class StartupManagerView : UserControl
{
    /// <summary>
    /// 通过 DI 注入 ViewModel，确保扫描和导出命令可正常工作
    /// </summary>
    public StartupManagerView(StartupManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

/// <summary>
/// 影响程度转颜色转换器（文字颜色）
/// 🟢低=绿 🟡中=黄 🔴高=红 🟠文件丢失=橙 ⚫不可用=灰
/// </summary>
public class ImpactToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ImpactLevel impact)
        {
            var color = impact switch
            {
                ImpactLevel.Low => Color.FromRgb(39, 174, 96),
                ImpactLevel.Medium => Color.FromRgb(243, 156, 18),
                ImpactLevel.High => Color.FromRgb(231, 76, 60),
                ImpactLevel.FileMissing => Color.FromRgb(230, 126, 34),
                ImpactLevel.Unavailable => Color.FromRgb(149, 165, 166),
                _ => Color.FromRgb(149, 165, 166)
            };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 影响程度转背景颜色转换器（标签背景色）
/// </summary>
public class ImpactToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ImpactLevel impact)
        {
            var color = impact switch
            {
                ImpactLevel.Low => Color.FromRgb(39, 174, 96),
                ImpactLevel.Medium => Color.FromRgb(243, 156, 18),
                ImpactLevel.High => Color.FromRgb(231, 76, 60),
                ImpactLevel.FileMissing => Color.FromRgb(230, 126, 34),
                ImpactLevel.Unavailable => Color.FromRgb(127, 140, 141),
                _ => Color.FromRgb(127, 140, 141)
            };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
