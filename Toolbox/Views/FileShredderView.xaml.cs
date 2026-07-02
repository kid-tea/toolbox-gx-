using System.Windows;
using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 文件粉碎功能 View
/// 负责拖放事件处理和视图初始化
/// </summary>
public partial class FileShredderView : UserControl
{
    /// <summary>绑定的 ViewModel</summary>
    private FileShredderViewModel ViewModel => (FileShredderViewModel)DataContext;

    /// <summary>
    /// 构造函数 — 初始化组件并绑定 ViewModel
    /// </summary>
    public FileShredderView(FileShredderViewModel viewModel)
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
    /// 拖入时的视觉反馈
    /// </summary>
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZone.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
            DropZone.BorderThickness = new Thickness(3);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 拖出时恢复边框
    /// </summary>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        DropZone.BorderThickness = new Thickness(2);
        e.Handled = true;
    }

    /// <summary>
    /// 放置文件
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        DropZone.BorderThickness = new Thickness(2);

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
