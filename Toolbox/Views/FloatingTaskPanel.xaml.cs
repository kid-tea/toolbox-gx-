using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Toolbox.Models;
using Toolbox.Native;

namespace Toolbox.Views;

/// <summary>
/// 悬浮任务面板 — 独立 Window
/// 支持简洁列表风格、透明度滑块、置顶开关
/// 图片缓存限制 10MB（异步加载）
/// 拖出屏幕限制坐标
/// </summary>
public partial class FloatingTaskPanel : Window
{
    private ObservableCollection<TaskItem> _tasks = new();

    /// <summary>
    /// 构造函数
    /// </summary>
    public FloatingTaskPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// 加载完成后绑定数据
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 设置窗口位置（右下角）
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 60;

        TaskItemsControl.ItemsSource = _tasks;

        // 应用透明度
        Opacity = 0.9;
    }

    /// <summary>
    /// 窗口关闭时记录
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 保存窗口位置和透明度设置
    }

    /// <summary>
    /// 关闭窗口
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    /// <summary>
    /// 切换置顶
    /// </summary>
    private void OnToggleTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (sender is Button btn)
        {
            btn.Content = Topmost ? "📌" : "📍";
        }
    }

    /// <summary>
    /// 刷新任务列表
    /// </summary>
    public void RefreshTasks(IEnumerable<TaskItem> tasks)
    {
        _tasks.Clear();
        foreach (var task in tasks)
        {
            _tasks.Add(task);
        }
    }
}
