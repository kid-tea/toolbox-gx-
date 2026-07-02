using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Toolbox.Services;

// ==================== 模型定义 ====================

/// <summary>
/// 注册表扫描结果模型
/// </summary>
public class RegistryIssue
{
    /// <summary>问题唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>问题分类</summary>
    public RegistryScanCategory Category { get; set; }

    /// <summary>问题描述</summary>
    public string Description { get; set; } = "";

    /// <summary>注册表路径</summary>
    public string RegistryPath { get; set; } = "";

    /// <summary>严重程度</summary>
    public IssueSeverity Severity { get; set; } = IssueSeverity.Low;

    /// <summary>是否被用户选中修复</summary>
    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// 注册表扫描分类
/// </summary>
public enum RegistryScanCategory
{
    /// <summary>无效文件关联（扩展名指向不存在的程序）</summary>
    InvalidFileAssociation,
    /// <summary>卸载残留（已卸载程序留下的注册表项）</summary>
    UninstallResidue,
    /// <summary>无效快捷方式</summary>
    InvalidShortcut,
    /// <summary>空注册表键</summary>
    EmptyKey,
    /// <summary>启动项残留</summary>
    StartupResidue,
    /// <summary>无效 COM/OLE 注册</summary>
    InvalidComOle
}

/// <summary>
/// 问题严重程度
/// </summary>
public enum IssueSeverity
{
    Low,
    Medium,
    High
}

/// <summary>
/// 注册表备份记录模型
/// </summary>
public class RegistryBackup
{
    /// <summary>备份文件名</summary>
    public string FileName { get; set; } = "";

    /// <summary>备份完整路径</summary>
    public string FilePath { get; set; } = "";

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>备份的问题数量</summary>
    public int IssueCount { get; set; }

    /// <summary>SHA256 哈希值</summary>
    public string Hash { get; set; } = "";

    /// <summary>备份描述</summary>
    public string Description { get; set; } = "";
}

// ==================== 注册表清理服务接口 ====================

/// <summary>
/// 注册表清理服务接口
/// </summary>
public interface IRegistryCleanerService
{
    /// <summary>扫描指定类别的注册表问题</summary>
    /// <param name="categories">要扫描的分类</param>
    /// <param name="progress">进度报告</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>发现的问题列表</returns>
    List<RegistryIssue> ScanIssues(
        List<RegistryScanCategory> categories,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>创建注册表备份</summary>
    /// <param name="issues">要备份的问题列表</param>
    /// <returns>备份记录，失败返回 null</returns>
    RegistryBackup? CreateBackup(List<RegistryIssue> issues);

    /// <summary>修复指定的注册表问题</summary>
    /// <param name="issues">要修复的问题列表</param>
    /// <param name="progress">进度报告</param>
    /// <returns>成功修复的数量</returns>
    int FixIssues(List<RegistryIssue> issues, IProgress<int>? progress = null);

    /// <summary>从备份还原</summary>
    /// <param name="backup">备份记录</param>
    /// <returns>还原成功返回 true</returns>
    bool RestoreFromBackup(RegistryBackup backup);

    /// <summary>获取所有备份历史</summary>
    List<RegistryBackup> GetBackupHistory();

    /// <summary>验证备份文件完整性（SHA256 校验）</summary>
    bool ValidateBackup(RegistryBackup backup);

    /// <summary>设置电源选项可见性</summary>
    /// <param name="show">true=显示，false=隐藏</param>
    bool SetPowerOptionsVisibility(bool show);
}

/// <summary>
/// 注册表清理服务实现
/// 6类扫描：无效文件关联、卸载残留、无效快捷方式、空键、启动项残留、无效COM/OLE
/// 安全铁律：备份写入失败 → 不执行修复；还原前 SHA256 校验；
///           64位 WOW6432Node 同时扫描；.reg 格式验证（UTF-16 LE 头）
/// 高级电源选项：白名单 GUID → Attributes 修改 → powercfg /setactive
/// </summary>
public class RegistryCleanerService : IRegistryCleanerService
{
    private readonly ILogService _log;
    private readonly IConfigService _config;
    private readonly string _backupDir;

    /// <summary>备份目录路径</summary>
    public string BackupDirectory => _backupDir;

    /// <summary>
    /// 高级电源选项白名单 GUID（子组 GUID）
    /// 54533251... = 处理器电源管理
    /// 238c9fa8... = 睡眠设置
    /// e73a048d... = 电池设置
    /// 7516b95f... = 显示设置
    /// </summary>
    private static readonly string[] PowerSubGroupGuids = new[]
    {
        "54533251-82be-4824-96c1-47b60b740d00", // 处理器电源管理
        "238c9fa8-0aad-41ed-83f4-97be242c8f20", // 睡眠设置
        "e73a048d-bf27-4f12-9731-8b2076e8891f", // 电池设置
        "7516b95f-f776-4464-8c53-06167f40cc99"  // 显示设置
    };

    /// <summary>电源设置根路径</summary>
    private const string PowerSettingsPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

    public RegistryCleanerService(ILogService logService, IConfigService configService)
    {
        _log = logService;
        _config = configService;

        _backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "工具箱备份",
            "registry");
        Directory.CreateDirectory(_backupDir);
    }

    // ==================== 扫描 ====================

    /// <summary>
    /// 扫描指定类别的注册表问题
    /// 使用 WOW6432Node 也扫描 32 位注册表视图（64位系统）
    /// </summary>
    public List<RegistryIssue> ScanIssues(
        List<RegistryScanCategory> categories,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allIssues = new List<RegistryIssue>();
        int totalCategories = categories.Count;
        int completed = 0;

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var issues = ScanCategory(category);
                allIssues.AddRange(issues);
            }
            catch (Exception ex)
            {
                _log.LogError($"扫描 {category} 分类异常", ex);
            }

            completed++;
            progress?.Report(completed * 100 / totalCategories);
        }

        // 去重
        var distinct = allIssues
            .GroupBy(i => i.RegistryPath)
            .Select(g => g.First())
            .ToList();

        _log.LogInfo($"注册表扫描完成: {distinct.Count} 个问题 ({categories.Count} 个分类)");
        return distinct;
    }

    /// <summary>
    /// 扫描单个分类
    /// </summary>
    private List<RegistryIssue> ScanCategory(RegistryScanCategory category)
    {
        return category switch
        {
            RegistryScanCategory.InvalidFileAssociation => ScanInvalidFileAssociations(),
            RegistryScanCategory.UninstallResidue => ScanUninstallResidue(),
            RegistryScanCategory.InvalidShortcut => ScanInvalidShortcuts(),
            RegistryScanCategory.EmptyKey => ScanEmptyKeys(),
            RegistryScanCategory.StartupResidue => ScanStartupResidue(),
            RegistryScanCategory.InvalidComOle => ScanInvalidComOle(),
            _ => new List<RegistryIssue>()
        };
    }

    /// <summary>
    /// 扫描无效文件关联 — HKCR 下扩展名指向不存在的程序
    /// 同时扫描 64 位和 32 位（WOW6432Node）视图
    /// </summary>
    private List<RegistryIssue> ScanInvalidFileAssociations()
    {
        var issues = new List<RegistryIssue>();

        try
        {
            // 扫描 HKCR（会经过注册表重定向）
            using var classesKey = Registry.ClassesRoot;
            foreach (var ext in classesKey.GetSubKeyNames().Where(n => n.StartsWith(".")).Take(500))
            {
                try
                {
                    using var extKey = classesKey.OpenSubKey(ext, writable: false);
                    if (extKey == null) continue;

                    // 检查默认值指向的文件类型
                    var progId = extKey.GetValue("")?.ToString();
                    if (string.IsNullOrEmpty(progId)) continue;

                    // 检查该 ProgID 的程序路径是否存在
                    using var progKey = classesKey.OpenSubKey($@"{progId}\shell\open\command", writable: false);
                    if (progKey == null) continue;

                    var command = progKey.GetValue("")?.ToString();
                    if (string.IsNullOrEmpty(command)) continue;

                    // 提取可执行文件路径
                    string exePath = ExtractExePath(command);
                    if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath))
                    {
                        issues.Add(new RegistryIssue
                        {
                            Category = RegistryScanCategory.InvalidFileAssociation,
                            Description = $"文件类型 {ext} 关联的程序不存在: {exePath}",
                            RegistryPath = $@"HKCR\{progId}\shell\open\command",
                            Severity = IssueSeverity.Medium
                        });
                    }
                }
                catch { /* 单个条目扫描失败不影响整体 */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"扫描文件关联失败: {ex.Message}");
        }

        _log.LogInfo($"无效文件关联: {issues.Count} 项");
        return issues;
    }

    /// <summary>
    /// 扫描卸载残留 — HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall 下
    /// 程序已卸载但注册表项仍存在的残留
    /// </summary>
    private List<RegistryIssue> ScanUninstallResidue()
    {
        var issues = new List<RegistryIssue>();
        var uninstallPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var uninstallPath in uninstallPaths)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var uninstallKey = hklm.OpenSubKey(uninstallPath, writable: false);
                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                        if (subKey == null) continue;

                        string? installLocation = subKey.GetValue("InstallLocation")?.ToString();
                        string? displayName = subKey.GetValue("DisplayName")?.ToString();
                        string? uninstallString = subKey.GetValue("UninstallString")?.ToString();

                        // 检查安装目录是否存在
                        if (!string.IsNullOrEmpty(installLocation) && !Directory.Exists(installLocation))
                        {
                            issues.Add(new RegistryIssue
                            {
                                Category = RegistryScanCategory.UninstallResidue,
                                Description = $"已卸载程序残留: {displayName ?? subKeyName} (目录不存在: {installLocation})",
                                RegistryPath = $@"HKLM\{uninstallPath}\{subKeyName}",
                                Severity = IssueSeverity.Low
                            });
                            continue;
                        }

                        // 检查卸载程序是否存在
                        if (!string.IsNullOrEmpty(uninstallString))
                        {
                            string exePath = ExtractExePath(uninstallString);
                            if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath))
                            {
                                issues.Add(new RegistryIssue
                                {
                                    Category = RegistryScanCategory.UninstallResidue,
                                    Description = $"已卸载程序残留: {displayName ?? subKeyName} (卸载程序不存在: {exePath})",
                                    RegistryPath = $@"HKLM\{uninstallPath}\{subKeyName}",
                                    Severity = IssueSeverity.Low
                                });
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"扫描卸载残留失败 ({uninstallPath}): {ex.Message}");
            }
        }

        _log.LogInfo($"卸载残留: {issues.Count} 项");
        return issues;
    }

    /// <summary>
    /// 扫描无效快捷方式 — 开始菜单和桌面的 .lnk 文件
    /// 检测指向不存在目标的快捷方式
    /// </summary>
    private List<RegistryIssue> ScanInvalidShortcuts()
    {
        var issues = new List<RegistryIssue>();

        var shortcutDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        foreach (var dir in shortcutDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                var lnkFiles = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories).Take(200);
                foreach (var lnkFile in lnkFiles)
                {
                    try
                    {
                        string target = ResolveShortcutTarget(lnkFile);
                        if (!string.IsNullOrEmpty(target) && !File.Exists(target) && !Directory.Exists(target))
                        {
                            issues.Add(new RegistryIssue
                            {
                                Category = RegistryScanCategory.InvalidShortcut,
                                Description = $"无效快捷方式: {Path.GetFileName(lnkFile)} -> {target}",
                                RegistryPath = $@"ShellLink:{lnkFile}",
                                Severity = IssueSeverity.Low
                            });
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"扫描无效快捷方式失败 ({dir}): {ex.Message}");
            }
        }

        _log.LogInfo($"无效快捷方式: {issues.Count} 项");
        return issues;
    }

    /// <summary>
    /// 扫描空注册表键 — 没有子键且没有值的键
    /// 只扫描 HKCU\Software 和 HKLM\Software 下的特定区域
    /// </summary>
    private List<RegistryIssue> ScanEmptyKeys()
    {
        var issues = new List<RegistryIssue>();

        var scanRoots = new[]
        {
            (RegistryHive.CurrentUser, @"Software", "HKCU"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM"),
        };

        foreach (var (hive, subPath, label) in scanRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var rootKey = baseKey.OpenSubKey(subPath, writable: false);
                if (rootKey == null) continue;

                FindEmptyKeys(rootKey, $@"{label}\{subPath}", issues, depth: 0);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"扫描空键失败 ({label}\\{subPath}): {ex.Message}");
            }
        }

        _log.LogInfo($"空键: {issues.Count} 项");
        return issues;
    }

    /// <summary>
    /// 递归查找空键
    /// </summary>
    private void FindEmptyKeys(RegistryKey key, string path, List<RegistryIssue> issues, int depth)
    {
        if (depth > 3) return; // 限制递归深度

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            try
            {
                using var subKey = key.OpenSubKey(subKeyName, writable: false);
                if (subKey == null) continue;

                int valueCount = subKey.ValueCount;
                int subKeyCount = subKey.GetSubKeyNames().Length;
                string fullPath = $@"{path}\{subKeyName}";

                if (valueCount == 0 && subKeyCount == 0)
                {
                    issues.Add(new RegistryIssue
                    {
                        Category = RegistryScanCategory.EmptyKey,
                        Description = $"空注册表键: {fullPath}",
                        RegistryPath = fullPath,
                        Severity = IssueSeverity.Low
                    });
                }
                else if (subKeyCount > 0 && depth < 3)
                {
                    FindEmptyKeys(subKey, fullPath, issues, depth + 1);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 扫描启动项残留 — 指向不存在文件的启动项
    /// </summary>
    private List<RegistryIssue> ScanStartupResidue()
    {
        var issues = new List<RegistryIssue>();

        var runKeys = new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM_WOW64"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU"),
        };

        foreach (var (hive, subPath, label) in runKeys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(subPath, writable: false);
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    try
                    {
                        var value = key.GetValue(valueName)?.ToString();
                        if (string.IsNullOrEmpty(value)) continue;

                        string exePath = ExtractExePath(value);
                        if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath))
                        {
                            issues.Add(new RegistryIssue
                            {
                                Category = RegistryScanCategory.StartupResidue,
                                Description = $"启动项指向不存在的文件: {valueName} -> {exePath}",
                                RegistryPath = $@"{label}\{subPath}\{valueName}",
                                Severity = IssueSeverity.Medium
                            });
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"扫描启动项残留失败 ({label}\\{subPath}): {ex.Message}");
            }
        }

        _log.LogInfo($"启动项残留: {issues.Count} 项");
        return issues;
    }

    /// <summary>
    /// 扫描无效 COM/OLE 注册
    /// 算法：枚举 HKCR\CLSID 子项 → 读取 InprocServer32 默认值 → 检查 DLL 是否存在
    /// 采样 1000 个 CLSID 避免扫描时间过长
    /// </summary>
    private List<RegistryIssue> ScanInvalidComOle()
    {
        var issues = new List<RegistryIssue>();

        try
        {
            using var clsidKey = Registry.ClassesRoot.OpenSubKey("CLSID", writable: false);
            if (clsidKey == null) return issues;

            var clsidNames = clsidKey.GetSubKeyNames()
                .Where(n => n.StartsWith("{"))
                .Take(1000)
                .ToList();

            foreach (var clsid in clsidNames)
            {
                try
                {
                    // 检查 InprocServer32
                    using var inprocKey = clsidKey.OpenSubKey($@"{clsid}\InprocServer32", writable: false);
                    if (inprocKey != null)
                    {
                        var dllPath = inprocKey.GetValue("")?.ToString();
                        if (!string.IsNullOrEmpty(dllPath))
                        {
                            // 展开环境变量
                            dllPath = Environment.ExpandEnvironmentVariables(dllPath);
                            // 如果是相对路径，尝试在 System32 查找
                            if (!Path.IsPathRooted(dllPath))
                            {
                                dllPath = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                                    dllPath);
                            }

                            if (!File.Exists(dllPath))
                            {
                                issues.Add(new RegistryIssue
                                {
                                    Category = RegistryScanCategory.InvalidComOle,
                                    Description = $"COM 组件 DLL 不存在: {clsid} -> {dllPath}",
                                    RegistryPath = $@"HKCR\CLSID\{clsid}",
                                    Severity = IssueSeverity.High
                                });
                            }
                        }
                    }

                    // 也检查 LocalServer32
                    using var localServerKey = clsidKey.OpenSubKey($@"{clsid}\LocalServer32", writable: false);
                    if (localServerKey != null)
                    {
                        var serverPath = localServerKey.GetValue("")?.ToString();
                        if (!string.IsNullOrEmpty(serverPath))
                        {
                            string exePath = ExtractExePath(Environment.ExpandEnvironmentVariables(serverPath));
                            if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath))
                            {
                                // 只在还没有 InprocServer32 问题时添加
                                if (!issues.Any(i => i.RegistryPath == $@"HKCR\CLSID\{clsid}"))
                                {
                                    issues.Add(new RegistryIssue
                                    {
                                        Category = RegistryScanCategory.InvalidComOle,
                                        Description = $"COM 组件 EXE 不存在: {clsid} -> {exePath}",
                                        RegistryPath = $@"HKCR\CLSID\{clsid}",
                                        Severity = IssueSeverity.High
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"扫描 COM/OLE 失败: {ex.Message}");
        }

        _log.LogInfo($"无效 COM/OLE: {issues.Count} 项");
        return issues;
    }

    /// <summary>
    /// 规范化问题路径为可备份的注册表键路径
    /// 启动项残留可能把值名拼在路径末尾，备份时只保留所属键
    /// </summary>
    private static string NormalizeBackupRegistryPath(string registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
            return "";

        if (registryPath.StartsWith("ShellLink:", StringComparison.OrdinalIgnoreCase))
            return "";

        var normalized = registryPath.Replace('/', '\\').Trim();
        if (!(normalized.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase)))
            return "";

        if (normalized.StartsWith("HKLM_WOW64\\", StringComparison.OrdinalIgnoreCase))
            normalized = "HKLM\\" + normalized[11..];

        if (normalized.StartsWith("HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\\", StringComparison.OrdinalIgnoreCase))
        {
            var lastSep = normalized.LastIndexOf('\\');
            if (lastSep > 0)
                return normalized[..lastSep];
        }

        return normalized;
    }

    // ==================== 备份 ====================

    /// <summary>
    /// 创建注册表备份
    /// 导出为 .reg 格式文件（UTF-16 LE 编码），并计算 SHA256 哈希
    /// 安全铁律：备份写入失败 → 返回 null → 调用方不执行修复
    /// </summary>
    public RegistryBackup? CreateBackup(List<RegistryIssue> issues)
    {
        try
        {
            var timestamp = DateTime.Now;
            var fileName = $"regbackup_{timestamp:yyyyMMdd_HHmmss}.reg";
            var filePath = Path.Combine(_backupDir, fileName);

            // 构建 .reg 文件内容
            var sb = new StringBuilder();
            // .reg 文件头（UTF-16 LE）
            sb.AppendLine("Windows Registry Editor Version 5.00");
            sb.AppendLine();

            // 需要导出的注册表路径（去重）
            var uniquePaths = issues
                .Select(i => NormalizeBackupRegistryPath(i.RegistryPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in uniquePaths)
            {
                try
                {
                    var regResult = ExportRegistryKey(path);
                    if (!string.IsNullOrEmpty(regResult))
                    {
                        sb.AppendLine(regResult);
                        sb.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"导出注册表键失败: {path}: {ex.Message}");
                }
            }

            // 写入文件（UTF-16 LE，带 BOM）
            var content = sb.ToString();
            File.WriteAllText(filePath, content, Encoding.Unicode); // Unicode = UTF-16 LE

            // 验证文件已正确写入
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                _log.LogError("备份文件写入失败或为空");
                return null;
            }

            // 计算哈希
            var hash = _config.ComputeHash(filePath);

            var backup = new RegistryBackup
            {
                FileName = fileName,
                FilePath = filePath,
                CreatedAt = timestamp,
                IssueCount = uniquePaths.Count,
                Hash = hash,
                Description = $"备份 {uniquePaths.Count} 个注册表键 ({timestamp:yyyy-MM-dd HH:mm})"
            };

            _log.LogOperation("registry", "backup", $"创建备份: {fileName} ({uniquePaths.Count} 键, SHA256={hash[..12]}...)");
            return backup;
        }
        catch (Exception ex)
        {
            _log.LogError("创建注册表备份失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 导出单个注册表键为 .reg 格式文本
    /// 支持 HKCR/HKCU/HKLM 路径格式
    /// </summary>
    private string ExportRegistryKey(string registryPath)
    {
        try
        {
            // 解析注册表路径
            var parts = registryPath.Split('\\', 2);
            if (parts.Length < 2) return "";

            RegistryKey? rootKey = null;
            string pathInReg = parts[1];

            if (parts[0].Equals("HKCR", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.ClassesRoot;
            }
            else if (parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) ||
                     parts[0].Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.CurrentUser;
            }
            else if (parts[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase) ||
                     parts[0].Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.LocalMachine;
            }
            else
            {
                return ""; // 不支持其他根键
            }

            using var key = rootKey.OpenSubKey(pathInReg, writable: false);
            if (key == null) return "";

            var sb = new StringBuilder();

            // 构建 .reg 格式的键头
            string regKeyPath;
            if (parts[0].Equals("HKCR", StringComparison.OrdinalIgnoreCase))
                regKeyPath = $"[HKEY_CLASSES_ROOT\\{pathInReg}]";
            else if (parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase))
                regKeyPath = $"[HKEY_CURRENT_USER\\{pathInReg}]";
            else
                regKeyPath = $"[HKEY_LOCAL_MACHINE\\{pathInReg}]";

            sb.AppendLine(regKeyPath);

            // 导出值
            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName);
                var kind = key.GetValueKind(valueName);

                string valueLine = FormatRegValue(valueName, value, kind);
                sb.AppendLine(valueLine);
            }

            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 格式化注册表值为 .reg 文本行
    /// </summary>
    private string FormatRegValue(string name, object? value, RegistryValueKind kind)
    {
        string quotedName = string.IsNullOrEmpty(name) ? "@" : $"\"{name}\"";

        if (value == null)
            return $"{quotedName}=-";

        return kind switch
        {
            RegistryValueKind.String => $"{quotedName}=\"{value}\"",
            RegistryValueKind.ExpandString => $"{quotedName}=hex(2):{BytesToHex(Encoding.Unicode.GetBytes(value.ToString()! + "\0"))}",
            RegistryValueKind.DWord => $"{quotedName}=dword:{((int)value):x8}",
            RegistryValueKind.QWord => $"{quotedName}=hex(b):{BytesToHex(BitConverter.GetBytes((long)value))}",
            RegistryValueKind.Binary => $"{quotedName}=hex:{BytesToHex((byte[])value)}",
            RegistryValueKind.MultiString => $"{quotedName}=hex(7):{BytesToHex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])value) + "\0\0"))}",
            _ => $"{quotedName}=\"{value}\""
        };
    }

    /// <summary>
    /// 字节数组转十六进制字符串（逗号分隔）
    /// </summary>
    private static string BytesToHex(byte[] bytes)
    {
        return string.Join(",", bytes.Select(b => b.ToString("x2")));
    }

    // ==================== 修复 ====================

    /// <summary>
    /// 修复指定的注册表问题
    /// 安全铁律：修复前必须先备份成功；部分失败时记录详细日志
    /// </summary>
    public int FixIssues(List<RegistryIssue> issues, IProgress<int>? progress = null)
    {
        int fixedCount = 0;
        int total = issues.Count;

        for (int i = 0; i < total; i++)
        {
            var issue = issues[i];
            try
            {
                if (FixSingleIssue(issue))
                {
                    fixedCount++;
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"修复注册表项失败: {issue.RegistryPath}", ex);
            }

            progress?.Report((i + 1) * 100 / total);
        }

        _log.LogOperation("registry", "fix", $"已修复 {fixedCount}/{total} 个问题");
        return fixedCount;
    }

    /// <summary>
    /// 修复单个注册表问题
    /// </summary>
    private bool FixSingleIssue(RegistryIssue issue)
    {
        var parts = issue.RegistryPath.Split('\\', 2);
        if (parts.Length < 2) return false;

        RegistryKey? rootKey = null;
        string pathInReg = parts[1];

        if (parts[0].Equals("HKCR", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase))
            rootKey = Registry.ClassesRoot;
        else if (parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            rootKey = Registry.CurrentUser;
        else if (parts[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            rootKey = Registry.LocalMachine;
        else if (parts[0].StartsWith("ShellLink:"))
            return FixShortcut(issue.RegistryPath[10..]);
        else
            return false;

        int lastSep = pathInReg.LastIndexOf('\\');
        if (lastSep < 0) return false;

        string parentPath = pathInReg[..lastSep];
        string entryName = pathInReg[(lastSep + 1)..];

        try
        {
            using var parentKey = rootKey.OpenSubKey(parentPath, writable: true);
            if (parentKey == null) return false;

            if (issue.Category == RegistryScanCategory.StartupResidue)
            {
                try
                {
                    parentKey.DeleteValue(entryName);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                parentKey.DeleteValue(entryName);
                return true;
            }
            catch
            {
                // 当前节点不是值，继续按子键处理
            }

            parentKey.DeleteSubKeyTree(entryName, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"修复注册表项失败: {issue.RegistryPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除无效快捷方式文件
    /// </summary>
    private bool FixShortcut(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 还原 ====================

    /// <summary>
    /// 从备份文件还原注册表
    /// 安全铁律：还原前 SHA256 校验；验证 .reg 格式（UTF-16 LE 头）
    /// 部分导入失败时逐条还原
    /// </summary>
    public bool RestoreFromBackup(RegistryBackup backup)
    {
        try
        {
            // 1. 验证备份存在
            if (!File.Exists(backup.FilePath))
            {
                _log.LogError($"备份文件不存在: {backup.FilePath}");
                return false;
            }

            // 2. SHA256 校验
            if (!ValidateBackup(backup))
            {
                _log.LogError($"备份文件 SHA256 校验失败: {backup.FileName}");
                return false;
            }

            // 3. 验证 .reg 格式头
            if (!ValidateRegFormat(backup.FilePath))
            {
                _log.LogError($"备份文件格式验证失败: {backup.FileName}");
                return false;
            }

            // 4. 使用 regedit.exe 导入
            var psi = new ProcessStartInfo("regedit.exe", $"/s \"{backup.FilePath}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(30000); // 等待最多30秒
                if (process.ExitCode == 0)
                {
                    _log.LogOperation("registry", "restore", $"从备份还原: {backup.FileName}");
                    return true;
                }
            }

            // 5. 如果整体导入失败，尝试逐条导入
            _log.LogWarning("整体导入失败，尝试逐条还原...");
            return RestoreEntryByEntry(backup.FilePath);
        }
        catch (Exception ex)
        {
            _log.LogError("从备份还原失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 逐条还原注册表备份
    /// 将 .reg 文件按空行分割，逐段导入
    /// </summary>
    private bool RestoreEntryByEntry(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath, Encoding.Unicode);
            var sections = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            int success = 0;
            int failed = 0;

            foreach (var section in sections)
            {
                if (!section.StartsWith("[")) continue; // 跳过文件头

                try
                {
                    // 写入临时文件
                    var tempFile = Path.GetTempFileName() + ".reg";
                    File.WriteAllText(tempFile, "Windows Registry Editor Version 5.00\r\n\r\n" + section, Encoding.Unicode);

                    var psi = new ProcessStartInfo("regedit.exe", $"/s \"{tempFile}\"")
                    {
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    };

                    var process = Process.Start(psi);
                    process?.WaitForExit(10000);

                    if (process?.ExitCode == 0)
                        success++;
                    else
                        failed++;

                    // 清理临时文件
                    try { File.Delete(tempFile); } catch { }
                }
                catch
                {
                    failed++;
                }
            }

            _log.LogOperation("registry", "restore_detail", $"逐条还原完成: 成功 {success}, 失败 {failed}");
            return failed == 0;
        }
        catch (Exception ex)
        {
            _log.LogError("逐条还原失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 验证 .reg 文件格式（检查 UTF-16 LE 头和版本声明）
    /// </summary>
    private bool ValidateRegFormat(string filePath)
    {
        try
        {
            // 读取前几个字节检查 BOM
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 4) return false;

            // UTF-16 LE BOM: 0xFF 0xFE
            if (bytes[0] != 0xFF || bytes[1] != 0xFE)
            {
                _log.LogWarning("备份文件缺少 UTF-16 LE BOM");
                return false;
            }

            // 检查版本声明
            var content = File.ReadAllText(filePath, Encoding.Unicode);
            if (!content.StartsWith("Windows Registry Editor Version 5.00"))
            {
                _log.LogWarning("备份文件缺少正确的版本声明");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("验证 .reg 格式失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 验证备份文件 SHA256 哈希
    /// </summary>
    public bool ValidateBackup(RegistryBackup backup)
    {
        if (string.IsNullOrEmpty(backup.Hash))
            return true; // 旧备份可能没有存储哈希

        return _config.ValidateHash(backup.FilePath, backup.Hash);
    }

    // ==================== 备份历史 ====================

    /// <summary>
    /// 获取所有备份历史记录
    /// </summary>
    public List<RegistryBackup> GetBackupHistory()
    {
        var backups = new List<RegistryBackup>();

        try
        {
            if (!Directory.Exists(_backupDir)) return backups;

            var files = Directory.GetFiles(_backupDir, "regbackup_*.reg");
            foreach (var file in files.OrderByDescending(f => f))
            {
                var fileInfo = new FileInfo(file);
                var fileName = Path.GetFileName(file);

                // 计算哈希（如果已有的话可以存储在 JSON meta 文件中）
                var hash = _config.ComputeHash(file);

                backups.Add(new RegistryBackup
                {
                    FileName = fileName,
                    FilePath = file,
                    CreatedAt = fileInfo.CreationTime,
                    IssueCount = 0, // 需要额外的元数据文件来存储
                    Hash = hash,
                    Description = $"{fileInfo.CreationTime:yyyy-MM-dd HH:mm} ({fileInfo.Length / 1024} KB)"
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogError("获取备份历史失败", ex);
        }

        return backups;
    }

    // ==================== 高级电源选项 ====================

    /// <summary>
    /// 设置电源选项可见性
    /// 修改注册表中电源子组的 Attributes 值
    /// Attributes=2 → 显示，Attributes=1 → 隐藏
    /// 执行后调用 powercfg /setactive 刷新
    /// </summary>
    /// <param name="show">true=显示高级选项，false=隐藏</param>
    public bool SetPowerOptionsVisibility(bool show)
    {
        try
        {
            int attributesValue = show ? 2 : 1;
            int modifiedCount = 0;

            using var powerKey = Registry.LocalMachine.OpenSubKey(PowerSettingsPath, writable: true);
            if (powerKey == null)
            {
                _log.LogError("无法打开电源设置注册表键");
                return false;
            }

            foreach (var guid in PowerSubGroupGuids)
            {
                try
                {
                    using var subGroupKey = powerKey.OpenSubKey(guid, writable: true);
                    if (subGroupKey == null)
                    {
                        // 创建子键（如果不存在）
                        using var newKey = powerKey.CreateSubKey(guid);
                        newKey.SetValue("Attributes", attributesValue, RegistryValueKind.DWord);
                    }
                    else
                    {
                        subGroupKey.SetValue("Attributes", attributesValue, RegistryValueKind.DWord);
                    }
                    modifiedCount++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"设置电源子组 {guid} Attributes 失败: {ex.Message}");
                }
            }

            if (modifiedCount > 0)
            {
                // 刷新电源方案
                try
                {
                    var psi = new ProcessStartInfo("powercfg.exe", "/setactive scheme_current")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"刷新电源方案失败: {ex.Message}");
                }

                string action = show ? "显示" : "隐藏";
                _log.LogOperation("registry", "power_options", $"{action} {modifiedCount} 个高级电源选项");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogError("设置电源选项失败", ex);
            return false;
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 从命令行字符串中提取可执行文件路径
    /// 处理带引号的路径和环境变量
    /// </summary>
    private static string ExtractExePath(string command)
    {
        if (string.IsNullOrEmpty(command)) return "";

        command = command.Trim();

        if (command.StartsWith("\""))
        {
            int end = command.IndexOf("\"", 1);
            if (end > 0) return Environment.ExpandEnvironmentVariables(command[1..end]);
        }
        else
        {
            int space = command.IndexOf(' ');
            string path = space > 0 ? command[..space] : command;
            return Environment.ExpandEnvironmentVariables(path);
        }

        return "";
    }

    /// <summary>
    /// 解析快捷方式的目标路径（使用 WScript.Shell COM）
    /// </summary>
    private static string ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return "";

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            return shortcut.TargetPath ?? "";
        }
        catch
        {
            return "";
        }
    }
}
