using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Services;
using ShutdownMode = Toolbox.Services.ShutdownMode;

namespace Toolbox.ViewModels;

/// <summary>
/// 定时关机 ViewModel — 管理关机任务的创建、监控和执行
/// 功能：指定时间/倒计时两种模式、关机/重启/睡眠/休眠/锁屏/注销六种操作、
///        5分钟+1分钟提醒、Stopwatch + TickCount64 双保险计时、
///        不支持模式灰色标注、崩溃恢复
/// </summary>
public partial class ScheduledShutdownViewModel : ViewModelBase
{
    private readonly IShutdownService _shutdown;
    private readonly ILogService _log;
    private readonly IConfigService _config;

    // ==================== 计时相关字段 ====================

    /// <summary>高精度计时器（Stopwatch 主计时）</summary>
    private readonly Stopwatch _stopwatch = new();

    /// <summary>TickCount64 备用计时起点</summary>
    private long _tickCountStart;

    /// <summary>定时器刷新间隔（毫秒）</summary>
    private System.Timers.Timer? _timer;

    /// <summary>倒计时总秒数（Countdown 模式用）</summary>
    private double _totalCountdownSeconds;

    /// <summary>上次提醒的分钟数（防止重复提醒）</summary>
    private int _lastReminderMinute = -1;

    /// <summary>任务执行锁，防止重复执行</summary>
    private bool _isExecuting;

    // ==================== 可观察属性 ====================

    /// <summary>当前模式：指定时间（true）或倒计时（false）</summary>
    [ObservableProperty]
    private bool _isSpecificTimeMode = true;

    /// <summary>指定的目标日期</summary>
    [ObservableProperty]
    private DateTime _targetDate = DateTime.Today;

    /// <summary>指定的目标时间（小时）</summary>
    [ObservableProperty]
    private int _targetHour = DateTime.Now.Hour + 1;

    /// <summary>指定的目标时间（分钟）</summary>
    [ObservableProperty]
    private int _targetMinute = 0;

    /// <summary>倒计时小时数</summary>
    [ObservableProperty]
    private int _countdownHours;

    /// <summary>倒计时分钟数</summary>
    [ObservableProperty]
    private int _countdownMinutes = 30;

    /// <summary>倒计时秒数</summary>
    [ObservableProperty]
    private int _countdownSeconds;

    /// <summary>当前选中的操作类型</summary>
    [ObservableProperty]
    private ShutdownAction _selectedAction = ShutdownAction.Shutdown;

    /// <summary>选中的操作类型索引（ComboBox 绑定用）</summary>
    [ObservableProperty]
    private int _selectedActionIndex;

    /// <summary>所有可选的操作类型列表</summary>
    public List<ShutdownActionItem> ActionItems { get; } = new();

    /// <summary>任务列表</summary>
    [ObservableProperty]
    private ObservableCollection<ShutdownTask> _tasks = new();

    /// <summary>当前活跃任务（第一个 Pendiente 状态的任务）</summary>
    [ObservableProperty]
    private ShutdownTask? _activeTask;

    /// <summary>是否有活跃任务</summary>
    [ObservableProperty]
    private bool _hasActiveTask;

    /// <summary>剩余时间显示文本</summary>
    [ObservableProperty]
    private string _remainingTimeDisplay = "--:--:--";

    /// <summary>是否支持睡眠</summary>
    [ObservableProperty]
    private bool _canSleep;

    /// <summary>是否支持休眠</summary>
    [ObservableProperty]
    private bool _canHibernate;

    /// <summary>长倒计时标记（>24h）</summary>
    [ObservableProperty]
    private bool _isLongCountdown;

    /// <summary>上次重新计算时间</summary>
    private DateTime _lastRecalculation = DateTime.MinValue;

    // ==================== ActionItems 数据类 ====================

    /// <summary>
    /// 操作类型项 — 用于 ComboBox 绑定
    /// </summary>
    public class ShutdownActionItem
    {
        public ShutdownAction Action { get; set; }
        public string DisplayName { get; set; } = "";
        public bool IsAvailable { get; set; } = true;
        public string Tooltip { get; set; } = "";
    }

    // ==================== 构造函数 ====================

    /// <summary>
    /// 构造函数 — 通过 DI 注入服务，初始化电源检测和操作列表
    /// 启动时自动加载已保存的任务并执行崩溃恢复
    /// </summary>
    public ScheduledShutdownViewModel(
        IShutdownService shutdownService,
        ILogService logService,
        IConfigService configService)
    {
        _shutdown = shutdownService;
        _log = logService;
        _config = configService;

        // 同步电源能力
        CanSleep = _shutdown.CanSleep;
        CanHibernate = _shutdown.CanHibernate;

        // 构建操作列表
        BuildActionItems();

        // 默认选中第一个可用操作
        var firstAvailable = ActionItems.FirstOrDefault(a => a.IsAvailable);
        if (firstAvailable != null)
            SelectedAction = firstAvailable.Action;

        // 加载已保存的任务
        LoadTasksFromFile();

        // 检查是否有活跃任务需要恢复
        var pendiente = Tasks.FirstOrDefault(t => t.Status == ShutdownTaskStatus.Pendiente);
        if (pendiente != null)
        {
            RestoreActiveTask(pendiente);
        }
    }

    // ==================== 操作列表构建 ====================

    /// <summary>
    /// 构建操作类型列表
    /// 不支持的操作标记为灰色并添加提示
    /// </summary>
    private void BuildActionItems()
    {
        ActionItems.Clear();

        ActionItems.Add(new ShutdownActionItem
        {
            Action = ShutdownAction.Shutdown,
            DisplayName = "关机",
            IsAvailable = true,
            Tooltip = "关闭计算机"
        });

        ActionItems.Add(new ShutdownActionItem
        {
            Action = ShutdownAction.Restart,
            DisplayName = "重启",
            IsAvailable = true,
            Tooltip = "重新启动计算机"
        });

        ActionItems.Add(new ShutdownActionItem
        {
            Action = ShutdownAction.Sleep,
            DisplayName = CanSleep ? "睡眠" : "睡眠（系统未开启此功能）",
            IsAvailable = CanSleep,
            Tooltip = CanSleep ? "进入睡眠模式" : "您的系统不支持睡眠功能或未开启"
        });

        ActionItems.Add(new ShutdownActionItem
        {
            Action = ShutdownAction.Hibernate,
            DisplayName = CanHibernate ? "休眠" : "休眠（系统未开启此功能）",
            IsAvailable = CanHibernate,
            Tooltip = CanHibernate ? "进入休眠模式" : "您的系统不支持休眠功能或未开启"
        });

        ActionItems.Add(new ShutdownActionItem
        {
            Action = ShutdownAction.Lock,
            DisplayName = "锁屏",
            IsAvailable = true,
            Tooltip = "锁定当前用户会话"
        });

        ActionItems.Add(new ShutdownActionItem
        {
            Action = ShutdownAction.Logout,
            DisplayName = "注销",
            IsAvailable = true,
            Tooltip = "注销当前用户"
        });
    }

    // ==================== 模式切换 ====================

    /// <summary>
    /// 切换到指定时间模式
    /// </summary>
    [RelayCommand]
    private void SwitchToSpecificTime()
    {
        IsSpecificTimeMode = true;
    }

    /// <summary>
    /// 切换到倒计时模式
    /// </summary>
    [RelayCommand]
    private void SwitchToCountdown()
    {
        IsSpecificTimeMode = false;
    }

    // ==================== 任务创建 ====================

    /// <summary>
    /// 创建新的定时关机任务
    /// 验证输入有效性后创建任务、持久化并启动监控
    /// </summary>
    [RelayCommand]
    private void CreateTask()
    {
        try
        {
            // 验证选中的操作是否可用
            if (!ActionItems.Any(a => a.Action == SelectedAction && a.IsAvailable))
            {
                StatusMessage = "该操作不可用，请选择其他操作";
                return;
            }

            // 计算目标时间和倒计时
            DateTime targetTime;
            TimeSpan countdown;
            string displayName;

            if (IsSpecificTimeMode)
            {
                // 指定时间模式
                targetTime = TargetDate.Date + new TimeSpan(TargetHour, TargetMinute, 0);

                if (targetTime <= DateTime.Now)
                {
                    StatusMessage = "目标时间已过，请选择将来的时间";
                    return;
                }

                countdown = targetTime - DateTime.Now;
                displayName = $"{SelectedAction} - {targetTime:yyyy-MM-dd HH:mm}";
            }
            else
            {
                // 倒计时模式
                countdown = new TimeSpan(CountdownHours, CountdownMinutes, CountdownSeconds);

                if (countdown.TotalSeconds <= 0)
                {
                    StatusMessage = "倒计时必须大于 0";
                    return;
                }

                targetTime = DateTime.Now + countdown;
                displayName = countdown.TotalDays >= 1
                    ? $"{SelectedAction} - {(int)countdown.TotalDays}天{countdown.Hours}时{countdown.Minutes}分后"
                    : $"{SelectedAction} - {countdown.Hours:D2}:{countdown.Minutes:D2}:{countdown.Seconds:D2}后";
            }

            // 如果已有活跃任务，先取消
            if (ActiveTask != null)
            {
                // 使用L1确认是否覆盖
                if (!ConfirmationHelper.RequestL1($"已有一个{ActiveTask.Action}任务正在进行，是否替换？"))
                    return;

                ActiveTask.Status = ShutdownTaskStatus.Cancelled;
                StopMonitoring();
            }

            // 创建新任务
            var task = new ShutdownTask
            {
                Action = SelectedAction,
                Mode = IsSpecificTimeMode ? ShutdownMode.SpecificTime : ShutdownMode.Countdown,
                TargetTime = targetTime,
                Countdown = countdown,
                DisplayName = displayName,
                Status = ShutdownTaskStatus.Pendiente
            };

            Tasks.Insert(0, task);
            ActiveTask = task;
            HasActiveTask = true;

            // 持久化
            SaveTasksToFile();

            // 启动倒计时监控
            StartMonitoring(task, countdown);

            StatusMessage = $"任务已创建: {displayName}";
            _log.LogOperation("shutdown", "create", displayName);
        }
        catch (Exception ex)
        {
            _log.LogError("创建关机任务异常", ex);
            StatusMessage = $"创建任务失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 取消当前活跃任务
    /// </summary>
    [RelayCommand]
    private void CancelTask()
    {
        if (ActiveTask == null) return;

        var task = ActiveTask;
        task.Status = ShutdownTaskStatus.Cancelled;
        StopMonitoring();
        ActiveTask = null;
        HasActiveTask = false;
        RemainingTimeDisplay = "--:--:--";

        // 取消系统层面的关机计划
        _shutdown.CancelSystemShutdown();

        SaveTasksToFile();

        StatusMessage = $"已取消: {task.DisplayName}";
        _log.LogOperation("shutdown", "cancel", task.DisplayName);
    }

    /// <summary>
    /// 从任务列表中删除指定任务（仅非活跃状态可删除）
    /// </summary>
    [RelayCommand]
    private void RemoveTask(ShutdownTask? task)
    {
        if (task == null) return;

        if (task.Status == ShutdownTaskStatus.Pendiente)
        {
            StatusMessage = "无法删除正在进行中的任务，请先取消";
            return;
        }

        Tasks.Remove(task);
        SaveTasksToFile();
        StatusMessage = $"已删除: {task.DisplayName}";
    }

    // ==================== 计时监控 ====================

    /// <summary>
    /// 启动倒计时监控
    /// 使用 Stopwatch + Environment.TickCount64 双保险
    /// 长倒计时（>24h）每30分钟重新计算剩余时间以消除累积误差
    /// </summary>
    private void StartMonitoring(ShutdownTask task, TimeSpan countdown)
    {
        _stopwatch.Restart();
        _tickCountStart = Environment.TickCount64;
        _totalCountdownSeconds = countdown.TotalSeconds;
        _lastReminderMinute = -1;
        _isExecuting = false;
        IsLongCountdown = countdown.TotalDays > 1;
        _lastRecalculation = DateTime.Now;

        // 启动 1 秒间隔的定时器
        _timer?.Dispose();
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (s, e) =>
        {
            Application.Current?.Dispatcher.Invoke(() => OnTimerTick(task));
        };
        _timer.AutoReset = true;
        _timer.Start();

        _log.LogInfo($"开始监控任务: {task.DisplayName}, 总时长={countdown.TotalSeconds:F0}秒");
    }

    /// <summary>
    /// 停止计时监控
    /// </summary>
    private void StopMonitoring()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        _stopwatch.Reset();
    }

    /// <summary>
    /// 定时器每秒触发 — 更新剩余时间、检查提醒、触发执行
    /// 双保险计时：同时使用 Stopwatch 和 TickCount64 计算，取平均值
    /// 长倒计时每30分钟重新基于系统时间计算剩余时间
    /// </summary>
    private void OnTimerTick(ShutdownTask task)
    {
        try
        {
            if (task.Status != ShutdownTaskStatus.Pendiente || _isExecuting)
                return;

            // 双保险计时
            double elapsedSw = _stopwatch.Elapsed.TotalSeconds;
            double elapsedTc = (Environment.TickCount64 - _tickCountStart) / 1000.0;
            double elapsed = (elapsedSw + elapsedTc) / 2.0; // 取平均值

            // 长倒计时(>24h)：每30分钟基于系统时间重新计算剩余时间
            if (IsLongCountdown && (DateTime.Now - _lastRecalculation).TotalMinutes >= 30)
            {
                // 基于创建时间和总时长重新计算
                var totalElapsed = (DateTime.Now - task.CreatedAt).TotalSeconds;
                if (totalElapsed > 0 && totalElapsed < _totalCountdownSeconds * 1.1) // 1.1倍容差
                {
                    elapsed = totalElapsed;
                    // 重置 Stopwatch 基准以消除累积误差
                    _stopwatch.Restart();
                    _tickCountStart = Environment.TickCount64 - (long)(totalElapsed * 1000);
                }
                _lastRecalculation = DateTime.Now;
            }

            // 计算剩余秒数
            double remaining = _totalCountdownSeconds - elapsed;
            if (remaining <= 0) remaining = 0;

            // 更新 UI 显示
            UpdateRemainingDisplay(remaining);

            // 检查提醒节点
            CheckReminders(task, remaining);

            // 检查是否到达执行时间
            if (remaining <= 0.5) // 0.5秒容差
            {
                ExecuteTask(task);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("定时器回调异常", ex);
        }
    }

    /// <summary>
    /// 更新剩余时间显示
    /// </summary>
    private void UpdateRemainingDisplay(double remainingSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));

        if (ts.TotalDays >= 1)
            RemainingTimeDisplay = $"{(int)ts.TotalDays}天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        else
            RemainingTimeDisplay = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// 检查并在5分钟和1分钟节点发出提醒
    /// </summary>
    private void CheckReminders(ShutdownTask task, double remainingSeconds)
    {
        var currentMinute = (int)(remainingSeconds / 60);

        // 5分钟提醒
        if (remainingSeconds <= 300 && remainingSeconds > 240 && _lastReminderMinute > 5)
        {
            ShowReminder(task, 5);
            _lastReminderMinute = currentMinute;
        }
        // 1分钟提醒
        else if (remainingSeconds <= 60 && remainingSeconds > 30 && _lastReminderMinute > 1)
        {
            ShowReminder(task, 1);
            _lastReminderMinute = currentMinute;
        }
        else if (_lastReminderMinute == -1 || currentMinute != _lastReminderMinute)
        {
            _lastReminderMinute = currentMinute;
        }
    }

    /// <summary>
    /// 显示提醒消息
    /// </summary>
    private void ShowReminder(ShutdownTask task, int minutesLeft)
    {
        var actionName = GetActionName(task.Action);
        StatusMessage = $"提醒: 将在 {minutesLeft} 分钟后{actionName}";

        // 弹窗提醒
        MessageBox.Show(
            $"系统将在 {minutesLeft} 分钟后{actionName}。\n任务: {task.DisplayName}",
            "定时关机提醒",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        _log.LogOperation("shutdown", "reminder", $"{task.DisplayName} - {minutesLeft}分钟");
    }

    /// <summary>
    /// 获取操作的中文名称
    /// </summary>
    private static string GetActionName(ShutdownAction action) => action switch
    {
        ShutdownAction.Shutdown => "关机",
        ShutdownAction.Restart => "重启",
        ShutdownAction.Sleep => "进入睡眠",
        ShutdownAction.Hibernate => "进入休眠",
        ShutdownAction.Lock => "锁屏",
        ShutdownAction.Logout => "注销",
        _ => action.ToString()
    };

    // ==================== 任务执行 ====================

    /// <summary>
    /// 执行任务 — 到达倒计时终点时触发
    /// </summary>
    private void ExecuteTask(ShutdownTask task)
    {
        if (_isExecuting) return;
        _isExecuting = true;

        try
        {
            task.Status = ShutdownTaskStatus.Executing;
            SaveTasksToFile();

            // 停止监控
            StopMonitoring();

            StatusMessage = $"正在执行: {task.DisplayName}";

            // 执行关机操作（delaySeconds=0 表示立即执行）
            bool success = _shutdown.ExecuteShutdown(task.Action, 0, task.DisplayName);

            if (success)
            {
                task.Status = ShutdownTaskStatus.Executed;
                StatusMessage = $"已执行: {task.DisplayName}";
                _log.LogOperation("shutdown", "executed", task.DisplayName);
            }
            else
            {
                task.Status = ShutdownTaskStatus.Pendiente; // 执行失败，恢复等待
                StatusMessage = $"执行失败: {task.DisplayName}";
                _log.LogError($"任务执行失败: {task.DisplayName}");
                // 重新启动监控
                StartMonitoring(task, TimeSpan.FromSeconds(30)); // 30秒后重试
                _isExecuting = false;
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"任务执行异常: {task.DisplayName}", ex);
            task.Status = ShutdownTaskStatus.Pendiente;
            StatusMessage = $"执行异常: {ex.Message}";
            _isExecuting = false;
            return;
        }

        // 更新 UI 状态
        ActiveTask = null;
        HasActiveTask = false;
        RemainingTimeDisplay = "--:--:--";
        SaveTasksToFile();

        // 锁屏和注销后保持界面可用
        _isExecuting = false;
    }

    /// <summary>
    /// 恢复已保存的活跃任务（程序重启后）
    /// 重新启动倒计时监控
    /// </summary>
    private void RestoreActiveTask(ShutdownTask task)
    {
        if (task.Mode == ShutdownMode.Countdown && task.Countdown.HasValue)
        {
            // 计算剩余倒计时
            var elapsed = DateTime.Now - task.CreatedAt;
            var remaining = task.Countdown.Value - elapsed;

            if (remaining.TotalSeconds <= 0)
            {
                // 已经过期，立即执行
                task.Status = ShutdownTaskStatus.Missed;
                SaveTasksToFile();
                StatusMessage = $"任务已过期: {task.DisplayName}";
                _log.LogWarning($"恢复任务时发现已过期: {task.DisplayName}");
                return;
            }

            // 恢复监控
            ActiveTask = task;
            HasActiveTask = true;
            StartMonitoring(task, remaining);
            StatusMessage = $"已恢复任务: {task.DisplayName}";
            _log.LogInfo($"恢复活跃任务: {task.DisplayName}, 剩余={remaining.TotalSeconds:F0}秒");
        }
        else if (task.Mode == ShutdownMode.SpecificTime && task.TargetTime.HasValue)
        {
            var remaining = task.TargetTime.Value - DateTime.Now;

            if (remaining.TotalSeconds <= 0)
            {
                // 目标时间已过
                task.Status = ShutdownTaskStatus.Missed;
                SaveTasksToFile();
                StatusMessage = $"任务已过期: {task.DisplayName}";
                _log.LogWarning($"恢复任务时发现已过期: {task.DisplayName}");
                return;
            }

            // 恢复监控（切换到倒计时模式）
            task.Mode = ShutdownMode.Countdown;
            task.Countdown = remaining;
            ActiveTask = task;
            HasActiveTask = true;
            StartMonitoring(task, remaining);
            StatusMessage = $"已恢复任务: {task.DisplayName}";
            _log.LogInfo($"恢复活跃任务(指定时间转倒计时): {task.DisplayName}, 剩余={remaining.TotalSeconds:F0}秒");
        }
    }

    // ==================== 任务持久化 ====================

    /// <summary>
    /// 从文件加载任务列表
    /// </summary>
    private void LoadTasksFromFile()
    {
        var loaded = _shutdown.LoadTasks();
        Tasks = new ObservableCollection<ShutdownTask>(loaded);
    }

    /// <summary>
    /// 保存当前任务列表到文件
    /// </summary>
    private void SaveTasksToFile()
    {
        _shutdown.SaveTasks(Tasks.ToList());
    }
}
