using System.Windows;
using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 内存释放视图代码后置
/// 通过 DI 注入 ViewModel，确保命令、状态和列表数据可正常显示
/// </summary>
public partial class MemoryReleaseView : UserControl
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public MemoryReleaseView(MemoryReleaseViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MemoryReleaseViewModel vm && vm.RefreshMemoryInfoCommand.CanExecute(null))
        {
            vm.RefreshMemoryInfoCommand.Execute(null);
        }
    }
}
