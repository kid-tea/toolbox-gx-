using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// ScheduledShutdownView 代码后台 — 定时关机页面
/// </summary>
public partial class ScheduledShutdownView : UserControl
{
    /// <summary>
    /// 通过 DI 注入 ViewModel，确保操作类型、倒计时和任务列表可正常工作
    /// </summary>
    public ScheduledShutdownView(ScheduledShutdownViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

// ==================== 值转换器 ====================

/// <summary>
/// 任务状态转显示文字转换器
/// </summary>
public class TaskStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ShutdownTaskStatus status)
        {
            return status switch
            {
                ShutdownTaskStatus.Pendiente => "等待中",
                ShutdownTaskStatus.Executing => "执行中",
                ShutdownTaskStatus.Executed => "已完成",
                ShutdownTaskStatus.Cancelled => "已取消",
                ShutdownTaskStatus.Missed => "已过期",
                _ => status.ToString()
            };
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 任务状态转颜色转换器（已执行/已取消灰色，等待中蓝色，执行中绿色，过期红色）
/// </summary>
public class TaskStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ShutdownTaskStatus status)
        {
            var brush = status switch
            {
                ShutdownTaskStatus.Pendiente => new SolidColorBrush(Color.FromRgb(52, 152, 219)),   // 蓝色
                ShutdownTaskStatus.Executing => new SolidColorBrush(Color.FromRgb(46, 204, 113)),    // 绿色
                ShutdownTaskStatus.Executed => new SolidColorBrush(Color.FromRgb(149, 165, 166)),    // 灰色
                ShutdownTaskStatus.Cancelled => new SolidColorBrush(Color.FromRgb(149, 165, 166)),   // 灰色
                ShutdownTaskStatus.Missed => new SolidColorBrush(Color.FromRgb(231, 76, 60)),        // 红色
                _ => new SolidColorBrush(Color.FromRgb(149, 165, 166))
            };
            brush.Freeze(); // 冻结以提高性能
            return brush;
        }
        return new SolidColorBrush(Color.FromRgb(149, 165, 166));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 布尔值取反转换器
/// </summary>
public class NegateBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }
}

/// <summary>
/// Count=0 时显示（Visibility），Count>0 时隐藏
/// 用于显示"暂无任务"提示
/// </summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Count>0 时显示（Visibility），Count=0 时隐藏
/// 用于显示任务列表
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
