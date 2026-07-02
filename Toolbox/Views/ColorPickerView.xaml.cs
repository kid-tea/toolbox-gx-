using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 取色器视图代码后置
/// 处理颜色格式点击复制事件
/// </summary>
public partial class ColorPickerView : UserControl
{
    private ColorPickerViewModel VM => (ColorPickerViewModel)DataContext;

    /// <summary>
    /// 构造函数
    /// 通过 DI 注入 ViewModel，确保开始取色命令和历史列表可正常工作
    /// </summary>
    public ColorPickerView(ColorPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// HEX 值点击复制
    /// </summary>
    private void OnHexClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(VM.HexValue);
            VM.StatusMessage = $"已复制: {VM.HexValue}";
        }
        catch
        {
            VM.StatusMessage = "复制失败，请重试";
        }
    }

    /// <summary>
    /// RGB 值点击复制
    /// </summary>
    private void OnRgbClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(VM.RgbValue);
            VM.StatusMessage = $"已复制: {VM.RgbValue}";
        }
        catch
        {
            VM.StatusMessage = "复制失败，请重试";
        }
    }

    /// <summary>
    /// HSL 值点击复制
    /// </summary>
    private void OnHslClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(VM.HslValue);
            VM.StatusMessage = $"已复制: {VM.HslValue}";
        }
        catch
        {
            VM.StatusMessage = "复制失败，请重试";
        }
    }
}
