using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// RegistryCleanerView 代码后台 — 注册表清理 + 高级电源选项页面
/// </summary>
public partial class RegistryCleanerView : UserControl
{
    /// <summary>
    /// 通过 DI 注入 ViewModel，确保扫描/修复/备份命令和结果列表可正常工作
    /// </summary>
    public RegistryCleanerView(RegistryCleanerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

/// <summary>
/// 严重程度转文字颜色转换器
/// </summary>
public class SeverityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IssueSeverity severity)
        {
            var color = severity switch
            {
                IssueSeverity.High => Color.FromRgb(231, 76, 60),
                IssueSeverity.Medium => Color.FromRgb(243, 156, 18),
                IssueSeverity.Low => Color.FromRgb(39, 174, 96),
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
/// 严重程度转背景色转换器
/// </summary>
public class SeverityToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IssueSeverity severity)
        {
            var color = severity switch
            {
                IssueSeverity.High => Color.FromRgb(231, 76, 60),
                IssueSeverity.Medium => Color.FromRgb(243, 156, 18),
                IssueSeverity.Low => Color.FromRgb(39, 174, 96),
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
