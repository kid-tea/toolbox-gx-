using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// ContextMenuView 代码后台 — 右键菜单管理页面
/// </summary>
public partial class ContextMenuView : UserControl
{
    /// <summary>
    /// 通过 DI 注入 ViewModel，确保扫描/启用/删除命令可正常工作
    /// </summary>
    public ContextMenuView(ContextMenuViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

/// <summary>
/// 布尔值取反转换器（用于 InverseBoolConverter）
/// </summary>
public class InverseBoolConverter : IValueConverter
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
