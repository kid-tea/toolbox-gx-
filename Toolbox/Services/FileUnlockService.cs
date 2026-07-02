using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Toolbox.Helpers;
using Toolbox.Native;

namespace Toolbox.Services;

/// <summary>
/// 文件解锁服务接口
/// 封装 Restart Manager API 用于枚举占用文件的进程，并提供强制删除能力
/// </summary>
public interface IFileUnlockService
{
    /// <summary>
    /// 枚举占用指定文件的进程列表
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>占用该文件的进程信息列表</returns>
    List<ProcessLockInfo> GetLockingProcesses(string filePath);

    /// <summary>
    /// 尝试强制删除单个文件
    /// 流程：RM枚举占用进程 → 尝试终止 → 删除文件 → API降级方案
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="token">取消令牌</param>
    /// <returns>删除结果</returns>
    FileDeleteResultDto ForceDelete(string filePath, CancellationToken token = default);

    /// <summary>
    /// 批量强制删除文件
    /// </summary>
    /// <param name="filePaths">文件路径列表</param>
    /// <param name="progress">进度报告</param>
    /// <param name="token">取消令牌</param>
    /// <returns>各文件的删除结果列表</returns>
    List<FileDeleteResultDto> ForceDeleteBatch(
        IEnumerable<string> filePaths,
        IProgress<int>? progress = null,
        CancellationToken token = default);

    /// <summary>
    /// 解析 .lnk 快捷方式文件的目标路径
    /// </summary>
    /// <param name="shortcutPath">.lnk 文件路径</param>
    /// <returns>目标路径，解析失败返回空字符串</returns>
    string ResolveShortcutTarget(string shortcutPath);
}

/// <summary>
/// 占用文件的进程信息
/// </summary>
public class ProcessLockInfo
{
    /// <summary>进程 ID</summary>
    public uint ProcessId { get; set; }
    /// <summary>进程名称（如"explorer.exe"）</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>RM 报告的应用名称</summary>
    public string AppName { get; set; } = "";
    /// <summary>是否为不可终止的系统关键进程</summary>
    public bool IsNonTerminable { get; set; }
}

/// <summary>
/// 文件删除结果（DTO，在服务层和生产层间传递）
/// </summary>
public class FileDeleteResultDto
{
    /// <summary>文件路径</summary>
    public string FilePath { get; set; } = "";
    /// <summary>是否成功</summary>
    public bool Success { get; set; }
    /// <summary>中文错误消息</summary>
    public string ErrorMessage { get; set; } = "";
    /// <summary>英文错误消息</summary>
    public string ErrorMessageEn { get; set; } = "";
    /// <summary>占用进程列表</summary>
    public List<ProcessLockInfo> LockingProcesses { get; set; } = new();
}

/// <summary>
/// 文件解锁服务实现
/// 使用 Windows Restart Manager API 枚举文件占用，提供多种降级删除方案
/// </summary>
public class FileUnlockService : IFileUnlockService
{
    private readonly ILogService _log;

    public FileUnlockService(ILogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 枚举占用指定文件的进程
    /// </summary>
    public List<ProcessLockInfo> GetLockingProcesses(string filePath)
    {
        var result = new List<ProcessLockInfo>();

        // 标准化路径：使用 \\?\ 前缀支持长路径
        var normalizedPath = NormalizePath(filePath);

        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
            return result;

        uint sessionHandle;
        var sessionKey = new StringBuilder(256);
        uint ret = NativeMethods.RmStartSession(out sessionHandle, 0, sessionKey);
        if (ret != NativeMethods.RM_ERROR_SUCCESS)
        {
            _log.LogWarning($"RmStartSession failed: {ret}");
            return result;
        }

        try
        {
            // 注册文件资源
            ret = NativeMethods.RmRegisterResources(
                sessionHandle, 1, new[] { normalizedPath },
                0, null, 0, null);
            if (ret != NativeMethods.RM_ERROR_SUCCESS)
            {
                _log.LogWarning($"RmRegisterResources failed for '{filePath}': {ret}");
                return result;
            }

            // 获取占用进程列表
            uint procInfoNeeded = 0;
            uint procInfoCount = 10; // 初始分配10个槽位
            uint rebootReasons = 0;

            ret = NativeMethods.RmGetList(sessionHandle, out procInfoNeeded, ref procInfoCount, null, ref rebootReasons);
            if (ret == NativeMethods.RM_ERROR_MORE_DATA)
            {
                // 需要更多空间
                procInfoCount = procInfoNeeded;
                var procInfos = new NativeMethods.RM_PROCESS_INFO[procInfoNeeded];
                ret = NativeMethods.RmGetList(sessionHandle, out procInfoNeeded, ref procInfoCount, procInfos, ref rebootReasons);
                if (ret == NativeMethods.RM_ERROR_SUCCESS)
                {
                    for (int i = 0; i < procInfoCount; i++)
                    {
                        var info = procInfos[i];
                        string processName = "";
                        try
                        {
                            var proc = Process.GetProcessById((int)info.Process.dwProcessId);
                            processName = proc.ProcessName;
                        }
                        catch
                        {
                            processName = $"PID:{info.Process.dwProcessId}";
                        }

                        result.Add(new ProcessLockInfo
                        {
                            ProcessId = info.Process.dwProcessId,
                            ProcessName = processName,
                            AppName = info.strAppName ?? "",
                            IsNonTerminable = ProtectedPaths.IsNonTerminableProcess(processName)
                        });
                    }
                }
            }
            else if (ret == NativeMethods.RM_ERROR_SUCCESS && procInfoCount > 0)
            {
                // procInfoCount 可能已经返回非零但之前没分配数组，这种情况很少见但保守处理
            }
        }
        finally
        {
            NativeMethods.RmEndSession(sessionHandle);
        }

        return result;
    }

    /// <summary>
    /// 强制删除单个文件
    /// </summary>
    public FileDeleteResultDto ForceDelete(string filePath, CancellationToken token = default)
    {
        var result = new FileDeleteResultDto { FilePath = filePath };

        if (token.IsCancellationRequested) return result;

        // 检查受保护路径
        if (ProtectedPaths.IsProtectedPath(filePath))
        {
            result.ErrorMessage = "该文件位于系统受保护路径，无法强制删除（File is in a protected system path）";
            result.ErrorMessageEn = "File is in a protected system path";
            return result;
        }

        // 检查文件是否存在
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            result.Success = true; // 文件已不存在，视为成功
            return result;
        }

        bool isDirectory = Directory.Exists(filePath);

        // TOCTOU 检测：记录尝试删除前的文件信息，事后验证
        DateTime? beforeLastWrite = null;
        long? beforeLength = null;
        try
        {
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                beforeLastWrite = fileInfo.LastWriteTimeUtc;
                beforeLength = fileInfo.Length;
            }
        }
        catch { /* 获取信息失败不影响后续流程 */ }

        // 步骤1：RM API 枚举占用进程并尝试终止
        var lockingProcesses = GetLockingProcesses(filePath);
        result.LockingProcesses = lockingProcesses;

        if (lockingProcesses.Count > 0)
        {
            // 检查是否存在不可终止的系统进程
            if (lockingProcesses.Any(p => p.IsNonTerminable))
            {
                var nonTermProc = lockingProcesses.First(p => p.IsNonTerminable);
                result.ErrorMessage = $"文件被系统关键进程占用: {nonTermProc.ProcessName}（" +
                    $"PID: {nonTermProc.ProcessId}），该进程不可被终止。\n" +
                    $"建议进入安全模式后重试，或在系统重启后再删除。";
                result.ErrorMessageEn = $"File is locked by a critical system process: {nonTermProc.ProcessName} " +
                    $"(PID: {nonTermProc.ProcessId}), which cannot be terminated.\n" +
                    $"Try deleting in Safe Mode or after system restart.";
                return result;
            }

            // 尝试终止非关键占用进程
            foreach (var proc in lockingProcesses.Where(p => !p.IsNonTerminable))
            {
                try
                {
                    var process = Process.GetProcessById((int)proc.ProcessId);
                    _log.LogInfo($"Terminating process: {proc.ProcessName} (PID: {proc.ProcessId})");
                    process.Kill();
                    process.WaitForExit(5000); // 等待最多5秒
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Failed to kill process {proc.ProcessName}: {ex.Message}");
                }
            }

            // 终止后等待一小段时间让文件句柄释放
            Thread.Sleep(500);
        }

        // 步骤2：尝试删除文件
        if (TryDeleteFile(filePath, isDirectory, result))
        {
            // TOCTOU 验证：确认删除成功
            if (File.Exists(filePath) || Directory.Exists(filePath))
            {
                result.Success = false;
                result.ErrorMessage = "删除操作未生效：文件仍然存在（可能存在锁定或权限问题）";
                result.ErrorMessageEn = "Delete operation had no effect: file still exists (may be locked or permission issue)";
            }
            else
            {
                result.Success = true;
                _log.LogInfo($"Successfully deleted: {filePath}");
            }
        }

        return result;
    }

    /// <summary>
    /// 批量强制删除文件
    /// </summary>
    public List<FileDeleteResultDto> ForceDeleteBatch(
        IEnumerable<string> filePaths,
        IProgress<int>? progress = null,
        CancellationToken token = default)
    {
        var paths = filePaths.ToList();
        var results = new List<FileDeleteResultDto>();

        int total = paths.Count;
        int completed = 0;

        // 超过1000个文件时分批处理
        const int batchSize = 1000;
        for (int i = 0; i < total; i += batchSize)
        {
            var batch = paths.Skip(i).Take(batchSize);
            foreach (var path in batch)
            {
                if (token.IsCancellationRequested) return results;
                results.Add(ForceDelete(path, token));
                completed++;
                progress?.Report(completed * 100 / total);
            }
        }

        return results;
    }

    /// <summary>
    /// 尝试多种方式删除文件（API 降级方案）
    /// 1. 尝试直接 File.Delete / Directory.Delete
    /// 2. 去掉只读属性后重试
    /// 3. 文件夹递归删除
    /// </summary>
    private bool TryDeleteFile(string filePath, bool isDirectory, FileDeleteResultDto result)
    {
        try
        {
            // 使用 \\?\ 前缀支持超长路径
            var normalizedPath = NormalizePath(filePath);

            // 先去掉只读属性
            RemoveReadOnlyAttribute(normalizedPath, isDirectory);

            if (isDirectory)
            {
                // 递归删除目录（处理非空目录）
                var dirInfo = new DirectoryInfo(normalizedPath);
                // 去掉所有子文件和子目录的只读属性
                foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                        file.Attributes &= ~FileAttributes.ReadOnly;
                }
                foreach (var dir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
                {
                    if (dir.Attributes.HasFlag(FileAttributes.ReadOnly))
                        dir.Attributes &= ~FileAttributes.ReadOnly;
                }
                Directory.Delete(normalizedPath, recursive: true);
            }
            else
            {
                // 方案1：直接删除
                File.Delete(normalizedPath);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 方案2：去掉只读/系统/隐藏属性后重试
            try
            {
                RemoveAllAttributes(filePath, isDirectory);
                if (isDirectory)
                    Directory.Delete(NormalizePath(filePath), recursive: true);
                else
                    File.Delete(NormalizePath(filePath));
                return true;
            }
            catch (Exception ex2)
            {
                result.ErrorMessage = $"权限不足，删除被拒绝: {ex2.Message}";
                result.ErrorMessageEn = $"Access denied: {ex2.Message}";
                return false;
            }
        }
        catch (IOException ex)
        {
            result.ErrorMessage = $"文件被占用或正在使用: {ex.Message}";
            result.ErrorMessageEn = $"File is in use: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"删除失败: {ex.Message}";
            result.ErrorMessageEn = $"Delete failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 标准化文件路径，使用 \\?\ 前缀支持超长路径（>260字符）
    /// </summary>
    private static string NormalizePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;

        // 已包含 \\?\ 前缀则不重复添加
        if (filePath.StartsWith(@"\\?\")) return filePath;

        // 绝对路径添加 \\?\ 前缀以突破 260 字符限制
        if (Path.IsPathRooted(filePath) && filePath.Length > 260)
            return @"\\?\" + filePath;

        return filePath;
    }

    /// <summary>
    /// 去掉文件的只读属性
    /// </summary>
    private static void RemoveReadOnlyAttribute(string filePath, bool isDirectory)
    {
        try
        {
            FileAttributes attr;
            if (isDirectory)
            {
                attr = new DirectoryInfo(filePath).Attributes;
                if (attr.HasFlag(FileAttributes.ReadOnly))
                {
                    new DirectoryInfo(filePath).Attributes &= ~FileAttributes.ReadOnly;
                }
            }
            else
            {
                attr = File.GetAttributes(filePath);
                if (attr.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(filePath, attr & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch { /* 去属性失败不阻塞主流程 */ }
    }

    /// <summary>
    /// 去掉文件/目录的全部特殊属性（只读、系统、隐藏）
    /// </summary>
    private static void RemoveAllAttributes(string filePath, bool isDirectory)
    {
        try
        {
            FileAttributes attr;
            if (isDirectory)
            {
                attr = new DirectoryInfo(filePath).Attributes;
                new DirectoryInfo(filePath).Attributes = attr & ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);
            }
            else
            {
                attr = File.GetAttributes(filePath);
                File.SetAttributes(filePath, attr & ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden));
            }
        }
        catch { /* 去属性失败不阻塞主流程 */ }
    }

    /// <summary>
    /// 解析 .lnk 快捷方式文件的目标路径
    /// 使用 COM ShellLink 对象进行解析
    /// </summary>
    public string ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return "";

            // 使用 Shell32 COM 组件解析快捷方式
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return "";

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string target = shortcut.TargetPath ?? "";
            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
            return target;
        }
        catch
        {
            _log.LogWarning($"Failed to resolve shortcut: {shortcutPath}");
            return "";
        }
    }
}
