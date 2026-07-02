using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 文件删除项的模型
/// 表示待删除或已删除的文件，包含删除结果状态和占用进程信息
/// </summary>
public partial class FileDeleteItem : ObservableObject
{
    /// <summary>文件路径</summary>
    [ObservableProperty]
    private string _filePath = "";

    /// <summary>文件名（不含路径）</summary>
    [ObservableProperty]
    private string _fileName = "";

    /// <summary>文件大小（用于显示，如"1.5 MB"）</summary>
    [ObservableProperty]
    private string _fileSize = "";

    /// <summary>删除状态：Pending(待删除), Success(成功), Failed(失败)</summary>
    [ObservableProperty]
    private DeleteItemStatus _status = DeleteItemStatus.Pending;

    /// <summary>结果图标：✅ 或 ❌</summary>
    [ObservableProperty]
    private string _resultIcon = "";

    /// <summary>错误消息（中文）</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    /// <summary>错误消息（英文）</summary>
    [ObservableProperty]
    private string _errorMessageEn = "";

    /// <summary>占用该文件的进程名称列表（逗号分隔）</summary>
    [ObservableProperty]
    private string _lockingProcessNames = "";

    /// <summary>是否为快捷方式(.lnk)文件</summary>
    [ObservableProperty]
    private bool _isShortcut;

    /// <summary>如果是快捷方式，指向的目标路径</summary>
    [ObservableProperty]
    private string _shortcutTarget = "";

    /// <summary>是否位于系统受保护路径（如 C:\Windows）</summary>
    [ObservableProperty]
    private bool _isProtected;
}

/// <summary>
/// 删除状态枚举
/// </summary>
public enum DeleteItemStatus
{
    /// <summary>待删除</summary>
    Pending,
    /// <summary>删除成功</summary>
    Success,
    /// <summary>删除失败</summary>
    Failed
}
