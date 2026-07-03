using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 重复文件查找视图 — 代码后置文件
/// 构造函数接收 ViewModel 并绑定
/// </summary>
public partial class DuplicateFinderView : UserControl
{
    /// <summary>
    /// 构造函数，从 DI 容器获取 ViewModel 并绑定为 DataContext
    /// </summary>
    public DuplicateFinderView(DuplicateFinderViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
