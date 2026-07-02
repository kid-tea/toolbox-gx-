using System.Collections.ObjectModel;
using System.IO;
using Toolbox.Native;

namespace Toolbox.Services;

/// <summary>
/// 磁盘空间分析服务接口
/// 扫描磁盘目录树，计算各文件夹大小，支持进度报告和取消
/// </summary>
public interface IDiskAnalyzerService
{
    /// <summary>获取所有固定磁盘驱动器</summary>
    List<DriveInfo> GetFixedDrives();

    /// <summary>
    /// 异步扫描指定磁盘的目录树
    /// 使用 EnumerateFiles 流式枚举，32 层递归限制，排除非固定磁盘
    /// </summary>
    /// <param name="rootPath">要扫描的根路径（如 C:\）</param>
    /// <param name="progress">进度报告，报告当前扫描的目录</param>
    /// <param name="token">取消令牌</param>
    /// <returns>根目录节点，包含完整树结构</returns>
    Task<DiskFolderNode> ScanDiskAsync(string rootPath, IProgress<string> progress, CancellationToken token);

    /// <summary>
    /// 指定路径是否在受保护位置内
    /// </summary>
    bool IsProtected(string path);
}

/// <summary>
/// 磁盘文件夹节点模型
/// 表示树形目录结构中的一个节点，支持数据绑定
/// </summary>
public class DiskFolderNode
{
    /// <summary>文件夹名称</summary>
    public string Name { get; set; } = "";

    /// <summary>文件夹完整路径</summary>
    public string FullPath { get; set; } = "";

    /// <summary>此文件夹下所有文件的总大小（字节）</summary>
    public long TotalSize { get; set; }

    /// <summary>格式化后的大小字符串</summary>
    public string SizeDisplay => FormatSize(TotalSize);

    /// <summary>此文件夹下的文件数量</summary>
    public int FileCount { get; set; }

    /// <summary>占父文件夹大小的百分比</summary>
    public double Percentage { get; set; }

    /// <summary>百分比显示（用于饼图）</summary>
    public string PercentageDisplay => $"{Percentage:0.#}%";

    /// <summary>子文件夹节点列表</summary>
    public ObservableCollection<DiskFolderNode> Children { get; set; } = new();

    /// <summary>是否处于展开状态（用于 TreeView）</summary>
    public bool IsExpanded { get; set; }

    /// <summary>是否在受保护路径中</summary>
    public bool IsProtected { get; set; }

    /// <summary>是否有子节点</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>深度层级（0=根）</summary>
    public int Depth { get; set; }

    /// <summary>
    /// 格式化字节数为可读字符串
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }
}

/// <summary>
/// 磁盘空间分析服务实现
/// 扫描指定磁盘的目录树大小
/// 特性：
///   - EnumerateFiles 流式枚举（不一次性加载所有文件）
///   - 32 层递归深度限制
///   - 只扫描固定磁盘（DRIVE_FIXED）
///   - 处理权限不足（UnauthorizedAccessException）和磁盘弹出（DirectoryNotFoundException）
///   - 跳过符号链接
///   - 支持 CancellationToken 取消
/// </summary>
public class DiskAnalyzerService : IDiskAnalyzerService
{
    private readonly ILogService _log;
    private const int MaxRecursionDepth = 32; // 最大递归深度

    /// <summary>构造函数，注入日志服务</summary>
    public DiskAnalyzerService(ILogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 获取系统中所有固定磁盘驱动器
    /// 排除 CD-ROM、可移动磁盘、网络映射等
    /// </summary>
    /// <returns>固定磁盘驱动器列表</returns>
    public List<DriveInfo> GetFixedDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives();
            return drives.Where(d =>
            {
                try
                {
                    // 只返回已就绪且类型为固定磁盘的驱动器
                    return d.IsReady && d.DriveType == DriveType.Fixed;
                }
                catch
                {
                    return false; // 弹出或权限不足的磁盘跳过
                }
            }).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError("获取磁盘列表失败", ex);
            return new List<DriveInfo>();
        }
    }

    /// <summary>
    /// 扫描指定根路径的完整目录树
    /// </summary>
    public async Task<DiskFolderNode> ScanDiskAsync(
        string rootPath, IProgress<string> progress, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            var root = new DiskFolderNode
            {
                Name = rootPath,
                FullPath = rootPath,
                Depth = 0,
                IsProtected = IsProtected(rootPath),
                IsExpanded = true
            };

            progress.Report($"正在分析: {rootPath}");
            ScanDirectoryIterative(root, rootPath, token, progress);

            return root;
        }, token);
    }

    /// <summary>
    /// 栈式迭代扫描目录（替代递归，避免 StackOverflowException）
    /// 使用 Stack 模拟深度优先遍历，最大深度 32 层
    /// 每个节点扫描完成后向上回溯累加大小
    /// </summary>
    private void ScanDirectoryIterative(
        DiskFolderNode root, string rootPath,
        CancellationToken token, IProgress<string> progress)
    {
        // 栈元素: (节点, 目录路径, 深度)
        var stack = new Stack<(DiskFolderNode Node, string Path, int Depth)>();

        stack.Push((root, rootPath, 0));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var (node, path, depth) = stack.Pop();

            long totalSize = 0;
            int fileCount = 0;
            var subDirs = new List<string>();

            try
            {
                // 流式枚举文件
                foreach (var file in Directory.EnumerateFiles(
                    path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }))
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            totalSize += fileInfo.Length;
                            fileCount++;
                        }
                    }
                    catch
                    {
                        // 跳过无法访问的文件
                    }
                }

                // 获取子目录列表
                if (depth < MaxRecursionDepth)
                {
                    foreach (var dir in Directory.EnumerateDirectories(
                        path, "*", new EnumerationOptions
                        {
                            RecurseSubdirectories = false,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint
                        }))
                    {
                        subDirs.Add(dir);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _log.LogWarning($"无权访问目录: {path}");
            }
            catch (DirectoryNotFoundException)
            {
                _log.LogWarning($"目录不存在或磁盘已弹出: {path}");
            }
            catch (PathTooLongException)
            {
                _log.LogWarning($"路径过长: {path}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"扫描目录 {path} 时出错: {ex.Message}");
            }

            node.TotalSize = totalSize;
            node.FileCount = fileCount;

            // 处理子目录：创建子节点并推入栈
            foreach (var dir in subDirs)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    var dirName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(dirName))
                        dirName = dir;

                    var childNode = new DiskFolderNode
                    {
                        Name = dirName,
                        FullPath = dir,
                        Depth = depth + 1,
                        IsProtected = IsProtected(dir)
                    };

                    // 关键：将子节点添加到父节点的 Children 集合中
                    node.Children.Add(childNode);

                    progress.Report($"正在分析: {dir}");
                    stack.Push((childNode, dir, depth + 1));
                }
                catch (UnauthorizedAccessException)
                {
                    _log.LogWarning($"无权访问目录: {dir}");
                }
                catch (DirectoryNotFoundException)
                {
                    _log.LogWarning($"目录不存在或磁盘已弹出: {dir}");
                }
                catch (PathTooLongException)
                {
                    _log.LogWarning($"路径过长: {dir}");
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"扫描目录 {dir} 时出错: {ex.Message}");
                }
            }
        }

        // 后处理：从叶子向上累加大小、排序、计算百分比
        PostProcessTree(root, token);
    }

    /// <summary>
    /// 后处理树结构：从叶子向上累加大小，排序子节点，计算百分比，移除空目录
    /// </summary>
    private void PostProcessTree(DiskFolderNode node, CancellationToken token)
    {
        // 使用栈做后序遍历
        var postStack = new Stack<DiskFolderNode>();
        var resultStack = new Stack<DiskFolderNode>();
        postStack.Push(node);

        while (postStack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = postStack.Pop();
            resultStack.Push(current);

            foreach (var child in current.Children)
            {
                postStack.Push(child);
            }
        }

        // resultStack 现在包含后序遍历顺序（子节点在父节点之前）
        while (resultStack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = resultStack.Pop();

            if (current.Children.Count > 0)
            {
                // 先累加子节点的大小
                long childTotalSize = 0;
                int childFileCount = 0;
                foreach (var child in current.Children)
                {
                    childTotalSize += child.TotalSize;
                    childFileCount += child.FileCount;
                }

                // 合并后的总大小 = 当前目录文件 + 所有子目录
                long mergedTotal = current.TotalSize + childTotalSize;

                // 基于合并后的总大小计算百分比
                foreach (var child in current.Children)
                {
                    child.Percentage = mergedTotal > 0
                        ? (double)child.TotalSize / mergedTotal * 100
                        : 0;
                }

                current.TotalSize = mergedTotal;
                current.FileCount += childFileCount;

                // 移除空目录（无文件且无有效子目录）
                var emptyChildren = current.Children
                    .Where(c => c.TotalSize <= 0 && c.FileCount <= 0)
                    .ToList();
                foreach (var empty in emptyChildren)
                {
                    current.Children.Remove(empty);
                }

                // 按大小降序排列
                var sorted = current.Children.OrderByDescending(c => c.TotalSize).ToList();
                current.Children.Clear();
                foreach (var child in sorted)
                {
                    current.Children.Add(child);
                }
            }
        }
    }

    /// <summary>
    /// 判断路径是否在受保护位置内
    /// </summary>
    public bool IsProtected(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = path.Replace("/", "\\").TrimEnd('\\') + "\\";
        return ProtectedPaths.Any(p =>
            normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 受保护根目录列表
    /// </summary>
    private static readonly string[] ProtectedPaths = new[]
    {
        @"C:\Windows\",
        @"C:\Program Files\",
        @"C:\Program Files (x86)\",
        @"C:\ProgramData\"
    };
}
