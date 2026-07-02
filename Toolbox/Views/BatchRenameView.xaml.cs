using System.Windows;
using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 批量重命名功能 View
/// 负责拖放事件处理和视图初始化
/// </summary>
public partial class BatchRenameView : UserControl
{
    /// <summary>绑定的 ViewModel</summary>
    private BatchRenameViewModel ViewModel => (BatchRenameViewModel)DataContext;

    /// <summary>
    /// 构造函数 — 初始化组件并绑定 ViewModel
    /// </summary>
    public BatchRenameView(BatchRenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 拖入时的视觉反馈
    /// </summary>
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 拖出时清除效果
    /// </summary>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// 放置文件 — 委托给 ViewModel
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
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
