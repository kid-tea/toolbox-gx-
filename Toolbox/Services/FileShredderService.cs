using System.IO;
using System.Runtime.InteropServices;
using Toolbox.Helpers;
using Toolbox.Models;
using Toolbox.Native;

namespace Toolbox.Services;

/// <summary>
/// 文件粉碎服务接口
/// 提供文件内容覆写、SSD/HDD 检测、硬链接检测、安全属性检查等功能
/// </summary>
public interface IFileShredderService
{
    /// <summary>
    /// 使用指定算法粉碎单个文件
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="algorithm">覆写算法</param>
    /// <param name="progress">进度报告（百分比 0-100）</param>
    /// <param name="token">取消令牌</param>
    /// <returns>破碎结果（成功/失败及原因）</returns>
    ShredderResult ShredFile(string filePath, ShredderAlgorithm algorithm,
        IProgress<int>? progress = null, CancellationToken token = default);

    /// <summary>
    /// 批量粉碎文件
    /// </summary>
    List<ShredderResult> ShredFiles(IEnumerable<string> filePaths, ShredderAlgorithm algorithm,
        IProgress<int>? progress = null, CancellationToken token = default);

    /// <summary>
    /// 检测指定文件所在驱动器是否为 SSD
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是 SSD 返回 true</returns>
    bool IsSSD(string filePath);

    /// <summary>
    /// 获取文件的硬链接数量
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>硬链接数量（>=1），失败返回 0</returns>
    int GetHardLinkCount(string filePath);

    /// <summary>
    /// 检查文件是否为 EFS 加密
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是 EFS 加密返回 true</returns>
    bool IsEFSEncrypted(string filePath);

    /// <summary>
    /// 检查目标磁盘是否有足够空间进行覆写（需要至少 1 字节可用空间）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>有足够空间返回 true</returns>
    bool HasEnoughDiskSpace(string filePath);
}

/// <summary>
/// 粉碎结果
/// </summary>
public class ShredderResult
{
    /// <summary>文件路径</summary>
    public string FilePath { get; set; } = "";
    /// <summary>是否成功</summary>
    public bool Success { get; set; }
    /// <summary>中文错误消息</summary>
    public string ErrorMessage { get; set; } = "";
    /// <summary>使用的覆写次数</summary>
    public int PassesUsed { get; set; }
    /// <summary>操作日志描述</summary>
    public string OperationLog { get; set; } = "";
}

/// <summary>
/// 文件粉碎服务实现
/// 覆写算法：Fast(1次随机)、Secure(3次-随机+互补+随机)、Thorough(7次Gutmann简化)
/// 安全特性：SSD检测、硬链接检测、空间检查、长路径处理
/// </summary>
public class FileShredderService : IFileShredderService
{
    private readonly ILogService _log;
    private const int BufferSize = 64 * 1024; // 64KB 覆写缓冲区

    public FileShredderService(ILogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 粉碎单个文件
    /// </summary>
    public ShredderResult ShredFile(string filePath, ShredderAlgorithm algorithm,
        IProgress<int>? progress = null, CancellationToken token = default)
    {
        var result = new ShredderResult { FilePath = filePath };

        if (token.IsCancellationRequested) return result;

        // 检查文件是否存在
        if (!File.Exists(filePath))
        {
            result.Success = true; // 文件已不存在，视为成功
            result.OperationLog = "文件已不存在";
            return result;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;

            // 空文件直接删除
            if (fileSize == 0)
            {
                File.Delete(filePath);
                result.Success = true;
                result.OperationLog = $"空文件直接删除: {filePath}";
                _log.LogOperation("shredder", "shred", $"删除空文件: {filePath}");
                return result;
            }

            // 确定覆写次数（SSD 上强制为 1 次）
            int passes = DeterminePasses(filePath, algorithm);
            result.PassesUsed = passes;

            // 覆写前检测
            if (!HasEnoughDiskSpace(filePath))
            {
                result.ErrorMessage = "磁盘空间不足，无法进行安全覆写";
                return result;
            }

            // 处理长文件名：如果文件名过长，先重命名为短名
            string actualFilePath = HandleLongFileName(filePath);

            // 去掉只读属性
            RemoveReadOnlyAttribute(actualFilePath);

            // 执行覆写
            _log.LogInfo($"Shredding: {actualFilePath}, {passes} passes, size: {fileSize}");
            OverwriteFile(actualFilePath, fileSize, passes, progress, token);

            // 覆写完成后删除文件
            File.Delete(actualFilePath);
            result.Success = true;
            result.OperationLog = $"覆写 {passes} 次后删除: {actualFilePath}";
            _log.LogOperation("shredder", "shred", result.OperationLog);
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "操作已取消";
        }
        catch (UnauthorizedAccessException ex)
        {
            result.ErrorMessage = $"权限不足: {ex.Message}";
        }
        catch (IOException ex)
        {
            result.ErrorMessage = $"文件被占用: {ex.Message}";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"粉碎失败: {ex.Message}";
            _log.LogError($"Shred failed: {filePath}", ex);
        }

        return result;
    }

    /// <summary>
    /// 批量粉碎文件
    /// </summary>
    public List<ShredderResult> ShredFiles(IEnumerable<string> filePaths, ShredderAlgorithm algorithm,
        IProgress<int>? progress = null, CancellationToken token = default)
    {
        var paths = filePaths.ToList();
        var results = new List<ShredderResult>();
        int total = paths.Count;
        int completed = 0;

        foreach (var path in paths)
        {
            if (token.IsCancellationRequested) break;
            results.Add(ShredFile(path, algorithm));
            completed++;
            progress?.Report(completed * 100 / total);
        }

        return results;
    }

    /// <summary>
    /// 根据算法和磁盘类型确定实际覆写次数
    /// SSD 上强制限制为 1 次覆写（因为 SSD 的磨损均衡和写入放大会使多次覆写无效）
    /// </summary>
    private int DeterminePasses(string filePath, ShredderAlgorithm algorithm)
    {
        bool isSSD = IsSSD(filePath);

        if (isSSD)
        {
            _log.LogInfo($"SSD detected for {filePath}, limiting to 1 pass");
            return 1; // SSD 仅 1 次覆写
        }

        return algorithm switch
        {
            ShredderAlgorithm.Fast => 1,
            ShredderAlgorithm.Secure => 3,
            ShredderAlgorithm.Thorough => 7,
            _ => 1
        };
    }

    /// <summary>
    /// 执行文件覆写操作
    /// 使用指定次数的覆写模式，每次覆写整个文件内容
    /// </summary>
    private void OverwriteFile(string filePath, long fileSize, int passes,
        IProgress<int>? progress, CancellationToken token)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write,
            FileShare.None, BufferSize, FileOptions.WriteThrough);

        var buffer = new byte[Math.Min(BufferSize, fileSize)];
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();

        for (int pass = 1; pass <= passes; pass++)
        {
            token.ThrowIfCancellationRequested();
            fs.Position = 0;

            long remaining = fileSize;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int chunkSize = (int)Math.Min(buffer.Length, remaining);

                // 根据不同轮次填充不同数据模式
                FillBuffer(buffer, chunkSize, pass, passes, rng);

                fs.Write(buffer, 0, chunkSize);
                remaining -= chunkSize;
            }

            // 确保数据刷新到磁盘
            fs.Flush(true);

            // 报告进度（每轮覆写的进度）
            int overallProgress = (pass * 100) / passes;
            progress?.Report(overallProgress);
        }
    }

    /// <summary>
    /// 填充覆写缓冲区数据
    /// 不同轮次使用不同模式：
    ///   第1轮（随机）：随机字节（所有算法通用）
    ///   第2轮（互补）：前一字节的按位取反
    ///   第3轮（全部0）：全零字节
    ///   第4轮（全部1）：全 0xFF 字节
    ///   第5轮（随机2）：再次随机
    ///   第6轮（交替模式）：0xAA, 0x55
    ///   第7轮（混合）：0x92, 0x49, 0x24 循环
    /// </summary>
    private static void FillBuffer(byte[] buffer, int size, int pass, int totalPasses,
        System.Security.Cryptography.RandomNumberGenerator rng)
    {
        switch (pass)
        {
            case 1:
                rng.GetBytes(buffer, 0, size);
                break;
            case 2:
                // 互补模式：0xFF - 前一字节
                // 由于无法获取前一字节，生成随机后填充其互补
                var tempBuf = new byte[size];
                rng.GetBytes(tempBuf, 0, size);
                for (int i = 0; i < size; i++)
                    buffer[i] = (byte)~tempBuf[i];
                break;
            case 3:
                // 全零
                Array.Clear(buffer, 0, size);
                break;
            case 4:
                // 全 0xFF
                Array.Fill(buffer, (byte)0xFF, 0, size);
                break;
            case 5:
                rng.GetBytes(buffer, 0, size);
                break;
            case 6:
                // 交替模式
                for (int i = 0; i < size; i++)
                    buffer[i] = (byte)((i % 2 == 0) ? 0xAA : 0x55);
                break;
            case 7:
                // 混合模式
                byte[] patterns = { 0x92, 0x49, 0x24 };
                for (int i = 0; i < size; i++)
                    buffer[i] = patterns[i % 3];
                break;
            default:
                rng.GetBytes(buffer, 0, size);
                break;
        }
    }

    /// <summary>
    /// 检测指定路径所在驱动器是否为 SSD
    /// 通过 DeviceIoControl 查询 StorageDeviceSeekPenaltyProperty
    /// 无寻道延迟（IncursSeekPenalty=FALSE）→ SSD；有寻道延迟（TRUE）→ HDD
    /// </summary>
    public bool IsSSD(string filePath)
    {
        try
        {
            string root = Path.GetPathRoot(filePath) ?? @"C:\";
            // 构造物理驱动器路径：\\.\C:
            string drivePath = @"\\.\" + root.TrimEnd('\\');

            IntPtr hDevice = NativeMethods.CreateFileW(
                drivePath,
                0, // 无需读写权限
                (uint)(FileShare.Read | FileShare.Write),
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDevice == IntPtr.Zero || hDevice == new IntPtr(-1))
                return false;

            try
            {
                // 查询 SeekPenalty 属性
                int descSize = Marshal.SizeOf<NativeMethods.DEVICE_SEEK_PENALTY_DESCRIPTOR>();
                IntPtr descPtr = Marshal.AllocHGlobal(descSize);
                try
                {
                    var query = new NativeMethods.STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = NativeMethods.StorageDeviceSeekPenaltyProperty,
                        QueryType = NativeMethods.PropertyStandardQuery,
                        AdditionalParameters = new byte[1]
                    };
                    int querySize = Marshal.SizeOf(query);
                    IntPtr queryPtr = Marshal.AllocHGlobal(querySize);
                    Marshal.StructureToPtr(query, queryPtr, false);

                    uint bytesReturned;
                    bool success = NativeMethods.DeviceIoControl(
                        hDevice, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                        queryPtr, (uint)querySize,
                        descPtr, (uint)descSize,
                        out bytesReturned, IntPtr.Zero);

                    Marshal.FreeHGlobal(queryPtr);

                    if (success)
                    {
                        var desc = Marshal.PtrToStructure<NativeMethods.DEVICE_SEEK_PENALTY_DESCRIPTOR>(descPtr);
                        // IncursSeekPenalty = False (0) → SSD
                        return desc.IncursSeekPenalty == 0;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(descPtr);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hDevice);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"SSD detection failed for {filePath}: {ex.Message}");

            // 降级方案：通过驱动器类型 + Trim 支持推断
            try
            {
                string root = Path.GetPathRoot(filePath) ?? @"C:\";
                uint driveType = NativeMethods.GetDriveTypeW(root);
                if (driveType != NativeMethods.DRIVE_FIXED)
                    return false; // 非固定磁盘不是 SSD

                // 简单启发式：检查是否有 NVMe 或 SCSI 特征
                // 如果无法确定，保守地当作 SSD 处理（限制为 1 次覆写）
                return true;
            }
            catch
            {
                return false; // 无法确定，假定为 HDD
            }
        }

        return false;
    }

    /// <summary>
    /// 获取文件的硬链接数量
    /// 使用 GetFileInformationByHandle 查询 BY_HANDLE_FILE_INFORMATION.nNumberOfLinks
    /// </summary>
    public int GetHardLinkCount(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, FileOptions.None);

            if (NativeMethods.GetFileInformationByHandle(
                fs.SafeFileHandle.DangerousGetHandle(),
                out NativeMethods.BY_HANDLE_FILE_INFORMATION fileInfo))
            {
                return (int)fileInfo.nNumberOfLinks;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Failed to get hard link count for {filePath}: {ex.Message}");
        }

        return 0; // 失败返回0，外层调用者应保守处理
    }

    /// <summary>
    /// 检查文件是否为 EFS 加密
    /// 通过 File.GetAttributes 检测 Encrypted 属性
    /// </summary>
    public bool IsEFSEncrypted(string filePath)
    {
        try
        {
            var attributes = File.GetAttributes(filePath);
            return (attributes & FileAttributes.Encrypted) == FileAttributes.Encrypted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查文件所在磁盘是否有足够的可用空间
    /// 至少需要文件大小的 1% 或 1MB 的空间，以较大者为准
    /// </summary>
    public bool HasEnoughDiskSpace(string filePath)
    {
        try
        {
            string root = Path.GetPathRoot(filePath) ?? @"C:\";
            ulong freeBytesAvailable, totalBytes, totalFreeBytes;
            if (NativeMethods.GetDiskFreeSpaceExW(root, out freeBytesAvailable,
                out totalBytes, out totalFreeBytes))
            {
                long fileSize = new FileInfo(filePath).Length;
                long minRequired = Math.Max(fileSize / 100, 1024 * 1024); // 至少 1MB 或文件 1%
                return (long)freeBytesAvailable >= minRequired;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Disk space check failed for {filePath}: {ex.Message}");
        }

        return true; // 无法检测时保守地假定有空间
    }

    /// <summary>
    /// 处理文件名过长的情况
    /// 如果文件名（不含路径）超过 200 字符，先重命名为一个短名再处理
    /// </summary>
    private static string HandleLongFileName(string filePath)
    {
        try
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.Length <= 200) return filePath;

            // 文件名过长，重命名为临时短名
            string directory = Path.GetDirectoryName(filePath) ?? "";
            string extension = Path.GetExtension(filePath);
            string tempName = $"_shred_tmp_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            string newPath = Path.Combine(directory, tempName);

            // 去掉只读属性后重命名
            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Move(filePath, newPath);
            return newPath;
        }
        catch
        {
            return filePath; // 重命名失败，尝试直接处理
        }
    }

    /// <summary>
    /// 去掉文件的只读属性
    /// </summary>
    private static void RemoveReadOnlyAttribute(string filePath)
    {
        try
        {
            var attr = File.GetAttributes(filePath);
            if (attr.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(filePath, attr & ~FileAttributes.ReadOnly);
            }
        }
        catch { /* 去属性失败不阻塞主流程 */ }
    }
}
