using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using Toolbox.Native;

namespace Toolbox.Services;

// ==================== 枚举定义 ====================

/// <summary>
/// 关机操作类型
/// </summary>
public enum ShutdownAction
{
    /// <summary>关机</summary>
    Shutdown,
    /// <summary>重启</summary>
    Restart,
    /// <summary>睡眠</summary>
    Sleep,
    /// <summary>休眠</summary>
    Hibernate,
    /// <summary>锁屏</summary>
    Lock,
    /// <summary>注销</summary>
    Logout
}

/// <summary>
/// 定时模式：指定时间 或 倒计时
/// </summary>
public enum ShutdownMode
{
    /// <summary>在指定时间执行</summary>
    SpecificTime,
    /// <summary>倒计时后执行</summary>
    Countdown
}

/// <summary>
/// 任务状态
/// </summary>
public enum ShutdownTaskStatus
{
    /// <summary>等待执行</summary>
    Pendiente,
    /// <summary>正在执行</summary>
    Executing,
    /// <summary>已执行完成</summary>
    Executed,
    /// <summary>已取消</summary>
    Cancelled,
    /// <summary>程序崩溃后恢复，任务已过期</summary>
    Missed
}

/// <summary>
/// 关机任务模型 — 描述一个定时关机任务
/// 持久化到 %AppData%\工具箱\tasks.json
/// </summary>
public class ShutdownTask
{
    /// <summary>任务唯一标识（GUID）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>操作类型：关机/重启/睡眠/休眠/锁屏/注销</summary>
    public ShutdownAction Action { get; set; }

    /// <summary>定时模式：指定时间 或 倒计时</summary>
    public ShutdownMode Mode { get; set; }

    /// <summary>目标执行时间（仅 SpecificTime 模式）</summary>
    public DateTime? TargetTime { get; set; }

    /// <summary>倒计时时长（仅 Countdown 模式）</summary>
    public TimeSpan? Countdown { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>任务状态</summary>
    public ShutdownTaskStatus Status { get; set; } = ShutdownTaskStatus.Pendiente;

    /// <summary>任务显示名称</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>强制关闭应用（InitiateSystemShutdownEx 的 bForceAppsClosed）</summary>
    public bool ForceClose { get; set; } = true;

    /// <summary>关机原因代码（传递给 Windows 的 dwReason）</summary>
    public uint ReasonCode { get; set; } = 0x80000000; // SHTDN_REASON_FLAG_PLANNED

    /// <summary>用于显示的剩余时间描述</summary>
    public string RemainingDisplay => GetRemainingDisplay();

    /// <summary>
    /// 获取用于显示的剩余时间文本
    /// </summary>
    private string GetRemainingDisplay()
    {
        if (Status != ShutdownTaskStatus.Pendiente || Mode != ShutdownMode.Countdown || Countdown == null)
            return "";

        var elapsed = DateTime.Now - CreatedAt;
        var remaining = Countdown.Value - elapsed;
        if (remaining.TotalSeconds <= 0)
            return "即将执行...";

        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}天 {remaining.Hours}时 {remaining.Minutes}分 {remaining.Seconds}秒";
        if (remaining.TotalHours >= 1)
            return $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}

// ==================== 关机服务 ====================

/// <summary>
/// 关机服务接口 — 定时关机核心逻辑
/// </summary>
public interface IShutdownService
{
    /// <summary>系统是否支持睡眠</summary>
    bool CanSleep { get; }

    /// <summary>系统是否支持休眠</summary>
    bool CanHibernate { get; }

    /// <summary>检测系统电源能力（睡眠/休眠支持）</summary>
    void DetectPowerCapabilities();

    /// <summary>执行关机操作</summary>
    /// <param name="action">操作类型</param>
    /// <param name="delaySeconds">延迟秒数（0 表示立即执行）</param>
    /// <param name="reason">关机原因描述</param>
    /// <returns>true 表示执行成功</returns>
    bool ExecuteShutdown(ShutdownAction action, int delaySeconds, string reason);

    /// <summary>从文件加载所有任务</summary>
    List<ShutdownTask> LoadTasks();

    /// <summary>保存任务列表到文件</summary>
    void SaveTasks(List<ShutdownTask> tasks);

    /// <summary>取消系统中已排队的关机操作（针对 shutdown.exe）</summary>
    void CancelSystemShutdown();
}

/// <summary>
/// 关机服务实现 — P/Invoke Windows 电源和关机 API
/// 电源检测：CallNtPowerInformation + PowerGetActiveScheme（powrprof.dll）
/// 关机执行：InitiateSystemShutdownEx API，回退到 shutdown.exe
/// 任务持久化：%AppData%\工具箱\tasks.json，含崩溃恢复
/// </summary>
public class ShutdownService : IShutdownService
{
    private readonly ILogService _log;
    private readonly string _tasksFilePath;

    /// <summary>系统是否支持睡眠（S1/S2/S3 或 Modern Standby）</summary>
    public bool CanSleep { get; private set; }

    /// <summary>系统是否支持休眠（S4）</summary>
    public bool CanHibernate { get; private set; }

    public ShutdownService(ILogService logService)
    {
        _log = logService;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _tasksFilePath = Path.Combine(appData, "工具箱", "tasks.json");
        DetectPowerCapabilities();
    }

    // ==================== P/Invoke 声明 ====================

    /// <summary>获取系统电源能力结构体（Sleep=4, Hibernate=5 等）</summary>
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int InformationLevel,
        IntPtr lpInputBuffer,
        uint nInputBufferSize,
        out SYSTEM_POWER_CAPABILITIES lpOutputBuffer,
        uint nOutputBufferSize);

    /// <summary>获取当前活动的电源方案 GUID</summary>
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(
        IntPtr UserRootPowerKey,
        out IntPtr ActivePolicyGuid);

    /// <summary>释放电源方案句柄</summary>
    [DllImport("powrprof.dll")]
    private static extern uint LocalFree(IntPtr hMem);

    /// <summary>系统关机/重启 API（支持远程机器，本机传 null）</summary>
    /// <param name="lpMachineName">目标机器名，null 表示本机</param>
    /// <param name="lpMessage">关机提示消息</param>
    /// <param name="dwTimeout">延迟秒数，0 表示立即</param>
    /// <param name="bForceAppsClosed">是否强制关闭应用</param>
    /// <param name="bRebootAfterShutdown">关机后是否重启</param>
    /// <param name="dwReason">关机原因代码</param>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownEx(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        bool bForceAppsClosed,
        bool bRebootAfterShutdown,
        uint dwReason);

    /// <summary>设置系统挂起状态（睡眠/休眠）</summary>
    /// <param name="bHibernate">true=休眠，false=睡眠</param>
    /// <param name="bForceCritical">是否强制执行（即使有程序阻止）</param>
    /// <param name="bDisableWakeEvent">是否禁用唤醒事件</param>
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(
        bool bHibernate,
        bool bForceCritical,
        bool bDisableWakeEvent);

    /// <summary>锁定工作站（锁屏）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    /// <summary>注销当前用户</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    /// <summary>EWX_LOGOFF：注销</summary>
    private const uint EWX_LOGOFF = 0;

    /// <summary>EWX_FORCE：强制（不等待程序响应）</summary>
    private const uint EWX_FORCE = 4;

    // ==================== 电源能力结构体 ====================

    /// <summary>
    /// SYSTEM_POWER_CAPABILITIES 结构体 — 系统电源能力
    /// 通过 CallNtPowerInformation(4, ...) 获取
    /// 只定义我们关心的前 8 个字段，后续用 Padding 补齐
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_CAPABILITIES
    {
        /// <summary>是否有机箱电源按钮</summary>
        public byte PowerButtonPresent;
        /// <summary>是否有睡眠按钮</summary>
        public byte SleepButtonPresent;
        /// <summary>是否有合盖传感器（笔记本）</summary>
        public byte LidPresent;
        /// <summary>系统是否支持 S1 睡眠</summary>
        public byte SystemS1;
        /// <summary>系统是否支持 S2 睡眠</summary>
        public byte SystemS2;
        /// <summary>系统是否支持 S3 待机（传统睡眠）</summary>
        public byte SystemS3;
        /// <summary>系统是否支持 S4 休眠</summary>
        public byte SystemS4;
        /// <summary>系统是否支持 S5 软关机</summary>
        public byte SystemS5;
        // 后续字段省略（padding），我们不需要
    }

    // ==================== 电源检测 ====================

    /// <summary>
    /// 检测系统电源能力
    /// 使用 CallNtPowerInformation 获取 SystemPowerCapabilities 判断睡眠/休眠支持
    /// 使用 PowerGetActiveScheme 验证电源管理 API 可用性
    /// 现代 Windows（8+）的 Modern Standby 替代传统 S3 睡眠，仍报告为"支持睡眠"
    /// </summary>
    public void DetectPowerCapabilities()
    {
        try
        {
            // 1. 检查硬件睡眠/休眠能力 (InformationLevel=4 = SystemPowerCapabilities)
            var caps = default(SYSTEM_POWER_CAPABILITIES);
            var result = CallNtPowerInformation(
                4,                          // SystemPowerCapabilities
                IntPtr.Zero, 0,
                out caps,
                (uint)Marshal.SizeOf<SYSTEM_POWER_CAPABILITIES>());

            if (result == 0) // STATUS_SUCCESS
            {
                // 硬件报告支持 S1/S2/S3 任一即为支持睡眠
                bool hardwareSleep = caps.SystemS1 == 1 || caps.SystemS2 == 1 || caps.SystemS3 == 1;
                bool hardwareHibernate = caps.SystemS4 == 1;

                // Windows 8+ 可能使用 Modern Standby（S0 低功耗空闲）替代 S3
                // 即使硬件不报告 S1-S3，Modern Standby 设备仍支持睡眠
                var osVersion = Environment.OSVersion.Version;
                bool isModernWindows = osVersion.Major >= 10 || (osVersion.Major == 6 && osVersion.Minor >= 2);

                CanSleep = hardwareSleep || isModernWindows;
                CanHibernate = hardwareHibernate;

                _log.LogInfo($"电源检测: 睡眠支持={(hardwareSleep ? "S1/S2/S3硬件" : (isModernWindows ? "Modern Standby" : "不支持"))}, 休眠支持={(CanHibernate ? "S4硬件" : "不支持")}");
            }
            else
            {
                // CallNtPowerInformation 失败，使用备用策略
                _log.LogWarning($"CallNtPowerInformation 失败 (0x{result:X8})，使用备用检测");
                CanSleep = true;  // 多数Windows设备支持睡眠
                CanHibernate = false; // 保守估计不支持休眠
            }

            // 2. 验证 PowerGetActiveScheme 可用性
            var schemeResult = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeScheme);
            if (schemeResult == 0 && activeScheme != IntPtr.Zero)
            {
                LocalFree(activeScheme);
                _log.LogInfo("PowerGetActiveScheme API 可用");
            }
            else
            {
                _log.LogWarning($"PowerGetActiveScheme 失败 (0x{schemeResult:X8})，部分功能可能受限");
            }
        }
        catch (Exception ex)
        {
            _log.LogError("电源检测异常", ex);
            // 异常时使用保守默认值
            CanSleep = true;
            CanHibernate = false;
        }
    }

    // ==================== 关机执行 ====================

    /// <summary>
    /// 执行关机/重启/睡眠/休眠/锁屏/注销操作
    /// 关机/重启：优先使用 InitiateSystemShutdownEx API，失败回退 shutdown.exe
    /// 睡眠/休眠：使用 SetSuspendState API
    /// 锁屏：使用 LockWorkStation API
    /// 注销：使用 ExitWindowsEx API
    /// </summary>
    /// <param name="action">操作类型</param>
    /// <param name="delaySeconds">延迟秒数（0 表示立即执行，睡眠/锁屏等操作忽略此参数）</param>
    /// <param name="reason">关机原因描述</param>
    /// <returns>true 表示操作成功发起</returns>
    public bool ExecuteShutdown(ShutdownAction action, int delaySeconds, string reason)
    {
        _log.LogOperation("shutdown", action.ToString(), $"延迟={delaySeconds}秒, 原因={reason}");

        try
        {
            switch (action)
            {
                case ShutdownAction.Shutdown:
                    return ExecuteShutdownOrRestart(delaySeconds, reboot: false, reason);
                case ShutdownAction.Restart:
                    return ExecuteShutdownOrRestart(delaySeconds, reboot: true, reason);
                case ShutdownAction.Sleep:
                    return ExecuteSleep();
                case ShutdownAction.Hibernate:
                    return ExecuteHibernate();
                case ShutdownAction.Lock:
                    return ExecuteLock();
                case ShutdownAction.Logout:
                    return ExecuteLogout();
                default:
                    _log.LogError($"不支持的关机操作类型: {action}");
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError("执行关机操作异常", ex);
            return false;
        }
    }

    /// <summary>
    /// 执行关机或重启
    /// 优先使用 InitiateSystemShutdownEx API（需要 SE_SHUTDOWN_NAME 权限）
    /// 失败时回退到 shutdown.exe 命令行
    /// </summary>
    private bool ExecuteShutdownOrRestart(int delaySeconds, bool reboot, string reason)
    {
        try
        {
            // 方式1：InitiateSystemShutdownEx API
            bool apiResult = InitiateSystemShutdownEx(
                null,                           // 本机
                $"工具箱-{(reboot ? "重启" : "关机")}: {reason}",
                (uint)Math.Max(delaySeconds, 0),
                true,                           // 强制关闭应用
                reboot,                         // 是否重启
                0x80000000                      // SHTDN_REASON_FLAG_PLANNED
            );

            if (apiResult)
            {
                _log.LogInfo($"API {(reboot ? "重启" : "关机")}成功, 延迟={delaySeconds}秒");
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            _log.LogWarning($"InitiateSystemShutdownEx 失败 (错误码={error})，回退到 shutdown.exe");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"InitiateSystemShutdownEx 异常: {ex.Message}，回退到 shutdown.exe");
        }

        // 方式2：shutdown.exe 备用
        try
        {
            string args = reboot
                ? $"/r /t {delaySeconds} /f /c \"工具箱-重启: {reason}\""
                : $"/s /t {delaySeconds} /f /c \"工具箱-关机: {reason}\"";

            var psi = new ProcessStartInfo("shutdown.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            _log.LogInfo($"shutdown.exe {(reboot ? "重启" : "关机")}命令已执行, 延迟={delaySeconds}秒");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("shutdown.exe 也执行失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 执行睡眠 — 使用 SetSuspendState API
    /// </summary>
    private bool ExecuteSleep()
    {
        if (!CanSleep)
        {
            _log.LogWarning("系统不支持睡眠功能");
            return false;
        }

        bool result = SetSuspendState(false, true, false);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            _log.LogError($"SetSuspendState(睡眠) 失败, 错误码={error}");

            // 回退：使用系统命令
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _log.LogInfo("rundll32 睡眠命令已执行");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("rundll32 睡眠也失败", ex);
                return false;
            }
        }
        _log.LogInfo("睡眠命令已执行");
        return true;
    }

    /// <summary>
    /// 执行休眠 — 使用 SetSuspendState API
    /// </summary>
    private bool ExecuteHibernate()
    {
        if (!CanHibernate)
        {
            _log.LogWarning("系统不支持休眠功能");
            return false;
        }

        bool result = SetSuspendState(true, true, false);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            _log.LogError($"SetSuspendState(休眠) 失败, 错误码={error}");

            // 回退：使用系统命令
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/h /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _log.LogInfo("shutdown.exe /h 休眠命令已执行");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("shutdown /h 休眠也失败", ex);
                return false;
            }
        }
        _log.LogInfo("休眠命令已执行");
        return true;
    }

    /// <summary>
    /// 执行锁屏 — 使用 LockWorkStation API
    /// </summary>
    private bool ExecuteLock()
    {
        bool result = LockWorkStation();
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            _log.LogError($"LockWorkStation 失败, 错误码={error}");
            return false;
        }
        _log.LogInfo("锁屏命令已执行");
        return true;
    }

    /// <summary>
    /// 执行注销 — 使用 ExitWindowsEx API
    /// </summary>
    private bool ExecuteLogout()
    {
        bool result = ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, 0);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            _log.LogError($"ExitWindowsEx 注销失败, 错误码={error}");
            return false;
        }
        _log.LogInfo("注销命令已执行");
        return true;
    }

    /// <summary>
    /// 取消所有已排队的关机操作（针对 shutdown.exe 发起的）
    /// </summary>
    public void CancelSystemShutdown()
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe", "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            _log.LogInfo("已取消系统关机计划");
        }
        catch (Exception ex)
        {
            _log.LogError("取消系统关机失败", ex);
        }
    }

    // ==================== 任务持久化 ====================

    /// <summary>
    /// 从文件加载任务列表
    /// 启动时自动执行崩溃恢复：将过期未执行的 Pendiente 任务标记为 Missed
    /// </summary>
    public List<ShutdownTask> LoadTasks()
    {
        try
        {
            if (!File.Exists(_tasksFilePath))
                return new List<ShutdownTask>();

            var json = File.ReadAllText(_tasksFilePath);
            var tasks = JsonSerializer.Deserialize<List<ShutdownTask>>(json) ?? new List<ShutdownTask>();

            // 崩溃恢复：检查并修复 Pendiente 状态但已过期的任务
            foreach (var task in tasks.Where(t => t.Status == ShutdownTaskStatus.Pendiente))
            {
                bool isExpired = task.Mode == ShutdownMode.SpecificTime
                    ? (task.TargetTime.HasValue && DateTime.Now > task.TargetTime.Value.AddMinutes(1))
                    : (task.Countdown.HasValue && DateTime.Now > task.CreatedAt + task.Countdown.Value.Add(TimeSpan.FromMinutes(1)));

                if (isExpired)
                {
                    task.Status = ShutdownTaskStatus.Missed;
                    _log.LogWarning($"崩溃恢复: 任务 {task.Id} ({task.DisplayName}) 已过期，标记为 Missed");
                }
            }

            _log.LogInfo($"加载了 {tasks.Count} 个关机任务");
            return tasks;
        }
        catch (Exception ex)
        {
            _log.LogError("加载关机任务文件失败", ex);
            return new List<ShutdownTask>();
        }
    }

    /// <summary>
    /// 保存任务列表到文件
    /// </summary>
    public void SaveTasks(List<ShutdownTask> tasks)
    {
        try
        {
            var dir = Path.GetDirectoryName(_tasksFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(tasks, options);
            File.WriteAllText(_tasksFilePath, json);
        }
        catch (Exception ex)
        {
            _log.LogError("保存关机任务文件失败", ex);
        }
    }
}
