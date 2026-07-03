using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 导航项模型
/// 用于侧边栏导航，每个导航项对应一个功能页面
/// 继承 ObservableObject 以支持 UI 属性变更通知
/// </summary>
public partial class NavItem : ObservableObject
{
    /// <summary>功能名称，如"C盘清理"、"文件强制删除"等</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>所属分类：核心功能 / 重要功能 / 补充功能</summary>
    [ObservableProperty]
    private string _category = "";

    /// <summary>分类图标：⭐ / ✅ / 📦</summary>
    [ObservableProperty]
    private string _icon = "";

    /// <summary>对应的 View 类型，导航时用于动态加载</summary>
    [ObservableProperty]
    private Type? _viewType;

    /// <summary>是否需要管理员权限才能使用</summary>
    [ObservableProperty]
    private bool _requiresAdmin;

    /// <summary>当前是否可用（搜索过滤时用到）</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>是否处于待优化状态（点击时提示"功能优化中，暂不可用"）</summary>
    [ObservableProperty]
    private bool _isPendingOptimization;

    /// <summary>是否被选中（当前页面）</summary>
    [ObservableProperty]
    private bool _isSelected;
}
