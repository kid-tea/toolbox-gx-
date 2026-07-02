using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.ViewModels;

/// <summary>
/// ViewModel 基类 — 所有 ViewModel 的公共基础
/// 继承 CommunityToolkit.Mvvm 的 ObservableObject 以获得属性变更通知能力
/// 提供通用的忙碌状态、进度、状态消息等属性
/// </summary>
public partial class ViewModelBase : ObservableObject
{
    /// <summary>
    /// 是否正在执行耗时操作
    /// 用于控制加载动画、进度条、按钮禁用等
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 当前状态消息，显示在状态栏或进度区域
    /// 如"正在扫描..."、"清理完成"等
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>
    /// 进度条当前值（0 - ProgressMax）
    /// 用于确定性进度展示
    /// </summary>
    [ObservableProperty]
    private int _progressValue;

    /// <summary>
    /// 进度条最大值，默认为 100
    /// </summary>
    [ObservableProperty]
    private int _progressMax = 100;

    /// <summary>
    /// 是否使用不确定进度模式（动画效果）
    /// 用于无法估算进度的操作
    /// </summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;
}
