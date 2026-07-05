using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 导航分类分组（用于侧边栏分组显示）
/// </summary>
public class NavCategoryGroup
{
    /// <summary>分类名称</summary>
    public string Category { get; set; } = "";

    /// <summary>该分类下的导航项列表</summary>
    public List<NavItem> Items { get; set; } = new();

    /// <summary>是否已折叠</summary>
    public bool IsCollapsed { get; set; }
}

/// <summary>
/// 主窗口 ViewModel
/// 管理导航状态、主题切换、搜索过滤、设置面板
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly INavigationService _nav;
    private readonly IThemeService _theme;

    /// <summary>所有导航项的集合（与导航服务共享引用）</summary>
    [ObservableProperty]
    private ObservableCollection<NavItem> _navItems = new();

    /// <summary>当前选中的导航项</summary>
    [ObservableProperty]
    private NavItem? _selectedNavItem;

    /// <summary>搜索框文本，用于过滤导航项</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>是否使用暗色主题</summary>
    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>状态栏文本</summary>
    [ObservableProperty]
    private string _statusBarText = "就绪";

    /// <summary>设置面板是否打开</summary>
    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>
    /// 分组后的导航项，按分类（核心功能/重要功能/补充功能）分组
    /// 折叠的分类下不显示导航项
    /// </summary>
    public IEnumerable<NavCategoryGroup> GroupedNavItems
    {
        get
        {
            var groups = NavItems
                .GroupBy(n => n.Category)
                .Select(g => new NavCategoryGroup
                {
                    Category = g.Key,
                    Items = g.Where(i => !CollapsedCategories.Contains(g.Key)).ToList()
                })
                .ToList();

            return groups;
        }
    }

    /// <summary>已折叠的分类集合</summary>
    public HashSet<string> CollapsedCategories { get; } = new();

    /// <summary>
    /// 刷新分组导航项（折叠/展开后调用）
    /// </summary>
    public void RefreshGroupedNavItems()
    {
        OnPropertyChanged(nameof(GroupedNavItems));
    }

    /// <summary>
    /// 构造函数，通过 DI 注入导航服务和主题服务
    /// </summary>
    public MainViewModel(INavigationService nav, IThemeService theme)
    {
        _nav = nav;
        _theme = theme;

        // 共享导航服务的 NavItems 集合
        _navItems = nav.NavItems;
        InitializeDefaultCollapsedCategories();

        // 同步主题状态
        _isDarkTheme = _theme.CurrentTheme is ThemeType.Dark or ThemeType.Dashboard;
    }

    private void InitializeDefaultCollapsedCategories()
    {
        CollapsedCategories.Clear();

        foreach (var category in NavItems.Select(item => item.Category).Distinct())
        {
            if (!category.Contains("存储清理", StringComparison.Ordinal))
                CollapsedCategories.Add(category);
        }
    }

    /// <summary>
    /// 搜索文本变化时，过滤导航项
    /// 不包含搜索词的导航项会被隐藏（IsEnabled = false）
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        foreach (var item in NavItems)
        {
            if (string.IsNullOrEmpty(value))
            {
                // 搜索框为空时显示所有项
                item.IsEnabled = true;
            }
            else
            {
                // 按名称模糊匹配（不区分大小写）
                item.IsEnabled = item.Name.Contains(value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// 切换主题：亮色 ↔ 暗色
    /// </summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        _theme.SetTheme(IsDarkTheme ? ThemeType.Dark : ThemeType.Light);
    }

    /// <summary>
    /// 打开设置面板
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
    }

    /// <summary>
    /// 关闭设置面板
    /// </summary>
    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
    }
}
