using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Toolbox.Helpers;

/// <summary>
/// 布尔值转 Visibility 转换器
/// true → Visible, false → Collapsed
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>正向转换</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b) return Visibility.Visible;
        return Visibility.Collapsed;
    }

    /// <summary>反向转换</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// 反向布尔值转 Visibility 转换器
/// true → Collapsed, false → Visible
/// </summary>
public class InvertBoolToVisibilityConverter : IValueConverter
{
    /// <summary>正向转换</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    /// <summary>反向转换</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v != Visibility.Visible;
    }
}

/// <summary>
/// 布尔值取反转换器
/// true → false, false → true
/// </summary>
public class InvertBoolConverter : IValueConverter
{
    /// <summary>正向转换 — 取反</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }

    /// <summary>反向转换 — 取反</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }
}

/// <summary>
/// 字符串非空转 Visibility 转换器
/// 非空/非空白 → Visible, 空 → Collapsed
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    /// <summary>正向转换</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        return !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>反向转换</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 整数百分比转字符串（如 0.75 → "75%"）
/// </summary>
public class PercentToStringConverter : IValueConverter
{
    /// <summary>正向转换</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d) return $"{d:P0}";
        if (value is int i) return $"{i}%";
        return "";
    }

    /// <summary>反向转换</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 字节大小格式化转换器
/// 将数字（字节数）转换为可读的大小字符串，如 "1.5 GB"
/// </summary>
public class BytesToSizeConverter : IValueConverter
{
    /// <summary>正向转换 — 将字节数转换为可读格式</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => (long)i,
            ulong ul => (long)ul,
            double d => (long)d,
            _ => 0
        };

        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>反向转换不支持</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 百分比格式化转换器
/// 将数字转换为百分比字符串，如 75 → "75%"
/// </summary>
public class PercentageConverter : IValueConverter
{
    /// <summary>正向转换 — 格式化百分比</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            uint u => $"{u}%",
            int i => $"{i}%",
            double d => $"{d:F0}%",
            _ => "--"
        };
    }

    /// <summary>反向转换不支持</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 枚举值相等转换器（用于 ComboBox 绑定枚举值）
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
            return Enum.Parse(targetType, parameter.ToString()!);
        return Binding.DoNothing;
    }
}

/// <summary>
/// 数量为 0 时折叠，否则显示
/// </summary>
public class ZeroCountToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            int count when count > 0 => Visibility.Visible,
            long count when count > 0 => Visibility.Visible,
            _ => Visibility.Collapsed
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 重要性枚举转可见性转换器
/// </summary>
public class ImportanceToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.TaskImportance imp && imp == Models.TaskImportance.Important)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 删除状态枚举转图标文本
/// </summary>
public class DeleteStatusToIconConverter : IValueConverter
{
    /// <summary>正向转换</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Models.DeleteItemStatus.Success => "OK",
            Models.DeleteItemStatus.Failed => "FAIL",
            _ => ""
        };
    }

    /// <summary>反向转换</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
