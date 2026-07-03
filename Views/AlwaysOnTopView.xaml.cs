using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 窗口置顶视图代码后置
/// 处理窗口初始化、取消置顶按钮点击事件
/// </summary>
public partial class AlwaysOnTopView : UserControl
{
    private AlwaysOnTopViewModel VM => (AlwaysOnTopViewModel)DataContext;

    /// <summary>
    /// 构造函数
    /// 通过 DI 注入 ViewModel，确保按钮命令和列表数据正常工作
    /// </summary>
    public AlwaysOnTopView(AlwaysOnTopViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 视图加载完成后初始化 ViewModel
    /// 传入工具箱窗口句柄用于排除自身
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlwaysOnTopViewModel vm)
        {
            var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            vm.Initialize(hwnd);
            if (vm.RefreshForegroundWindowCommand.CanExecute(null))
            {
                vm.RefreshForegroundWindowCommand.Execute(null);
            }
        }
    }

    /// <summary>
    /// 取消置顶按钮点击事件
    /// 从按钮的 Tag 获取对应的窗口项并调用取消置顶
    /// </summary>
    private void OnRemoveTopmostClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AlwaysOnTopViewModel.TopmostWindowItem item)
        {
            VM.RemoveTopmostCommand.Execute(item);
        }
    }
}
