using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 磁盘空间分析视图 — 代码后置文件
/// 处理磁盘标签页点击事件
/// </summary>
public partial class DiskAnalyzerView : UserControl
{
    /// <summary>
    /// 构造函数，从 DI 容器获取 ViewModel 并绑定
    /// </summary>
    public DiskAnalyzerView(DiskAnalyzerViewModel viewModel)
    {
        try
        {
            InitializeComponent();
            DataContext = viewModel;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiskAnalyzerView 初始化失败: {ex}");
            // 创建最小化内容，避免整个页面无法显示
            Content = new TextBlock
            {
                Text = $"磁盘分析页加载失败:\n{ex.Message}",
                FontSize = 14,
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    /// <summary>
    /// 磁盘标签页点击事件
    /// </summary>
    private void OnDiskTabClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is DiskFolderNode node)
        {
            if (DataContext is DiskAnalyzerViewModel vm)
            {
                vm.SelectedDisk = node;
            }
        }
    }
}
