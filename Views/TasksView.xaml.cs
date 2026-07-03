using System.Windows;
using System.Windows.Controls;
using Toolbox.Models;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 任务视图代码后置
/// 处理列表中暂停/删除按钮的点击事件
/// </summary>
public partial class TasksView : UserControl
{
    private TasksViewModel VM => (TasksViewModel)DataContext;

    /// <summary>
    /// 构造函数
    /// 通过 DI 注入 ViewModel，确保命令和绑定可正常工作
    /// </summary>
    public TasksView(TasksViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 勾选/取消勾选任务完成状态
    /// </summary>
    private void OnToggleTaskCompleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TaskItem task)
        {
            VM.ToggleTaskCompleteCommand.Execute(task);
        }
    }

    /// <summary>
    /// 暂停/恢复任务按钮点击
    /// </summary>
    private void OnPauseTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TaskItem task)
        {
            VM.ToggleTaskPauseCommand.Execute(task);
        }
    }

    /// <summary>
    /// 删除任务按钮点击
    /// </summary>
    private void OnDeleteTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TaskItem task)
        {
            VM.DeleteTaskCommand.Execute(task);
        }
    }
}
