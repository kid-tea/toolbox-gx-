using System.Windows;
using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 文件强制删除功能的 View
/// 负责拖放事件处理，将用户操作委托给 ViewModel
/// </summary>
public partial class ForceDeleteView : UserControl
{
    /// <summary>绑定的 ViewModel</summary>
    private ForceDeleteViewModel ViewModel => (ForceDeleteViewModel)DataContext;

    /// <summary>
    /// 构造函数 — 初始化组件并设置 DataContext
    /// </summary>
    public ForceDeleteView(ForceDeleteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AllowDrop = true;
    }

    /// <summary>
    /// 拖入文件时的视觉反馈（高亮边框）
    /// </summary>
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (sender is Border border)
            {
                border.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
                border.BorderThickness = new Thickness(3);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 拖出拖放区域时恢复边框样式
    /// </summary>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
            border.BorderThickness = new Thickness(1);
        }
        e.Handled = true;
    }

    /// <summary>
    /// 放置文件事件 — 将文件路径传递给 ViewModel 处理
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
            border.BorderThickness = new Thickness(1);
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                ViewModel.AddFiles(files);
            }
        }
        e.Handled = true;
    }
}
