using System.Windows;
using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 大文件扫描视图 — 代码后置文件
/// 处理文件 CheckBox 事件和删除按钮点击
/// </summary>
public partial class LargeFileScannerView : UserControl
{
    /// <summary>
    /// 构造函数，从 DI 容器获取 ViewModel 并绑定
    /// </summary>
    public LargeFileScannerView(LargeFileScannerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 文件 CheckBox 选中时通知 ViewModel
    /// </summary>
    private void OnFileChecked(object sender, RoutedEventArgs e)
    {
        NotifyViewModel();
    }

    /// <summary>
    /// 文件 CheckBox 取消选中时通知 ViewModel
    /// </summary>
    private void OnFileUnchecked(object sender, RoutedEventArgs e)
    {
        NotifyViewModel();
    }

    /// <summary>
    /// 通知 ViewModel 更新选中文件统计
    /// </summary>
    private void NotifyViewModel()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is LargeFileScannerViewModel vm)
                vm.OnFileSelectionChanged();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 删除按钮点击事件
    /// 直接删除对应单个文件
    /// </summary>
    private void OnDeleteFileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is LargeFileInfo file)
        {
            if (DataContext is LargeFileScannerViewModel vm)
            {
                // 选中该文件然后调用删除命令
                file.IsSelected = true;
                vm.DeleteSelectedCommand.Execute(null);
            }
        }
    }
}
