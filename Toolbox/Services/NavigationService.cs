using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Toolbox.Models;

namespace Toolbox.Services;

/// <summary>
/// 导航服务接口
/// </summary>
public interface INavigationService
{
    /// <summary>所有导航项集合</summary>
    ObservableCollection<NavItem> NavItems { get; }

    /// <summary>当前选中的导航项</summary>
    NavItem? SelectedItem { get; set; }

    /// <summary>当前显示的 View 类型</summary>
    Type? CurrentViewType { get; }

    /// <summary>导航变化事件</summary>
    event Action<Type?>? NavigationChanged;

    /// <summary>导航到指定 View 类型</summary>
    void Navigate(Type viewType);
}

/// <summary>
/// 导航服务实现
/// 管理侧边栏导航项和页面切换
/// 导航项由 App.xaml.cs 在启动时注册
/// </summary>
public partial class NavigationService : ObservableObject, INavigationService
{
    /// <summary>所有导航项的可观察集合</summary>
    [ObservableProperty]
    private ObservableCollection<NavItem> _navItems = new();

    /// <summary>当前选中的导航项</summary>
    [ObservableProperty]
    private NavItem? _selectedItem;

    /// <summary>当前显示的页面类型</summary>
    [ObservableProperty]
    private Type? _currentViewType;

    /// <summary>当导航目标变化时触发</summary>
    public event Action<Type?>? NavigationChanged;

    public NavigationService()
    {
        // 导航项由 App.xaml.cs 在启动流程中填充
    }

    /// <summary>
    /// 导航到指定的页面类型
    /// </summary>
    /// <param name="viewType">View 的 Type 类型</param>
    public void Navigate(Type viewType)
    {
        CurrentViewType = viewType;
        NavigationChanged?.Invoke(viewType);
    }

    /// <summary>
    /// 当选中的导航项变化时，自动触发导航并同步选中状态
    /// </summary>
    partial void OnSelectedItemChanged(NavItem? value)
    {
        // 清除所有项的选中状态
        foreach (var item in NavItems)
        {
            item.IsSelected = false;
        }

        // 标记当前项为选中
        if (value != null)
        {
            value.IsSelected = true;
            if (value.ViewType != null)
                Navigate(value.ViewType);
        }
    }
}
