using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 文件粉碎项的模型
/// 表示待粉碎或已粉碎的文件，包含粉碎状态和后果说明
/// </summary>
public partial class FileShredderItem : ObservableObject
{
    /// <summary>文件路径</summary>
    [ObservableProperty]
    private string _filePath = "";

    /// <summary>文件名（不含路径）</summary>
    [ObservableProperty]
    private string _fileName = "";

    /// <summary>文件大小（人类可读格式）</summary>
    [ObservableProperty]
    private string _fileSize = "";

    /// <summary>粉碎状态</summary>
    [ObservableProperty]
    private ShredderItemStatus _status = ShredderItemStatus.Pending;

    /// <summary>后果说明文字（如"此文件粉碎后不可恢复！"）</summary>
    [ObservableProperty]
    private string _consequence = "粉碎后不可恢复！";

    /// <summary>是否为 SSD 磁盘上的文件（仅1次覆写）</summary>
    [ObservableProperty]
    private bool _isOnSSD;

    /// <summary>是否有硬链接（>1个链接指向同一数据）</summary>
    [ObservableProperty]
    private bool _hasHardLinks;

    /// <summary>硬链接数量</summary>
    [ObservableProperty]
    private int _hardLinkCount;

    /// <summary>是否为 EFS 加密文件</summary>
    [ObservableProperty]
    private bool _isEFSEncrypted;

    /// <summary>是否处于受保护路径</summary>
    [ObservableProperty]
    private bool _isProtectedPath;

    /// <summary>操作日志文本</summary>
    [ObservableProperty]
    private string _operationLog = "";
}

/// <summary>
/// 粉碎项状态枚举
/// </summary>
public enum ShredderItemStatus
{
    /// <summary>待粉碎</summary>
    Pending,
    /// <summary>粉碎成功</summary>
    Success,
    /// <summary>粉碎失败</summary>
    Failed,
    /// <summary>已跳过</summary>
    Skipped
}

/// <summary>
/// 覆写算法枚举
/// </summary>
public enum ShredderAlgorithm
{
    /// <summary>快速：1次覆写（随机数据），适用于 SSD 或快速清理</summary>
    Fast,
    /// <summary>安全：3次覆写（随机 + 补码 + 随机），符合美国国防部标准</summary>
    Secure,
    /// <summary>彻底：7次覆写（Gutmann 简化版），最高安全级别</summary>
    Thorough
}
