using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Toolbox.Services;

// ==================== 枚举和模型 ====================

/// <summary>
/// 启动项来源类型
/// </summary>
public enum StartupSource
{
    /// <summary>注册表 HKLM\Run</summary>
    RegistryHKLM,
    /// <summary>注册表 HKCU\Run</summary>
    RegistryHKCU,
    /// <summary>注册表 HKLM\WOW6432Node\Run（64位系统上的32位启动项）</summary>
    RegistryHKLMWow64,
    /// <summary>计划任务</summary>
    TaskScheduler,
    /// <summary>用户启动文件夹（shell:startup）</summary>
    StartupFolderUser,
    /// <summary>公用启动文件夹（shell:common startup）</summary>
    StartupFolderCommon
}

/// <summary>
/// 影响程度枚举
/// </summary>
public enum ImpactLevel
{
    /// <summary>低影响 - AppData路径且非系统程序</summary>
    Low,
    /// <summary>中影响 - Program Files路径且有有效数字签名</summary>
    Medium,
    /// <summary>高影响 - 系统目录或无签名</summary>
    High,
    /// <summary>不可用 - 键不可读或文件已删除</summary>
    Unavailable,
    /// <summary>文件丢失 - 启动项指向的文件不存在</summary>
    FileMissing
}

/// <summary>
/// 启动项模型 — 描述一个开机自启动项
/// </summary>
public class StartupEntry
{
    /// <summary>启动项名称</summary>
    public string Name { get; set; } = "";

    /// <summary>发布者/公司名称（从数字签名或文件信息获取）</summary>
    public string Publisher { get; set; } = "未知";

    /// <summary>可执行文件或命令的完整路径</summary>
    public string FilePath { get; set; } = "";

    /// <summary>命令行参数</summary>
    public string Arguments { get; set; } = "";

    /// <summary>启动项来源</summary>
    public StartupSource Source { get; set; }

    /// <summary>来源的显示名称（如"HKLM注册表"、"用户启动文件夹"）</summary>
    public string SourceDisplay => Source switch
    {
        StartupSource.RegistryHKLM => "HKLM注册表",
        StartupSource.RegistryHKCU => "HKCU注册表",
        StartupSource.RegistryHKLMWow64 => "HKLM注册表(32位)",
        StartupSource.TaskScheduler => "计划任务",
        StartupSource.StartupFolderUser => "用户启动文件夹",
        StartupSource.StartupFolderCommon => "公用启动文件夹",
        _ => Source.ToString()
    };

    /// <summary>影响程度</summary>
    public ImpactLevel Impact { get; set; } = ImpactLevel.Medium;

    /// <summary>影响程度显示文字</summary>
    public string ImpactDisplay => Impact switch
    {
        ImpactLevel.Low => "低",
        ImpactLevel.Medium => "中",
        ImpactLevel.High => "高",
        ImpactLevel.Unavailable => "不可用",
        ImpactLevel.FileMissing => "文件丢失",
        _ => Impact.ToString()
    };

    /// <summary>是否已启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>注册表键的完整路径（RegistrySource 时使用）</summary>
    public string? RegistryKeyPath { get; set; }

    /// <summary>注册表值名称（RegistrySource 时使用）</summary>
    public string? RegistryValueName { get; set; }

    /// <summary>计划任务路径（TaskScheduler 时使用）</summary>
    public string? TaskPath { get; set; }

    /// <summary>快捷方式文件路径（StartupFolder 时使用）</summary>
    public string? ShortcutPath { get; set; }
}

// ==================== 启动项服务 ====================

/// <summary>
/// 启动项服务接口
/// </summary>
public interface IStartupService
{
    /// <summary>扫描所有启动项</summary>
    /// <param name="progress">进度报告（可选）</param>
    /// <param name="cancellationToken">取消令牌（可选）</param>
    /// <returns>启动项列表</returns>
    List<StartupEntry> ScanAllStartupItems(IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>启用指定启动项</summary>
    bool EnableItem(StartupEntry entry);

    /// <summary>禁用指定启动项</summary>
    bool DisableItem(StartupEntry entry);

    /// <summary>删除指定启动项</summary>
    bool DeleteItem(StartupEntry entry);
}

/// <summary>
/// 启动项服务实现
/// 扫描来源：注册表 HKLM/HKCU Run 键、Task Scheduler 启动任务、
///           shell:startup 和 shell:common startup 文件夹
/// 数字签名验证：X509Certificate2.CreateFromSignedFile()
/// 影响程度自动判定：🟢低/🟡中/🔴高/🟠文件丢失
/// </summary>
public class StartupService : IStartupService
{
    private readonly ILogService _log;

    public StartupService(ILogService logService)
    {
        _log = logService;
    }

    /// <summary>
    /// 扫描所有来源的启动项
    /// 包括：注册表 Run 键（HKLM/HKCU/WOW64）、计划任务、启动文件夹
    /// </summary>
    public List<StartupEntry> ScanAllStartupItems(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<StartupEntry>();
        int totalSteps = 6;
        int currentStep = 0;

        try
        {
            // 1. 扫描 HKLM\Run
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(ScanRegistryRun(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                StartupSource.RegistryHKLM));
            progress?.Report((++currentStep * 100) / totalSteps);

            // 2. 扫描 HKLM\WOW6432Node\Run（64位系统上的32位启动项）
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(ScanRegistryRun(Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                StartupSource.RegistryHKLMWow64));
            progress?.Report((++currentStep * 100) / totalSteps);

            // 3. 扫描 HKCU\Run
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(ScanRegistryRun(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                StartupSource.RegistryHKCU));
            progress?.Report((++currentStep * 100) / totalSteps);

            // 4. 扫描 Task Scheduler 启动任务
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(ScanTaskScheduler());
            progress?.Report((++currentStep * 100) / totalSteps);

            // 5. 扫描用户启动文件夹
            cancellationToken.ThrowIfCancellationRequested();
            var userStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            items.AddRange(ScanStartupFolder(userStartup, StartupSource.StartupFolderUser));
            progress?.Report((++currentStep * 100) / totalSteps);

            // 6. 扫描公用启动文件夹
            cancellationToken.ThrowIfCancellationRequested();
            var commonStartup = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\Startup");
            items.AddRange(ScanStartupFolder(commonStartup, StartupSource.StartupFolderCommon));
            progress?.Report(100);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("启动项扫描已取消");
        }
        catch (Exception ex)
        {
            _log.LogError("扫描启动项异常", ex);
        }

        // 去重（按名称+路径组合）
        var distinct = items
            .GroupBy(i => $"{i.Name}|{i.FilePath}|{i.Source}")
            .Select(g => g.First())
            .ToList();

        _log.LogInfo($"启动项扫描完成: 共 {distinct.Count} 项");
        return distinct;
    }

    // ==================== 注册表扫描 ====================

    /// <summary>
    /// 扫描指定注册表 Run 键下的启动项
    /// 读取键下的所有值名称和值数据
    /// 键不可读时标记为"不可用"并跳过
    /// </summary>
    private List<StartupEntry> ScanRegistryRun(RegistryKey hive, string subKey, StartupSource source)
    {
        var items = new List<StartupEntry>();
        string hiveName = hive == Registry.LocalMachine ? "HKLM" : "HKCU";

        try
        {
            using var key = hive.OpenSubKey(subKey, writable: false);
            if (key == null)
            {
                _log.LogWarning($"注册表键不存在或不可读: {hiveName}\\{subKey}");
                return items;
            }

            foreach (var valueName in key.GetValueNames())
            {
                try
                {
                    var rawValue = key.GetValue(valueName);
                    if (rawValue == null) continue;

                    string commandLine = rawValue.ToString() ?? "";

                    // 解析可执行文件路径和参数
                    ParseCommandLine(commandLine, out string exePath, out string args);

                    var entry = new StartupEntry
                    {
                        Name = valueName,
                        FilePath = exePath,
                        Arguments = args,
                        Source = source,
                        IsEnabled = true,
                        RegistryKeyPath = $"{hiveName}\\{subKey}",
                        RegistryValueName = valueName
                    };

                    // 检测文件信息、发布者、影响程度
                    AnalyzeEntry(entry);
                    items.Add(entry);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"读取注册表值失败: {hiveName}\\{subKey}\\{valueName}: {ex.Message}");
                    // 键不可读时标记为不可用
                    items.Add(new StartupEntry
                    {
                        Name = valueName,
                        FilePath = $"{hiveName}\\{subKey}\\{valueName}",
                        Source = source,
                        IsEnabled = false,
                        Impact = ImpactLevel.Unavailable,
                        Publisher = "不可用",
                        RegistryKeyPath = $"{hiveName}\\{subKey}",
                        RegistryValueName = valueName
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"扫描注册表键失败: {hiveName}\\{subKey}: {ex.Message}");
        }

        return items;
    }

    // ==================== Task Scheduler 扫描 ====================

    /// <summary>
    /// 扫描 Task Scheduler 中在用户登录时触发的任务
    /// 使用 Microsoft.Win32.TaskScheduler 库
    /// </summary>
    private List<StartupEntry> ScanTaskScheduler()
    {
        var items = new List<StartupEntry>();

        try
        {
            using var ts = new TaskService();

            // 枚举所有任务文件夹
            EnumerateTaskFolder(ts.RootFolder, items);

            _log.LogInfo($"计划任务扫描完成: {items.Count} 个登录触发任务");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"扫描计划任务失败: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// 递归枚举任务文件夹中在登录时触发的任务
    /// </summary>
    private void EnumerateTaskFolder(TaskFolder folder, List<StartupEntry> items)
    {
        try
        {
            // 检查当前文件夹中的任务
            foreach (var task in folder.Tasks)
            {
                try
                {
                    // 检查触发器是否包含登录触发器
                    bool hasLogonTrigger = task.Definition.Triggers.Any(t =>
                        t.TriggerType == TaskTriggerType.Logon ||
                        t.TriggerType == TaskTriggerType.Boot ||
                        t.TriggerType == TaskTriggerType.SessionStateChange);

                    if (!hasLogonTrigger) continue;

                    // 获取可执行文件路径
                    string exePath = "";
                    string args = "";
                    if (task.Definition.Actions.FirstOrDefault() is ExecAction execAction)
                    {
                        exePath = execAction.Path;
                        args = execAction.Arguments ?? "";
                    }

                    var entry = new StartupEntry
                    {
                        Name = task.Name,
                        FilePath = exePath,
                        Arguments = args,
                        Source = StartupSource.TaskScheduler,
                        IsEnabled = task.Enabled,
                        Publisher = task.Definition.Principal.UserId ?? "未知",
                        TaskPath = task.Path
                    };

                    AnalyzeEntry(entry);
                    items.Add(entry);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"读取计划任务失败: {task.Name}: {ex.Message}");

                    items.Add(new StartupEntry
                    {
                        Name = task.Name,
                        FilePath = "",
                        Source = StartupSource.TaskScheduler,
                        IsEnabled = false,
                        Impact = ImpactLevel.Unavailable,
                        Publisher = "不可用",
                        TaskPath = task.Path
                    });
                }
            }

            // 递归子文件夹
            foreach (var subFolder in folder.SubFolders)
            {
                EnumerateTaskFolder(subFolder, items);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"枚举任务文件夹失败: {folder.Path}: {ex.Message}");
        }
    }

    // ==================== 启动文件夹扫描 ====================

    /// <summary>
    /// 扫描指定启动文件夹中的 .lnk 快捷方式和可执行文件
    /// </summary>
    private List<StartupEntry> ScanStartupFolder(string folderPath, StartupSource source)
    {
        var items = new List<StartupEntry>();

        if (!Directory.Exists(folderPath))
        {
            _log.LogWarning($"启动文件夹不存在: {folderPath}");
            return items;
        }

        try
        {
            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                try
                {
                    string exePath = file;
                    string args = "";
                    string fileName = Path.GetFileNameWithoutExtension(file);

                    // 如果是 .lnk 快捷方式，解析目标路径
                    if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var target = ResolveShortcut(file);
                        if (!string.IsNullOrEmpty(target))
                        {
                            ParseCommandLine(target, out exePath, out args);
                        }
                    }

                    var entry = new StartupEntry
                    {
                        Name = fileName,
                        FilePath = exePath,
                        Arguments = args,
                        Source = source,
                        IsEnabled = true,
                        ShortcutPath = file
                    };

                    AnalyzeEntry(entry);
                    items.Add(entry);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"读取启动文件夹文件失败: {file}: {ex.Message}");

                    items.Add(new StartupEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        FilePath = file,
                        Source = source,
                        IsEnabled = false,
                        Impact = ImpactLevel.Unavailable,
                        Publisher = "不可用",
                        ShortcutPath = file
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"扫描启动文件夹失败: {folderPath}: {ex.Message}");
        }

        return items;
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 解析命令行字符串，分离可执行文件路径和参数
    /// 处理带引号的路径
    /// </summary>
    private void ParseCommandLine(string commandLine, out string exePath, out string args)
    {
        exePath = "";
        args = "";

        commandLine = commandLine.Trim();
        if (string.IsNullOrEmpty(commandLine)) return;

        if (commandLine.StartsWith("\""))
        {
            int endQuote = commandLine.IndexOf("\"", 1);
            if (endQuote > 0)
            {
                exePath = commandLine.Substring(1, endQuote - 1);
                args = commandLine.Substring(endQuote + 1).Trim();
            }
            else
            {
                exePath = commandLine;
            }
        }
        else
        {
            int space = commandLine.IndexOf(' ');
            if (space > 0)
            {
                exePath = commandLine.Substring(0, space);
                args = commandLine.Substring(space + 1).Trim();
            }
            else
            {
                exePath = commandLine;
            }
        }

        // 展开环境变量
        exePath = Environment.ExpandEnvironmentVariables(exePath);
    }

    /// <summary>
    /// 解析 .lnk 快捷方式文件的目标路径
    /// 使用 WScript.Shell COM 对象
    /// </summary>
    private string ResolveShortcut(string shortcutPath)
    {
        try
        {
            // 使用 WScript.Shell COM 对象解析快捷方式
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return "";

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string target = shortcut.TargetPath ?? "";
            string args = shortcut.Arguments ?? "";

            if (!string.IsNullOrEmpty(args))
                target = $"\"{target}\" {args}";

            return target;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 分析启动项：检测文件信息、发布者、数字签名、影响程度
    /// 影响程度判定规则：
    ///   🟢低 = AppData路径且非系统程序
    ///   🟡中 = Program Files路径且有有效数字签名
    ///   🔴高 = 系统目录或无签名
    ///   🟠文件丢失 = 启动项指向的文件不存在
    /// </summary>
    private void AnalyzeEntry(StartupEntry entry)
    {
        if (string.IsNullOrEmpty(entry.FilePath))
        {
            entry.Impact = ImpactLevel.Unavailable;
            entry.Publisher = "未知";
            return;
        }

        // 提取实际的可执行文件（不含参数）
        string exePath = entry.FilePath;

        // 检查文件是否存在
        if (!File.Exists(exePath))
        {
            entry.Impact = ImpactLevel.FileMissing;
            entry.Publisher = "文件丢失";
            return;
        }

        try
        {
            // 获取文件版本信息（发布者）
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrEmpty(versionInfo.CompanyName))
                entry.Publisher = versionInfo.CompanyName;
            else if (!string.IsNullOrEmpty(versionInfo.FileDescription))
                entry.Publisher = versionInfo.FileDescription;
            else
                entry.Publisher = "未知";

            // 检查数字签名
            bool hasValidSignature = false;
            try
            {
                var cert = X509Certificate2.CreateFromSignedFile(exePath);
                if (cert != null)
                {
                    // 验证证书链
                    using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                    chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                    hasValidSignature = chain.Build(new X509Certificate2(cert));
                }
            }
            catch
            {
                hasValidSignature = false;
            }

            // 判断影响程度
            var dir = Path.GetDirectoryName(exePath) ?? "";
            var normalizedDir = dir.Replace("/", "\\").TrimEnd('\\') + "\\";

            bool isAppData = normalizedDir.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).Replace("/", "\\").TrimEnd('\\') + "\\",
                StringComparison.OrdinalIgnoreCase) ||
                normalizedDir.StartsWith(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("/", "\\").TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase);

            bool isProgramFiles = normalizedDir.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase) ||
                                  normalizedDir.StartsWith(@"C:\Program Files (x86)\", StringComparison.OrdinalIgnoreCase);

            bool isSystemDir = normalizedDir.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) ||
                               normalizedDir.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase);

            if (isAppData)
                entry.Impact = ImpactLevel.Low;
            else if (isProgramFiles && hasValidSignature)
                entry.Impact = ImpactLevel.Medium;
            else if (isSystemDir || !hasValidSignature)
                entry.Impact = ImpactLevel.High;
            else
                entry.Impact = ImpactLevel.Medium;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"分析启动项失败: {entry.Name}: {ex.Message}");
            entry.Impact = ImpactLevel.Unavailable;
            entry.Publisher = "分析失败";
        }
    }

    // ==================== 启用/禁用 ====================

    /// <summary>
    /// 启用指定启动项
    /// 根据来源类型采取不同的启用策略
    /// </summary>
    public bool EnableItem(StartupEntry entry)
    {
        try
        {
            switch (entry.Source)
            {
                case StartupSource.RegistryHKLM:
                case StartupSource.RegistryHKCU:
                case StartupSource.RegistryHKLMWow64:
                    return SetRegistryItemEnabled(entry, enabled: true);

                case StartupSource.TaskScheduler:
                    return SetTaskEnabled(entry, enabled: true);

                case StartupSource.StartupFolderUser:
                case StartupSource.StartupFolderCommon:
                    if (!string.IsNullOrEmpty(entry.ShortcutPath))
                    {
                        var disabledPath = entry.ShortcutPath + ".disabled";
                        if (File.Exists(disabledPath) && !File.Exists(entry.ShortcutPath))
                        {
                            File.Move(disabledPath, entry.ShortcutPath);
                            _log.LogOperation("startup", "enable", $"恢复启动文件: {disabledPath} -> {entry.ShortcutPath}");
                        }
                    }
                    entry.IsEnabled = true;
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"启用启动项失败: {entry.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 禁用指定启动项
    /// 根据来源类型采取不同的禁用策略
    /// 注册表项：备份值然后删除或重命名，计划任务：禁用任务
    /// </summary>
    public bool DisableItem(StartupEntry entry)
    {
        try
        {
            switch (entry.Source)
            {
                case StartupSource.RegistryHKLM:
                case StartupSource.RegistryHKCU:
                case StartupSource.RegistryHKLMWow64:
                    return SetRegistryItemEnabled(entry, enabled: false);

                case StartupSource.TaskScheduler:
                    return SetTaskEnabled(entry, enabled: false);

                case StartupSource.StartupFolderUser:
                case StartupSource.StartupFolderCommon:
                    if (!string.IsNullOrEmpty(entry.ShortcutPath) && File.Exists(entry.ShortcutPath))
                    {
                        var disabledPath = entry.ShortcutPath + ".disabled";
                        if (File.Exists(disabledPath))
                            File.Delete(disabledPath);

                        File.Move(entry.ShortcutPath, disabledPath);
                        _log.LogOperation("startup", "disable", $"移动启动文件: {entry.ShortcutPath} -> {disabledPath}");
                    }
                    entry.IsEnabled = false;
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"禁用启动项失败: {entry.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 通过修改注册表值名称来启用/禁用（添加/移除"_disabled"后缀）
    /// </summary>
    private bool SetRegistryItemEnabled(StartupEntry entry, bool enabled)
    {
        if (string.IsNullOrEmpty(entry.RegistryKeyPath) || string.IsNullOrEmpty(entry.RegistryValueName))
            return false;

        try
        {
            // 解析注册表路径
            var parts = entry.RegistryKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;

            var hive = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            var subKey = parts[1];

            RegistryView view = entry.Source == StartupSource.RegistryHKLMWow64 ? RegistryView.Registry32 : RegistryView.Default;

            using var key = RegistryKey.OpenBaseKey(
                hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                view).OpenSubKey(subKey, writable: true);

            if (key == null) return false;

            if (enabled)
            {
                // 启用：从 _disabled 恢复
                string disabledName = entry.RegistryValueName + "_disabled_by_toolbox";
                var disabledValue = key.GetValue(disabledName);
                if (disabledValue != null)
                {
                    key.SetValue(entry.RegistryValueName, disabledValue);
                    key.DeleteValue(disabledName);
                }
            }
            else
            {
                // 禁用：备份原值到 _disabled，删除原值
                var originalValue = key.GetValue(entry.RegistryValueName);
                if (originalValue != null)
                {
                    string disabledName = entry.RegistryValueName + "_disabled_by_toolbox";
                    key.SetValue(disabledName, originalValue);
                    key.DeleteValue(entry.RegistryValueName);
                }
            }

            entry.IsEnabled = enabled;
            _log.LogOperation("startup", enabled ? "enable" : "disable",
                $"{entry.Name} ({entry.SourceDisplay})");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"修改注册表启动项失败: {entry.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 设置计划任务的启用/禁用状态
    /// </summary>
    private bool SetTaskEnabled(StartupEntry entry, bool enabled)
    {
        if (string.IsNullOrEmpty(entry.TaskPath)) return false;

        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(entry.TaskPath);
            if (task != null)
            {
                task.Enabled = enabled;
                entry.IsEnabled = enabled;
                _log.LogOperation("startup", enabled ? "enable" : "disable",
                    $"计划任务: {entry.Name}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError($"修改计划任务失败: {entry.Name}", ex);
            return false;
        }
    }

    // ==================== 删除 ====================

    /// <summary>
    /// 删除指定启动项
    /// 注册表项：删除值，计划任务：删除任务，启动文件夹：删除文件
    /// </summary>
    public bool DeleteItem(StartupEntry entry)
    {
        try
        {
            switch (entry.Source)
            {
                case StartupSource.RegistryHKLM:
                case StartupSource.RegistryHKCU:
                case StartupSource.RegistryHKLMWow64:
                    return DeleteRegistryItem(entry);

                case StartupSource.TaskScheduler:
                    return DeleteTaskItem(entry);

                case StartupSource.StartupFolderUser:
                case StartupSource.StartupFolderCommon:
                    return DeleteStartupFile(entry);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"删除启动项失败: {entry.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 从注册表中删除启动项值
    /// </summary>
    private bool DeleteRegistryItem(StartupEntry entry)
    {
        if (string.IsNullOrEmpty(entry.RegistryKeyPath) || string.IsNullOrEmpty(entry.RegistryValueName))
            return false;

        try
        {
            var parts = entry.RegistryKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;

            var hive = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            var subKey = parts[1];

            RegistryView view = entry.Source == StartupSource.RegistryHKLMWow64 ? RegistryView.Registry32 : RegistryView.Default;

            using var key = RegistryKey.OpenBaseKey(
                hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                view).OpenSubKey(subKey, writable: true);

            if (key == null) return false;

            // 删除主值和备份值
            try { key.DeleteValue(entry.RegistryValueName); } catch { }
            try { key.DeleteValue(entry.RegistryValueName + "_disabled_by_toolbox"); } catch { }

            _log.LogOperation("startup", "delete", $"注册表: {entry.Name} ({entry.SourceDisplay})");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"删除注册表启动项失败: {entry.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 从 Task Scheduler 删除启动任务
    /// </summary>
    private bool DeleteTaskItem(StartupEntry entry)
    {
        if (string.IsNullOrEmpty(entry.TaskPath)) return false;

        try
        {
            using var ts = new TaskService();
            ts.RootFolder.DeleteTask(entry.TaskPath, exceptionOnNotExists: false);
            _log.LogOperation("startup", "delete", $"计划任务: {entry.Name}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"删除计划任务失败: {entry.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 从启动文件夹删除文件
    /// </summary>
    private bool DeleteStartupFile(StartupEntry entry)
    {
        var pathsToDelete = new List<string>();

        if (!string.IsNullOrEmpty(entry.ShortcutPath) && File.Exists(entry.ShortcutPath))
            pathsToDelete.Add(entry.ShortcutPath);
        if (File.Exists(entry.ShortcutPath + ".disabled"))
            pathsToDelete.Add(entry.ShortcutPath + ".disabled");

        foreach (var path in pathsToDelete)
        {
            try
            {
                File.Delete(path);
                _log.LogOperation("startup", "delete", $"启动文件: {path}");
            }
            catch (Exception ex)
            {
                _log.LogError($"删除启动文件失败: {path}", ex);
                return false;
            }
        }

        return pathsToDelete.Count > 0;
    }
}
