using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 任务重要性。保留旧枚举名，兼容旧 tasks.json 和现有转换器。
/// </summary>
public enum TaskImportance
{
    Normal,
    Important
}

/// <summary>
/// 任务重复模式。第一版只在提醒时间上生效；到期日不会自动弹提醒。
/// </summary>
public enum TaskRepeatMode
{
    Once,
    Daily,
    Weekly,
    Custom
}

/// <summary>
/// 子步骤：用于把一个任务拆成可勾选的小步骤。
/// </summary>
public partial class TaskStepItem : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;
}

/// <summary>
/// 本地任务数据模型。
/// 兼容旧版 Content/TriggerTime/NextTriggerTime 字段，同时增加清单、标签、备注、步骤、到期日和提醒时间。
/// </summary>
public partial class TaskItem : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>任务标题。旧版字段名叫 Content，继续保留作为主标题字段。</summary>
    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _listName = "收件箱";

    [ObservableProperty]
    private ObservableCollection<string> _tags = new();

    [ObservableProperty]
    private ObservableCollection<TaskStepItem> _steps = new();

    [ObservableProperty]
    private DateTime? _dueDate;

    [ObservableProperty]
    private DateTime? _reminderAt;

    /// <summary>旧版触发时间字段。新建任务不依赖它，但加载旧数据时会迁移到 DueDate/ReminderAt。</summary>
    [ObservableProperty]
    private DateTime _triggerTime = DateTime.MinValue;

    [ObservableProperty]
    private TaskRepeatMode _repeatMode = TaskRepeatMode.Once;

    [ObservableProperty]
    private int _customInterval = 1;

    [ObservableProperty]
    private string _customIntervalUnit = "天";

    /// <summary>每周重复的星期：1=周一，7=周日，逗号分隔。</summary>
    [ObservableProperty]
    private string _weeklyDays = string.Empty;

    [ObservableProperty]
    private TaskImportance _importance = TaskImportance.Normal;

    [ObservableProperty]
    private string _alertSound = "SystemDefault";

    [ObservableProperty]
    private string _alertSoundPath = string.Empty;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.Now;

    [ObservableProperty]
    private DateTime? _completedAt;

    [ObservableProperty]
    private DateTime? _lastTriggeredAt;

    /// <summary>兼容旧 UI/排序字段：下一次提醒时间；没有提醒时为到期日或 DateTime.MaxValue。</summary>
    [ObservableProperty]
    private DateTime _nextTriggerTime = DateTime.MaxValue;

    [JsonIgnore]
    public bool IsOverdue => !IsCompleted && !IsPaused && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;

    [JsonIgnore]
    public bool IsDueToday => !IsCompleted && DueDate.HasValue && DueDate.Value.Date == DateTime.Today;

    [JsonIgnore]
    public bool IsUpcoming => !IsCompleted && DueDate.HasValue && DueDate.Value.Date > DateTime.Today && DueDate.Value.Date <= DateTime.Today.AddDays(7);

    [JsonIgnore]
    public bool HasReminder => ReminderAt.HasValue;

    [JsonIgnore]
    public bool HasSteps => Steps.Count > 0;

    [JsonIgnore]
    public int StepCount => Steps.Count;

    [JsonIgnore]
    public int CompletedStepCount => Steps.Count(s => s.IsDone);

    [JsonIgnore]
    public int StepProgressPercent => StepCount == 0 ? 0 : (int)Math.Round(CompletedStepCount * 100.0 / StepCount);

    [JsonIgnore]
    public string ListDisplay => string.IsNullOrWhiteSpace(ListName) ? "收件箱" : ListName.Trim();

    [JsonIgnore]
    public string TagDisplay => Tags.Count == 0 ? "" : string.Join("  ", Tags.Select(t => "#" + t));

    [JsonIgnore]
    public string PriorityText => Importance == TaskImportance.Important ? "重要" : "普通";

    [JsonIgnore]
    public string CompletionText => IsCompleted ? "已完成" : IsPaused ? "已暂停" : IsOverdue ? "已逾期" : "进行中";

    [JsonIgnore]
    public string DueText
    {
        get
        {
            if (!DueDate.HasValue) return "无到期日";
            var date = DueDate.Value.Date;
            if (date == DateTime.Today) return "今天";
            if (date == DateTime.Today.AddDays(1)) return "明天";
            if (date == DateTime.Today.AddDays(-1)) return "昨天";
            return date.ToString("yyyy-MM-dd");
        }
    }

    [JsonIgnore]
    public string ReminderText => ReminderAt.HasValue ? ReminderAt.Value.ToString("yyyy-MM-dd HH:mm") : "无提醒";

    [JsonIgnore]
    public DateTime SortDate => DueDate ?? ReminderAt ?? CreatedAt;

    public void NormalizeAfterLoad()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = Guid.NewGuid().ToString("N");

        Content = Content?.Trim() ?? string.Empty;
        Notes ??= string.Empty;
        ListName = string.IsNullOrWhiteSpace(ListName) ? "收件箱" : ListName.Trim();
        Tags ??= new ObservableCollection<string>();
        Steps ??= new ObservableCollection<TaskStepItem>();
        AlertSound ??= "SystemDefault";
        AlertSoundPath ??= string.Empty;
        WeeklyDays ??= string.Empty;
        CustomIntervalUnit = NormalizeIntervalUnit(CustomIntervalUnit);
        if (CustomInterval <= 0) CustomInterval = 1;
        if (CreatedAt == default) CreatedAt = DateTime.Now;
        if (UpdatedAt == default) UpdatedAt = CreatedAt;

        // 旧版任务只记录 TriggerTime。把它迁移成到期日和提醒时间，避免旧任务消失。
        if (!DueDate.HasValue && TriggerTime > DateTime.MinValue.AddDays(1))
            DueDate = TriggerTime.Date;

        if (!ReminderAt.HasValue && TriggerTime > DateTime.MinValue.AddDays(1) && TriggerTime.TimeOfDay != TimeSpan.Zero)
            ReminderAt = TriggerTime;

        RecalculateNextTriggerTime();
    }

    public void MarkUpdated()
    {
        UpdatedAt = DateTime.Now;
        RecalculateNextTriggerTime();
    }

    public void RecalculateNextTriggerTime()
    {
        var nextReminder = CalculateNextReminderTime(DateTime.Now);
        if (nextReminder.HasValue)
        {
            NextTriggerTime = nextReminder.Value;
            TriggerTime = nextReminder.Value;
            return;
        }

        if (DueDate.HasValue)
        {
            NextTriggerTime = DueDate.Value.Date.AddDays(1).AddTicks(-1);
            return;
        }

        NextTriggerTime = DateTime.MaxValue;
    }

    public DateTime? CalculateNextReminderTime(DateTime from)
    {
        if (IsCompleted || IsPaused || !ReminderAt.HasValue)
            return null;

        var baseReminder = ReminderAt.Value;
        if (baseReminder > from)
            return baseReminder;

        return RepeatMode switch
        {
            TaskRepeatMode.Once => baseReminder,
            TaskRepeatMode.Daily => MoveForward(baseReminder, from, TimeSpan.FromDays(1)),
            TaskRepeatMode.Weekly => CalculateNextWeeklyReminder(from),
            TaskRepeatMode.Custom => MoveForward(baseReminder, from, CustomIntervalUnit == "小时"
                ? TimeSpan.FromHours(Math.Max(1, CustomInterval))
                : TimeSpan.FromDays(Math.Max(1, CustomInterval))),
            _ => baseReminder
        };
    }

    public bool ValidateCustomInterval()
    {
        return CustomInterval >= 1 &&
               ((CustomIntervalUnit == "天" && CustomInterval <= 365) ||
                (CustomIntervalUnit == "小时" && CustomInterval <= 8760));
    }

    private DateTime CalculateNextWeeklyReminder(DateTime from)
    {
        var days = WeeklyDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var value) ? value : -1)
            .Where(d => d is >= 1 and <= 7)
            .Distinct()
            .ToList();

        if (days.Count == 0)
            days.Add(ToMondayFirstDayOfWeek(ReminderAt!.Value.DayOfWeek));

        var time = ReminderAt!.Value.TimeOfDay;
        for (var i = 0; i <= 14; i++)
        {
            var date = from.Date.AddDays(i);
            var day = ToMondayFirstDayOfWeek(date.DayOfWeek);
            if (!days.Contains(day)) continue;
            var candidate = date.Add(time);
            if (candidate > from) return candidate;
        }

        return from.AddDays(7).Date.Add(time);
    }

    private static DateTime MoveForward(DateTime value, DateTime from, TimeSpan interval)
    {
        var next = value;
        while (next <= from)
            next = next.Add(interval);
        return next;
    }

    private static int ToMondayFirstDayOfWeek(DayOfWeek dayOfWeek)
        => dayOfWeek == DayOfWeek.Sunday ? 7 : (int)dayOfWeek;

    private static string NormalizeIntervalUnit(string? unit)
    {
        if (unit == "小时" || unit?.Equals("hour", StringComparison.OrdinalIgnoreCase) == true)
            return "小时";
        return "天";
    }
}
