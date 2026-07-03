using System.Collections.Concurrent;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Services;

/// <summary>
/// C盘清理服务接口
/// 定义扫描清理项大小、批量清理的契约
/// </summary>
public interface ICleanupService
{
    /// <summary>清理项列表</summary>
    List<CleanupItem> Items { get; }

    /// <summary>
    /// 扫描所有清理项的大小
    /// 使用流式枚举、48小时 LastWriteTime 过滤、跳过符号链接
    /// 每批 500 个文件更新一次进度
    /// </summary>
    /// <param name="progress">进度报告回调，报告当前扫描的项目名称</param>
    /// <param name="token">取消令牌</param>
    Task ScanAllAsync(IProgress<string> progress, CancellationToken token, bool onlyCleanOldFiles = true, int minimumAgeHours = 48);

    /// <summary>
    /// 清理选中的项目列表
    /// 每批 500 个文件执行删除，支持跳过受保护路径
    /// </summary>
    /// <param name="items">选中的清理项</param>
    /// <param name="progress">进度报告回调，报告(项目名, 进度百分比)</param>
    /// <param name="token">取消令牌</param>
    /// <returns>清理结果统计</returns>
    Task<CleanupResult> CleanAsync(IEnumerable<CleanupItem> items, IProgress<(string, int)> progress, CancellationToken token, bool onlyCleanOldFiles = true, int minimumAgeHours = 48);

    /// <summary>获取预定义的清理位置列表（系统预定义路径）</summary>
    List<(string Name, string Path, CleanupSafetyLevel Level)> GetPredefinedLocations();
}

/// <summary>
/// 清理安全等级
/// </summary>
public enum CleanupSafetyLevel
{
    /// <summary>安全级别：完全不影响系统运行</summary>
    Safe,
    /// <summary>推荐级别：一般不影响系统，可能需要对应软件重新生成</summary>
    Recommended,
    /// <summary>高级级别：可能影响系统或软件，需谨慎操作</summary>
    Advanced
}

/// <summary>
/// 单个清理项模型
/// </summary>
public partial class CleanupItem : ObservableObject
{
    /// <summary>清理项名称，如"Windows 临时文件"</summary>
    public string Name { get; set; } = "";

    /// <summary>清理项描述</summary>
    public string Description { get; set; } = "";

    /// <summary>文件夹路径（支持多个路径，用分号分隔）</summary>
    public string Path { get; set; } = "";

    /// <summary>文件匹配模式，如 "*.log"；为空表示所有文件</summary>
    public string Pattern { get; set; } = "";

    /// <summary>安全等级</summary>
    public CleanupSafetyLevel SafetyLevel { get; set; } = CleanupSafetyLevel.Safe;

    /// <summary>扫描到的总大小（字节）</summary>
    [ObservableProperty]
    private long _totalSize;

    /// <summary>扫描到的文件数量</summary>
    [ObservableProperty]
    private int _fileCount;

    /// <summary>格式化后的大小字符串，如 "256 MB"</summary>
    public string SizeDisplay => FormatSize(TotalSize);

    /// <summary>格式化后的文件数量字符串</summary>
    public string FileCountDisplay => FileCount <= 0 ? "无垃圾文件" : $"{FileCount:N0} 个文件";

    /// <summary>用户是否选中此项进行清理</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是否默认勾选（扫描完成后自动选中）</summary>
    public bool IsDefaultSelected { get; set; }

    partial void OnTotalSizeChanged(long value)
    {
        OnPropertyChanged(nameof(SizeDisplay));
    }

    partial void OnFileCountChanged(int value)
    {
        OnPropertyChanged(nameof(FileCountDisplay));
    }

    /// <summary>
    /// 格式化字节数为可读字符串
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
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
/// 清理结果统计
/// 使用内部字段支持 Interlocked 原子操作
/// </summary>
public class CleanupResult
{
    /// <summary>成功删除的文件数（内部字段，供 Interlocked 使用）</summary>
    internal int _filesDeleted;
    /// <summary>成功删除的文件数（属性访问）</summary>
    public int FilesDeleted => Volatile.Read(ref _filesDeleted);

    /// <summary>跳过的文件数（内部字段）</summary>
    internal int _filesSkipped;
    /// <summary>跳过的文件数（属性访问）</summary>
    public int FilesSkipped => Volatile.Read(ref _filesSkipped);

    /// <summary>删除失败的文件数（内部字段）</summary>
    internal int _filesFailed;
    /// <summary>删除失败的文件数（属性访问）</summary>
    public int FilesFailed => Volatile.Read(ref _filesFailed);

    /// <summary>释放的总空间（内部字段）</summary>
    internal long _totalFreedBytes;
    /// <summary>释放的总空间（字节）（属性访问）</summary>
    public long TotalFreedBytes => Interlocked.Read(ref _totalFreedBytes);

    /// <summary>格式化后释放的空间</summary>
    public string TotalFreedDisplay => CleanupItem.FormatSize(TotalFreedBytes);

    /// <summary>增量增加成功删除数</summary>
    internal void IncFilesDeleted() => Interlocked.Increment(ref _filesDeleted);
    /// <summary>增量增加跳过数</summary>
    internal void IncFilesSkipped() => Interlocked.Increment(ref _filesSkipped);
    /// <summary>增量增加失败数</summary>
    internal void IncFilesFailed() => Interlocked.Increment(ref _filesFailed);
    /// <summary>增量增加释放空间</summary>
    internal void AddFreedBytes(long bytes) => Interlocked.Add(ref _totalFreedBytes, bytes);
}

/// <summary>
/// C盘清理服务实现
/// 负责扫描存储清理项的大小并执行批量清理
/// 特性：
///   - 48小时 LastWriteTime 过滤（仅清除 48 小时前的文件）
///   - 每批 500 个文件分批处理
///   - 跳过符号链接
///   - 受保护路径不删除
///   - 支持 CancellationToken 取消
/// </summary>
public class CleanupService : ICleanupService
{
    private readonly ILogService _log;

    /// <summary>清理项列表</summary>
    public List<CleanupItem> Items { get; private set; } = new();

    /// <summary>构造函数，注入日志服务</summary>
    public CleanupService(ILogService log)
    {
        _log = log;
        LoadPredefinedItems();
    }

    /// <summary>
    /// 加载预定义的清理位置
    /// 分为安全、推荐、高级三个等级
    /// </summary>
    private void LoadPredefinedItems()
    {
        var systemTemp = System.IO.Path.GetTempPath().TrimEnd('\\');
        var userTemp = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");

        Items = new List<CleanupItem>
        {
            // ===== 安全级别 =====
            new CleanupItem
            {
                Name = "Windows 临时文件",
                Description = "Windows 系统和应用程序产生的临时文件，安全删除",
                Path = systemTemp,
                SafetyLevel = CleanupSafetyLevel.Safe,
                IsDefaultSelected = true
            },
            new CleanupItem
            {
                Name = "用户临时文件",
                Description = "当前用户应用程序产生的临时文件",
                Path = userTemp,
                SafetyLevel = CleanupSafetyLevel.Safe,
                IsDefaultSelected = true
            },
            new CleanupItem
            {
                Name = "回收站",
                Description = "清空回收站中的所有文件",
                Path = @"C:\$Recycle.Bin",
                SafetyLevel = CleanupSafetyLevel.Safe,
                IsDefaultSelected = false
            },

            // ===== 推荐级别 =====
            new CleanupItem
            {
                Name = "浏览器缓存 (Edge)",
                Description = "Microsoft Edge 浏览器缓存文件",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Default\Cache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Default\Code Cache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Default\GPUCache"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "浏览器缓存 (Chrome)",
                Description = "Google Chrome 浏览器缓存文件",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Default\Cache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Default\Code Cache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Default\GPUCache"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "浏览器缓存 (Firefox)",
                Description = "Mozilla Firefox 浏览器缓存文件",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Mozilla\Firefox\Profiles\*\cache2") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Mozilla\Firefox\Profiles\*\cache2"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "Windows 更新缓存",
                Description = "Windows Update 下载的更新缓存文件",
                Path = @"C:\Windows\SoftwareDistribution\Download",
                SafetyLevel = CleanupSafetyLevel.Recommended,
                IsDefaultSelected = false
            },
            new CleanupItem
            {
                Name = "缩略图缓存",
                Description = "资源管理器生成的图片缩略图缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Windows\Explorer"),
                Pattern = "thumbcache_*.db",
                SafetyLevel = CleanupSafetyLevel.Recommended,
                IsDefaultSelected = true
            },
            new CleanupItem
            {
                Name = "Windows 日志文件",
                Description = "系统和应用程序产生的 .log 日志文件",
                Path = @"C:\Windows\Logs" + ";" + @"C:\Windows\System32\LogFiles",
                SafetyLevel = CleanupSafetyLevel.Recommended,
                IsDefaultSelected = false
            },
            new CleanupItem
            {
                Name = "崩溃转储文件",
                Description = "系统和程序崩溃生成的 dump 文件",
                Path = @"C:\Windows\Minidump" + ";" + @"C:\Windows\MEMORY.DMP" + ";" +
                       System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CrashDumps"),
                Pattern = "*.dmp",
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "传递优化缓存",
                Description = "Windows 传递优化缓存文件",
                Path = @"C:\ProgramData\Microsoft\Windows\DeliveryOptimization\Cache",
                SafetyLevel = CleanupSafetyLevel.Recommended
            },

            // ===== 高级级别 =====
            new CleanupItem
            {
                Name = "DNS 缓存",
                Description = "刷新 DNS 解析缓存（可能短暂影响首次访问网站速度）",
                Path = "",  // 不需要路径，通过 ipconfig /flushdns 执行
                SafetyLevel = CleanupSafetyLevel.Advanced
            },
            new CleanupItem
            {
                Name = "Windows 错误报告",
                Description = "Windows 错误报告产生的 dump 和日志文件",
                Path = @"C:\ProgramData\Microsoft\Windows\WER",
                SafetyLevel = CleanupSafetyLevel.Advanced
            },
            new CleanupItem
            {
                Name = "DirectX 着色器缓存",
                Description = "显卡驱动生成的着色器缓存，占用空间可能很大",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NVIDIA\DXCache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"AMD\DxCache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"AMD\DxcCache"),
                SafetyLevel = CleanupSafetyLevel.Advanced
            },

            // ===== 推荐补充 =====
            new CleanupItem
            {
                Name = "Prefetch 预读取文件",
                Description = "Windows 预读取缓存，清理后首次启动程序会稍慢",
                Path = @"C:\Windows\Prefetch",
                SafetyLevel = CleanupSafetyLevel.Recommended,
                IsDefaultSelected = false
            },
            new CleanupItem
            {
                Name = "Windows Store 缓存",
                Description = "Microsoft Store 应用缓存（通过 wsreset 清理）",
                Path = "",
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "图标缓存",
                Description = "资源管理器图标缓存数据库",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "IconCache.db"),
                SafetyLevel = CleanupSafetyLevel.Recommended,
                IsDefaultSelected = false
            },
            new CleanupItem
            {
                Name = "最近文档历史",
                Description = "Windows 记录的最近打开文档历史",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Recent"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "VS Code 缓存",
                Description = "Visual Studio Code 编辑器缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Code\Cache") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Code\CachedData") + ";" +
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Code\User\workspaceStorage"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "npm 缓存",
                Description = "Node.js npm 包管理器缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"npm-cache") + ";" +
                    System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"npm-cache"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "pip 缓存",
                Description = "Python pip 包管理器缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"pip\Cache"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "NuGet 缓存",
                Description = ".NET NuGet 包缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NuGet\v3-cache") + ";" +
                    System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NuGet\plugins-cache"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "字体缓存",
                Description = "Windows 字体缓存服务生成的缓存文件",
                Path = @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache",
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "WeChat 缓存",
                Description = "微信本地缓存文件",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    @"WeChat Files") + ";" +
                    System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Tencent\WeChat"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "Discord 缓存",
                Description = "Discord 客户端缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"discord\Cache") + ";" +
                    System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"discord\Code Cache"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },
            new CleanupItem
            {
                Name = "Spotify 缓存",
                Description = "Spotify 客户端缓存",
                Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Spotify\Storage") + ";" +
                    System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Spotify\Data"),
                SafetyLevel = CleanupSafetyLevel.Recommended
            },

            // ===== 高级补充 =====
            new CleanupItem
            {
                Name = "Windows 旧系统备份",
                Description = "Windows 升级后保留的旧系统文件 (Windows.old)，确认不需要回退后可清理",
                Path = @"C:\Windows.old",
                SafetyLevel = CleanupSafetyLevel.Advanced
            },
            new CleanupItem
            {
                Name = "系统驱动旧版本",
                Description = "DriverStore 中保留的旧版本驱动包，清理后会失去驱动回滚能力",
                Path = @"C:\Windows\System32\DriverStore\FileRepository",
                SafetyLevel = CleanupSafetyLevel.Advanced
            }
        };

        _log.LogInfo($"加载了 {Items.Count} 个预定义清理项");
    }

    /// <summary>
    /// 获取预定义清理位置列表
    /// </summary>
    public List<(string Name, string Path, CleanupSafetyLevel Level)> GetPredefinedLocations()
    {
        return Items.Select(i => (i.Name, i.Path, i.SafetyLevel)).ToList();
    }

    /// <summary>
    /// 扫描所有清理项的文件大小
    /// 使用 Task.Run 在后台线程执行，通过 IProgress 报告进度
    /// </summary>
    public async Task ScanAllAsync(IProgress<string> progress, CancellationToken token, bool onlyCleanOldFiles = true, int minimumAgeHours = 48)
    {
        // 重置所有项目的统计数据
        Items.ForEach(i => { i.TotalSize = 0; i.FileCount = 0; });

        await Task.Run(() =>
        {
            foreach (var item in Items)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"正在扫描: {item.Name}...");

                if (string.IsNullOrEmpty(item.Path))
                {
                    // 无路径的项（如 DNS 缓存）标记为特殊处理
                    item.TotalSize = 1; // 非零表示可清理
                    item.FileCount = 0;
                    continue;
                }

                ScanItemSize(item, token, onlyCleanOldFiles, minimumAgeHours);

                token.ThrowIfCancellationRequested();
            }
        }, token);

        _log.LogInfo($"扫描完成：共 {Items.Count} 个清理项，总大小 {CleanupItem.FormatSize(Items.Sum(i => i.TotalSize))}");
    }

    /// <summary>
    /// 扫描单个清理项的大小
    /// 遍历指定路径下所有文件，计算总大小和文件数
    /// 使用 EnumerateFiles 流式枚举（不一次性加载所有条目）
    /// 跳过符号链接，不再做 48 小时过滤（用户主动清理时应统计全部）
    /// </summary>
    private void ScanItemSize(CleanupItem item, CancellationToken token, bool onlyCleanOldFiles, int minimumAgeHours)
    {
        var paths = item.Path.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var cutoffUtc = GetAgeCutoffUtc(onlyCleanOldFiles, minimumAgeHours);
        long totalSize = 0;
        int fileCount = 0;

        foreach (var path in paths)
        {
            var trimmedPath = path.Trim();

            // 处理通配符路径（如 Firefox Profiles\*\cache2）
            if (trimmedPath.Contains("*"))
            {
                var expandedPaths = ExpandWildcardPath(trimmedPath);
                foreach (var expandedPath in expandedPaths)
                {
                    if (Directory.Exists(expandedPath))
                        ScanDirectory(expandedPath, item, ref totalSize, ref fileCount, token, cutoffUtc);
                }
                continue;
            }

            // 单文件路径直接统计
            if (File.Exists(trimmedPath))
            {
                try
                {
                    var fileInfo = new FileInfo(trimmedPath);
                    if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && PassesAgeFilter(fileInfo, cutoffUtc))
                    {
                        totalSize += fileInfo.Length;
                        fileCount++;
                    }
                }
                catch { }
                continue;
            }

            if (!Directory.Exists(trimmedPath))
                continue;

            try
            {
                ScanDirectory(trimmedPath, item, ref totalSize, ref fileCount, token, cutoffUtc);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.LogWarning($"扫描 {item.Name} 时出错: {ex.Message}");
            }
        }

        item.TotalSize = totalSize;
        item.FileCount = fileCount;
    }

    /// <summary>
    /// 扫描单个目录下的文件，计算总大小和文件数
    /// 使用 EnumerateFiles 流式枚举（不一次性加载所有条目）
    /// 跳过符号链接
    /// </summary>
    private void ScanDirectory(string directoryPath, CleanupItem item,
        ref long totalSize, ref int fileCount, CancellationToken token, DateTime? cutoffUtc)
    {
        var searchPattern = string.IsNullOrEmpty(item.Pattern) ? "*" : item.Pattern;
        var files = Directory.EnumerateFiles(
            directoryPath, searchPattern,
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = new FileInfo(file);

                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !PassesAgeFilter(fileInfo, cutoffUtc))
                    continue;

                totalSize += fileInfo.Length;
                fileCount++;
            }
            catch
            {
                // 跳过无法访问的文件
            }
        }
    }

    private static DateTime? GetAgeCutoffUtc(bool onlyCleanOldFiles, int minimumAgeHours)
    {
        if (!onlyCleanOldFiles) return null;
        return DateTime.UtcNow.AddHours(-Math.Max(1, minimumAgeHours));
    }

    private static bool PassesAgeFilter(FileSystemInfo fileInfo, DateTime? cutoffUtc)
    {
        return cutoffUtc == null || fileInfo.LastWriteTimeUtc <= cutoffUtc.Value;
    }

    /// <summary>
    /// 展开包含通配符 * 的路径。
    /// 例如 "C:\Firefox\Profiles\*\cache2" → 枚举 Profiles 下所有子目录的 cache2 路径。
    /// </summary>
    private List<string> ExpandWildcardPath(string wildcardPath)
    {
        var results = new List<string>();
        var starIndex = wildcardPath.IndexOf('*');
        if (starIndex < 0) { results.Add(wildcardPath); return results; }

        var basePath = wildcardPath[..starIndex].TrimEnd('\\', '/');
        var remainingPath = wildcardPath[(starIndex + 1)..].TrimStart('\\', '/');

        if (!Directory.Exists(basePath)) return results;

        try
        {
            foreach (var subDir in Directory.GetDirectories(basePath))
            {
                var expanded = Path.Combine(subDir, remainingPath);
                if (expanded.Contains("*"))
                    results.AddRange(ExpandWildcardPath(expanded)); // 递归展开多个 *
                else
                    results.Add(expanded);
            }
        }
        catch { /* 权限不足时跳过 */ }

        return results;
    }

    /// <summary>
    /// 批量清理选中的项目
    /// 每批 500 个文件执行删除操作
    /// 跳过受保护路径下的文件
    /// </summary>
    public async Task<CleanupResult> CleanAsync(
        IEnumerable<CleanupItem> items,
        IProgress<(string, int)> progress,
        CancellationToken token,
        bool onlyCleanOldFiles = true,
        int minimumAgeHours = 48)
    {
        var result = new CleanupResult();
        const int batchSize = 500; // 每批 500 个文件
        var cutoffUtc = GetAgeCutoffUtc(onlyCleanOldFiles, minimumAgeHours);

        await Task.Run(async () =>
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();

                // 处理 DNS 缓存这种特殊清理项
                if (string.IsNullOrEmpty(item.Path))
                {
                    await CleanSpecialItemAsync(item, result);
                    continue;
                }

                var paths = item.Path.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var filesToDelete = new ConcurrentBag<string>();

                // 先收集所有待删除文件
                foreach (var path in paths)
                {
                    var trimmedPath = path.Trim();

                    // 单文件路径
                    if (File.Exists(trimmedPath))
                    {
                        try
                        {
                            var fi = new FileInfo(trimmedPath);
                            if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                && PassesAgeFilter(fi, cutoffUtc))
                                filesToDelete.Add(trimmedPath);
                        }
                        catch { }
                        continue;
                    }

                    if (!Directory.Exists(trimmedPath))
                        continue;

                    try
                    {
                        var searchPattern = string.IsNullOrEmpty(item.Pattern) ? "*" : item.Pattern;
                        var files = Directory.EnumerateFiles(
                            trimmedPath, searchPattern,
                            new EnumerationOptions
                            {
                                RecurseSubdirectories = true,
                                IgnoreInaccessible = true,
                                AttributesToSkip = FileAttributes.ReparsePoint
                            });

                        foreach (var file in files)
                        {
                            token.ThrowIfCancellationRequested();

                            try
                            {
                                var fileInfo = new FileInfo(file);

                                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                    || !PassesAgeFilter(fileInfo, cutoffUtc))
                                    continue;

                                filesToDelete.Add(file);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"收集待删除文件 {trimmedPath} 时出错: {ex.Message}");
                    }
                }

                // 分批删除（每批 500 个文件）
                var fileList = filesToDelete.ToList();
                int processed = 0;

                while (processed < fileList.Count)
                {
                    token.ThrowIfCancellationRequested();

                    var batch = fileList.Skip(processed).Take(batchSize).ToList();
                    var deleteTasks = batch.Select(file => Task.Run(() =>
                    {
                        try
                        {
                            // 跳过受保护路径下的文件
                            if (Helpers.ProtectedPaths.IsProtectedPath(file))
                            {
                                result.IncFilesSkipped();
                                return;
                            }

                            // 跳过受保护扩展名的文件
                            if (Helpers.ProtectedPaths.IsProtectedExtension(file))
                            {
                                result.IncFilesSkipped();
                                return;
                            }

                            var fileInfo = new FileInfo(file);
                            if (!fileInfo.Exists
                                || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                || !PassesAgeFilter(fileInfo, cutoffUtc))
                            {
                                result.IncFilesSkipped();
                                return;
                            }

                            // 移除只读属性后删除
                            if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                                fileInfo.Attributes &= ~FileAttributes.ReadOnly;

                            File.Delete(file);
                            result.IncFilesDeleted();
                            result.AddFreedBytes(fileInfo.Length);
                        }
                        catch
                        {
                            result.IncFilesFailed();
                        }
                    }, token));

                    await Task.WhenAll(deleteTasks);
                    processed += batchSize;

                    progress.Report((item.Name, processed * 100 / fileList.Count));
                }

                _log.LogOperation("cleanup", "clean",
                    $"清理 {item.Name}: 成功 {result.FilesDeleted}, 跳过 {result.FilesSkipped}, 失败 {result.FilesFailed}");
            }
        }, token);

        return result;
    }

    /// <summary>
    /// 处理特殊清理项（如 DNS 缓存刷新、Windows Store 缓存重置）
    /// 这些项不需要文件操作，而是执行系统命令
    /// </summary>
    private async Task CleanSpecialItemAsync(CleanupItem item, CleanupResult result)
    {
        try
        {
            if (item.Name.Contains("DNS"))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    result.IncFilesDeleted();
                    _log.LogInfo("DNS 缓存已刷新");
                }
            }
            else if (item.Name.Contains("Windows Store"))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("wsreset.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    result.IncFilesDeleted();
                    _log.LogInfo("Windows Store 缓存已重置");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"特殊清理项 {item.Name} 执行失败", ex);
            result.IncFilesFailed();
        }
    }
}
