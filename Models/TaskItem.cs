using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 任务重要性枚举
/// </summary>
public enum TaskImportance
{
    /// <summary>普通 — 30秒后自动关闭 + 通知栏提示</summary>
    Normal,
    /// <summary>重要 — 不自动消失 + 提示音循环</summary>
    Important
}

/// <summary>
/// 任务重复模式枚举
/// </summary>
public enum TaskRepeatMode
{
    /// <summary>不重复，仅触发一次</summary>
    Once,
    /// <summary>每天同一时间触发</summary>
    Daily,
    /// <summary>每周指定星期几触发</summary>
    Weekly,
    /// <summary>自定义间隔（每 N 天/小时）</summary>
    Custom
}

/// <summary>
/// 任务数据模型
/// 存储任务的所有属性：内容、触发时间、重复、重要性、提示音、完成/暂停状态
/// </summary>
public partial class TaskItem : ObservableObject
{
    /// <summary>任务唯一标识</summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    /// <summary>任务内容描述</summary>
    [ObservableProperty]
    private string _content = "";

    /// <summary>触发时间</summary>
    [ObservableProperty]
    private DateTime _triggerTime = DateTime.Now.AddMinutes(5);

    /// <summary>重复模式</summary>
    [ObservableProperty]
    private TaskRepeatMode _repeatMode = TaskRepeatMode.Once;

    /// <summary>自定义间隔数值（Custom 模式使用）</summary>
    [ObservableProperty]
    private int _customInterval = 1;

    /// <summary>自定义间隔单位：天/小时（Custom 模式使用）</summary>
    [ObservableProperty]
    private string _customIntervalUnit = "天";

    /// <summary>每周重复的星期几（逗号分隔，如 "1,3,5" 表示周一三五）</summary>
    [ObservableProperty]
    private string _weeklyDays = "";

    /// <summary>重要性</summary>
    [ObservableProperty]
    private TaskImportance _importance = TaskImportance.Normal;

    /// <summary>提示音类型：SystemDefault / Silent / CustomWav</summary>
    [ObservableProperty]
    private string _alertSound = "SystemDefault";

    /// <summary>自定义提示音 WAV 文件路径</summary>
    [ObservableProperty]
    private string _alertSoundPath = "";

    /// <summary>是否已完成</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>是否已暂停</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>创建时间</summary>
    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;

    /// <summary>上次触发时间</summary>
    [ObservableProperty]
    private DateTime? _lastTriggeredAt;

    /// <summary>下次触发时间（根据重复模式计算）</summary>
    [ObservableProperty]
    private DateTime _nextTriggerTime;

    /// <summary>
    /// 判断任务是否逾期（当前时间 > 触发时间且未完成）
    /// </summary>
    public bool IsOverdue => !IsCompleted && !IsPaused && NextTriggerTime < DateTime.Now;

    /// <summary>
    /// 构造函数 — 初始化下次触发时间为当前触发时间
    /// </summary>
    public TaskItem()
    {
        NextTriggerTime = TriggerTime;
    }

    /// <summary>
    /// 根据重复模式计算下次触发时间
    /// </summary>
    /// <returns>计算后的下次触发时间</returns>
    public DateTime CalculateNextTriggerTime()
    {
        if (IsCompleted || IsPaused) return NextTriggerTime;

        switch (RepeatMode)
        {
            case TaskRepeatMode.Once:
                return TriggerTime;

            case TaskRepeatMode.Daily:
                // 每天同一时间
                var next = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
                    TriggerTime.Hour, TriggerTime.Minute, TriggerTime.Second);
                if (next <= DateTime.Now)
                    next = next.AddDays(1);
                return next;

            case TaskRepeatMode.Weekly:
                // 每周指定星期几
                return CalculateNextWeeklyTrigger();

            case TaskRepeatMode.Custom:
                // 自定义间隔
                if (CustomInterval <= 0) CustomInterval = 1;
                var ts = CustomIntervalUnit == "小时"
                    ? TimeSpan.FromHours(CustomInterval)
                    : TimeSpan.FromDays(CustomInterval);
                var nextCustom = TriggerTime;
                while (nextCustom <= DateTime.Now)
                    nextCustom += ts;
                return nextCustom;

            default:
                return TriggerTime;
        }
    }

    /// <summary>
    /// 计算每周触发的下次时间
    /// </summary>
    private DateTime CalculateNextWeeklyTrigger()
    {
        if (string.IsNullOrWhiteSpace(WeeklyDays))
            return DateTime.Now.AddDays(1); // 如果没有选星期几，默认明天

        // 解析星期几："1,3,5" → {Monday, Wednesday, Friday}
        var days = WeeklyDays.Split(',')
            .Select(d => int.TryParse(d.Trim(), out int dayOfWeek) ? dayOfWeek : -1)
            .Where(d => d >= 1 && d <= 7)
            .ToList();

        if (days.Count == 0)
            return DateTime.Now.AddDays(1);

        var now = DateTime.Now;
        var triggerTime = new TimeSpan(TriggerTime.Hour, TriggerTime.Minute, TriggerTime.Second);

        for (int i = 0; i <= 7; i++)
        {
            var date = now.Date.AddDays(i);
            // 将 DayOfWeek (0=Sunday) 转换为 1-7 (1=Monday, 7=Sunday)
            int dayOfWeek = date.DayOfWeek == 0 ? 7 : (int)date.DayOfWeek;
            if (days.Contains(dayOfWeek))
            {
                var candidate = date.Add(triggerTime);
                if (candidate > now)
                    return candidate;
            }
        }

        return now.AddDays(1);
    }

    /// <summary>
    /// 验证自定义间隔是否合法
    /// </summary>
    /// <returns>合法返回 true</returns>
    public bool ValidateCustomInterval()
    {
        return CustomInterval >= 1 && CustomInterval <= 365 &&
               (CustomIntervalUnit == "天" || CustomIntervalUnit == "小时");
    }
}
