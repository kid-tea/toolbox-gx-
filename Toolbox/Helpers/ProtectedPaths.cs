using System.IO;

namespace Toolbox.Helpers;

/// <summary>
/// 受保护路径和不可终止进程的全局定义
/// 所有涉及删除/修改的功能统一引用此处的名单
/// 包含：受保护目录、受保护文件扩展名、不可终止进程
/// </summary>
public static class ProtectedPaths
{
    /// <summary>
    /// 受保护目录列表（全局定义）
    /// 这些目录及其子目录下的文件不可被删除或修改
    /// </summary>
    public static readonly string[] Paths = new[]
    {
        @"C:\Windows\",
        @"C:\Program Files\",
        @"C:\Program Files (x86)\",
        @"C:\ProgramData\"
    };

    /// <summary>
    /// 受保护文件扩展名列表
    /// 这些扩展名的文件不可被删除（如驱动程序 .sys）
    /// </summary>
    public static readonly string[] ProtectedExtensions = new[] { ".sys" };

    /// <summary>
    /// 不可终止进程名单（全局定义）
    /// 这些系统关键进程在任何情况下不可被工具箱终止
    /// </summary>
    public static readonly string[] NonTerminableProcesses = new[]
    {
        "System",
        "System Idle Process",
        "csrss.exe",
        "winlogon.exe",
        "smss.exe",
        "services.exe",
        "lsass.exe",
        "wininit.exe",
        "svchost.exe",
        "RuntimeBroker.exe",
        "MsMpEng.exe"  // Windows Defender 核心进程
    };

    /// <summary>
    /// 判断指定路径是否在受保护目录下
    /// </summary>
    /// <param name="path">要检查的文件或目录路径</param>
    /// <returns>如果在受保护路径下返回 true，否则 false</returns>
    public static bool IsProtectedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // 标准化路径分隔符，确保以 \ 结尾以精确匹配目录前缀
        var normalized = path.Replace("/", "\\").TrimEnd('\\') + "\\";
        return Paths.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断指定文件是否属于受保护扩展名类型
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>如果扩展名受保护返回 true，否则 false</returns>
    public static bool IsProtectedExtension(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return !string.IsNullOrEmpty(ext) && ProtectedExtensions.Contains(ext);
    }

    /// <summary>
    /// 判断指定进程是否在不可终止名单中
    /// </summary>
    /// <param name="processName">进程名称（含 .exe）</param>
    /// <returns>如果在不可终止名单中返回 true，否则 false</returns>
    public static bool IsNonTerminableProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;

        return NonTerminableProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }
}
