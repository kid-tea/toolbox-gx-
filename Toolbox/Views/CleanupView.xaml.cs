using System.Windows;
using System.Windows.Controls;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// C盘清理视图 — 代码后置文件
/// 负责处理 CheckBox 事件，通知 ViewModel 更新选中项统计
/// </summary>
public partial class CleanupView : UserControl
{
    /// <summary>
    /// 构造函数，从 DI 容器获取 ViewModel 并绑定为 DataContext
    /// </summary>
    public CleanupView(CleanupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 清理项 CheckBox 选中时，通知 ViewModel
    /// </summary>
    private void OnCleanupItemChecked(object sender, RoutedEventArgs e)
    {
        NotifyViewModel();
    }

    /// <summary>
    /// 清理项 CheckBox 取消选中时，通知 ViewModel
    /// </summary>
    private void OnCleanupItemUnchecked(object sender, RoutedEventArgs e)
    {
        NotifyViewModel();
    }

    /// <summary>
    /// 通知 ViewModel 清理项选中状态发生变化
    /// 延迟调用以避免 ItemCheckBox 绑定同步问题
    /// </summary>
    private void NotifyViewModel()
    {
        // 使用 Dispatcher 延迟调用，确保 CheckBox 绑定先完成更新
        if (DataContext is CleanupViewModel vm)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                vm.OnItemSelectionChanged();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
