using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 磁盘空间分析视图 — 代码后置文件
/// 处理磁盘标签页点击、右键菜单事件
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

    /// <summary>
    /// 右键菜单 — 在资源管理器中打开文件夹
    /// </summary>
    private void OnOpenFolderLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is DiskFolderNode node)
            OpenInExplorer(node.FullPath);
    }

    /// <summary>
    /// 右键菜单 — 打开文件所在位置（并选中该文件）
    /// </summary>
    private void OnOpenFileLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is DiskFileItem file)
        {
            // 打开文件夹并选中文件
            Process.Start("explorer.exe", $"/select,\"{file.FullPath}\"");
        }
        else if (sender is MenuItem mi && mi.DataContext is DiskFolderNode folder)
        {
            OpenInExplorer(folder.FullPath);
        }
    }

    /// <summary>
    /// 右键菜单 — 删除文件（系统保护路径内的文件不允许删除）
    /// </summary>
    private void OnDeleteFile(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not DiskFileItem file)
            return;

        // 检查是否在系统保护路径中
        if (IsSystemProtected(file.FullPath))
        {
            MessageBox.Show(
                $"「{file.Name}」位于系统保护目录中，不允许删除。\n\n路径: {file.FullPath}",
                "受保护的文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确认删除文件？\n\n「{file.Name}」\n大小: {file.SizeDisplay}\n路径: {file.FullPath}\n\n此操作不可恢复。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(file.FullPath);
            MessageBox.Show($"文件已删除: {file.Name}", "删除成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 在资源管理器中打开指定路径
    /// </summary>
    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开资源管理器: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 检查文件是否在系统保护路径中
    /// </summary>
    private static bool IsSystemProtected(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var normalized = filePath.Replace("/", "\\").TrimEnd('\\') + "\\";
        string[] protectedPaths = {
            @"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\", @"C:\ProgramData\"
        };
        return protectedPaths.Any(p =>
            normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
