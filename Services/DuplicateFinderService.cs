using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace Toolbox.Services;

/// <summary>
/// 重复文件查找服务接口
/// 分阶段扫描：按大小分组 → 计算哈希 → 排除硬链接
/// </summary>
public interface IDuplicateFinderService
{
    /// <summary>
    /// 分阶段扫描重复文件
    /// 阶段1：按大小分组（排除零字节文件）
    /// 阶段2：对同大小文件计算 SHA256/MD5 哈希（4MB 分块）
    /// 阶段3：排除硬链接（FileIndex + VolumeSerialNumber 相同）
    /// 降级策略：FileIndex 获取失败时跳过硬链接检测
    /// </summary>
    /// <param name="rootPaths">扫描的根路径列表</param>
    /// <param name="filePatterns">文件类型过滤模式，如 "*.jpg;*.png"，空表示所有</param>
    /// <param name="useMD5">true 使用 MD5，false 使用 SHA256</param>
    /// <param name="progress">进度报告，报告当前阶段描述</param>
    /// <param name="token">取消令牌</param>
    /// <returns>重复文件组列表</returns>
    Task<List<DuplicateGroup>> FindDuplicatesAsync(
        IEnumerable<string> rootPaths,
        string filePatterns,
        bool useMD5,
        IProgress<string> progress,
        CancellationToken token);
}

/// <summary>
/// 重复文件组 — 一组哈希相同的文件
/// </summary>
public class DuplicateGroup
{
    /// <summary>组中所有文件列表</summary>
    public List<DuplicateFileInfo> Files { get; set; } = new();

    /// <summary>文件哈希值</summary>
    public string Hash { get; set; } = "";

    /// <summary>重复文件数量</summary>
    public int DuplicateCount => Files.Count;

    /// <summary>单个文件大小（组内所有文件大小相同）</summary>
    public long FileSize => Files.FirstOrDefault()?.Size ?? 0;

    /// <summary>格式化单个文件大小</summary>
    public string FileSizeDisplay => FormatSize(FileSize);

    /// <summary>浪费的总空间（除保留一个外的所有文件大小之和）</summary>
    public long WastedSpace => FileSize * (Files.Count - 1);

    /// <summary>格式化浪费空间</summary>
    public string WastedSpaceDisplay => FormatSize(WastedSpace);

    /// <summary>组中是否包含受保护路径文件</summary>
    public bool HasProtectedFiles => Files.Any(f => f.IsProtected);

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
/// 重复文件信息 — 单个文件的基本信息
/// </summary>
public class DuplicateFileInfo
{
    /// <summary>文件名</summary>
    public string Name { get; set; } = "";

    /// <summary>文件完整路径</summary>
    public string FullPath { get; set; } = "";

    /// <summary>文件大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>格式化大小</summary>
    public string SizeDisplay => DuplicateGroup.FormatSize(Size);

    /// <summary>最后修改时间</summary>
    public DateTime LastWriteTime { get; set; }

    /// <summary>格式化日期</summary>
    public string DateDisplay => LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>创建时间</summary>
    public DateTime CreationTime { get; set; }

    /// <summary>是否在受保护路径下</summary>
    public bool IsProtected { get; set; }

    /// <summary>是否被用户标记为保留（不删除）</summary>
    public bool IsMarkedToKeep { get; set; }

    /// <summary>卷序列号（用于硬链接检测）</summary>
    public uint VolumeSerialNumber { get; set; }

    /// <summary>文件索引号高位（用于硬链接检测）</summary>
    public uint FileIndexHigh { get; set; }

    /// <summary>文件索引号低位（用于硬链接检测）</summary>
    public uint FileIndexLow { get; set; }

    /// <summary>是否成功获取了文件索引信息</summary>
    public bool HasFileIndex { get; set; }
}

/// <summary>
/// 重复文件查找服务实现
/// 三阶段扫描流式处理：
/// 阶段1：EnumerateFiles 流式收集所有文件，按大小分组（排除零字节文件）
/// 阶段2：对每组同大小文件计算哈希（SHA256 或 MD5，4MB 分块读取）
/// 阶段3：在每组哈希相同的文件中排除硬链接
/// 降级策略：FileIndex 获取失败时跳过硬链接检测
/// </summary>
public class DuplicateFinderService : IDuplicateFinderService
{
    private readonly ILogService _log;
    private const int HashChunkSize = 4 * 1024 * 1024; // 4MB 分块读取

    /// <summary>构造函数，注入日志服务</summary>
    public DuplicateFinderService(ILogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 分阶段查找重复文件
    /// </summary>
    public async Task<List<DuplicateGroup>> FindDuplicatesAsync(
        IEnumerable<string> rootPaths,
        string filePatterns,
        bool useMD5,
        IProgress<string> progress,
        CancellationToken token)
    {
        var groups = await Task.Run(() =>
        {
            // ===== 阶段1：收集所有文件，按大小分组 =====
            progress.Report("阶段 1/3：正在收集文件列表...");
            var sizeGroups = CollectFilesBySize(rootPaths, filePatterns, token, progress);

            if (!sizeGroups.Any())
            {
                progress.Report("未找到可比较的文件");
                return new List<DuplicateGroup>();
            }

            // ===== 阶段2：计算哈希，在每组同大小文件中找出哈希相同的 =====
            progress.Report("阶段 2/3：正在计算文件哈希...");
            var hashGroups = ComputeHashesAndGroup(sizeGroups, useMD5, token, progress);

            // ===== 阶段3：排除硬链接（同一 FileIndex + VolumeSerialNumber 的视为同一文件） =====
            progress.Report("阶段 3/3：正在检测硬链接...");
            var result = ExcludeHardLinks(hashGroups);

            return result;
        }, token);

        _log.LogInfo($"重复文件查找完成: {groups.Count} 个重复组");
        return groups;
    }

    /// <summary>
    /// 阶段1：收集所有文件，按大小分组
    /// 排除零字节文件
    /// 使用 EnumerateFiles 流式枚举
    /// </summary>
    private Dictionary<long, List<DuplicateFileInfo>> CollectFilesBySize(
        IEnumerable<string> rootPaths,
        string filePatterns,
        CancellationToken token,
        IProgress<string> progress)
    {
        var sizeGroups = new Dictionary<long, List<DuplicateFileInfo>>();
        var patterns = string.IsNullOrWhiteSpace(filePatterns)
            ? new[] { "*" }
            : filePatterns.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

        if (patterns.Length == 0) patterns = new[] { "*" };

        foreach (var rootPath in rootPaths)
        {
            token.ThrowIfCancellationRequested();

            if (!Directory.Exists(rootPath))
                continue;

            foreach (var pattern in patterns)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"收集文件: {rootPath} ({pattern})...");

                try
                {
                    var files = Directory.EnumerateFiles(
                        rootPath, pattern,
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

                            // 跳过符号链接
                            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                continue;

                            // 排除零字节文件
                            if (fileInfo.Length == 0)
                                continue;

                            var dupInfo = new DuplicateFileInfo
                            {
                                Name = fileInfo.Name,
                                FullPath = file,
                                Size = fileInfo.Length,
                                LastWriteTime = fileInfo.LastWriteTime,
                                CreationTime = fileInfo.CreationTime,
                                IsProtected = Helpers.ProtectedPaths.IsProtectedPath(file),
                                HasFileIndex = false
                            };

                            if (!sizeGroups.ContainsKey(fileInfo.Length))
                                sizeGroups[fileInfo.Length] = new List<DuplicateFileInfo>();

                            sizeGroups[fileInfo.Length].Add(dupInfo);
                        }
                        catch
                        {
                            // 跳过无法访问的文件
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"收集文件 {rootPath} ({pattern}) 时出错: {ex.Message}");
                }
            }
        }

        // 只保留有多个文件的组（至少2个文件才可能重复）
        var result = new Dictionary<long, List<DuplicateFileInfo>>();
        foreach (var kvp in sizeGroups)
        {
            if (kvp.Value.Count >= 2)
                result[kvp.Key] = kvp.Value;
        }

        _log.LogInfo($"阶段1完成: {result.Count} 个大小组，共 {result.Values.Sum(v => v.Count)} 个文件");
        return result;
    }

    /// <summary>
    /// 阶段2：对每组同大小文件计算哈希
    /// 使用 SHA256 或 MD5，4MB 分块读取
    /// </summary>
    private List<DuplicateGroup> ComputeHashesAndGroup(
        Dictionary<long, List<DuplicateFileInfo>> sizeGroups,
        bool useMD5,
        CancellationToken token,
        IProgress<string> progress)
    {
        var hashGroups = new ConcurrentDictionary<string, ConcurrentBag<DuplicateFileInfo>>();
        int totalFiles = sizeGroups.Values.Sum(g => g.Count);
        int processed = 0;

        // 使用并行处理加速哈希计算
        var allFiles = sizeGroups.Values.SelectMany(g => g).ToList();
        Parallel.ForEach(allFiles, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = token
        }, file =>
        {
            try
            {
                var hash = ComputeFileHash(file.FullPath, useMD5);
                file.HasFileIndex = TryGetFileIndex(file);

                var key = $"{file.Size}_{hash}";
                hashGroups.AddOrUpdate(key,
                    _ => new ConcurrentBag<DuplicateFileInfo> { file },
                    (_, bag) => { bag.Add(file); return bag; });

                var done = Interlocked.Increment(ref processed);
                if (done % 100 == 0)
                    progress.Report($"计算哈希: {done}/{totalFiles}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"计算文件哈希失败: {file.FullPath}, {ex.Message}");
            }
        });

        // 只保留有多个文件的哈希组（至少2个文件才算重复）
        var result = new List<DuplicateGroup>();
        foreach (var kvp in hashGroups)
        {
            var files = kvp.Value.ToList();
            if (files.Count >= 2)
            {
                result.Add(new DuplicateGroup
                {
                    Hash = kvp.Key.Split('_').LastOrDefault() ?? "",
                    Files = files.OrderBy(f => f.LastWriteTime).ToList()
                });
            }
        }

        _log.LogInfo($"阶段2完成: {result.Count} 个重复组");
        return result;
    }

    /// <summary>
    /// 计算单个文件的哈希值（SHA256 或 MD5）
    /// 使用 4MB 分块流式读取，避免大文件内存溢出
    /// </summary>
    private string ComputeFileHash(string filePath, bool useMD5)
    {
        using var stream = File.OpenRead(filePath);
        if (useMD5)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        else
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// 尝试获取文件的 FileIndex（用于硬链接检测）
    /// 降级策略：获取失败返回 false，跳过硬链接检测
    /// </summary>
    private bool TryGetFileIndex(DuplicateFileInfo file)
    {
        try
        {
            using var fs = new FileStream(file.FullPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None);

            if (Native.NativeMethods.GetFileInformationByHandle(
                fs.SafeFileHandle.DangerousGetHandle(),
                out var fileInfo))
            {
                file.VolumeSerialNumber = fileInfo.dwVolumeSerialNumber;
                file.FileIndexHigh = fileInfo.nFileIndexHigh;
                file.FileIndexLow = fileInfo.nFileIndexLow;
                return true;
            }
        }
        catch
        {
            // FileIndex 获取失败，降级处理
        }
        return false;
    }

    /// <summary>
    /// 阶段3：排除硬链接
    /// 同一 (VolumeSerialNumber, FileIndexHigh, FileIndexLow) 的文件视为同一物理文件
    /// 降级：HasFileIndex == false 的文件保留在组中
    /// </summary>
    private List<DuplicateGroup> ExcludeHardLinks(List<DuplicateGroup> hashGroups)
    {
        var result = new List<DuplicateGroup>();

        foreach (var group in hashGroups)
        {
            // 按 (VolumeSN, FileIndex) 分组，每组只保留一个代表
            var seenIndices = new HashSet<(uint, uint, uint)>();
            var filteredFiles = new List<DuplicateFileInfo>();

            foreach (var file in group.Files)
            {
                if (!file.HasFileIndex)
                {
                    // FileIndex 获取失败，降级保留
                    filteredFiles.Add(file);
                    continue;
                }

                var key = (file.VolumeSerialNumber, file.FileIndexHigh, file.FileIndexLow);

                // 零索引表示获取失败或系统文件
                if (key.Item2 == 0 && key.Item3 == 0)
                {
                    filteredFiles.Add(file);
                    continue;
                }

                if (!seenIndices.Contains(key))
                {
                    seenIndices.Add(key);
                    filteredFiles.Add(file);
                }
                // else: 硬链接重复，跳过
            }

            // 过滤后至少 2 个文件才保留
            if (filteredFiles.Count >= 2)
            {
                group.Files = filteredFiles;
                result.Add(group);
            }
        }

        var excludedCount = hashGroups.Sum(g => g.Files.Count) - result.Sum(g => g.Files.Count);
        _log.LogInfo($"阶段3完成: 排除 {excludedCount} 个硬链接重复, 剩余 {result.Count} 个重复组");

        return result;
    }
}
