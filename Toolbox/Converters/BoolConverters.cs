using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Toolbox.Converters;

/// <summary>
/// 布尔值反转转换器
/// true → false, false → true
/// 用于如"正在扫描时禁用扫描按钮"的场景
/// </summary>
public class BoolInverseConverter : IValueConverter
{
    /// <summary>反转布尔值</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return false;
    }

    /// <summary>反转回去（同样操作）</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return false;
    }
}

/// <summary>
/// 布尔值到可见性转换器
/// true → Visible, false → Collapsed
/// 用于根据 Boolean 属性控制 UI 元素显隐
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>bool → Visibility</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    /// <summary>Visibility → bool</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
            return visibility == Visibility.Visible;
        return false;
    }
}
