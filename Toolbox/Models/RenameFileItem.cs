using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 重命名文件项的模型
/// 包含原文件名、新文件名预览、预览警告和重命名状态
/// </summary>
public partial class RenameFileItem : ObservableObject
{
    /// <summary>文件完整路径（原始）</summary>
    [ObservableProperty]
    private string _filePath = "";

    /// <summary>原始文件名（含扩展名）</summary>
    [ObservableProperty]
    private string _originalName = "";

    /// <summary>预览的新文件名（含扩展名）</summary>
    [ObservableProperty]
    private string _newName = "";

    /// <summary>原始目录路径（不含文件名）</summary>
    [ObservableProperty]
    private string _directory = "";

    /// <summary>文件扩展名（含点号）</summary>
    [ObservableProperty]
    private string _extension = "";

    /// <summary>原始文件名（不含扩展名）</summary>
    [ObservableProperty]
    private string _nameWithoutExtension = "";

    /// <summary>重命名状态</summary>
    [ObservableProperty]
    private RenameItemStatus _status = RenameItemStatus.Pending;

    /// <summary>预览警告类型</summary>
    [ObservableProperty]
    private RenameWarningType _warning = RenameWarningType.None;

    /// <summary>警告文本</summary>
    [ObservableProperty]
    private string _warningMessage = "";

    /// <summary>是否为红色警告（严重问题：保留名、空文件名、非法字符）</summary>
    [ObservableProperty]
    private bool _isErrorWarning;

    /// <summary>是否为黄色警告（非严重：目标已存在、路径过长）</summary>
    [ObservableProperty]
    private bool _isCautionWarning;
}

/// <summary>
/// 重命名项状态枚举
/// </summary>
public enum RenameItemStatus
{
    /// <summary>待重命名</summary>
    Pending,
    /// <summary>重命名成功</summary>
    Success,
    /// <summary>重命名失败</summary>
    Failed
}

/// <summary>
/// 重命名预览警告类型
/// </summary>
public enum RenameWarningType
{
    /// <summary>无警告</summary>
    None,
    /// <summary>Windows 保留文件名（如 CON, PRN, NUL 等）</summary>
    ReservedName,
    /// <summary>包含非法字符（如 < > : " / \ | ? *）</summary>
    IllegalChars,
    /// <summary>文件名为空</summary>
    EmptyName,
    /// <summary>目标文件名已存在</summary>
    TargetExists,
    /// <summary>路径过长（超过 260 字符）</summary>
    PathTooLong,
    /// <summary>文件被占用</summary>
    FileLocked,
    /// <summary>权限不足</summary>
    AccessDenied
}
