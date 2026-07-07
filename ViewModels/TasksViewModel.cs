using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

public enum TaskFilterKind
{
    All,
    Today,
    Upcoming,
    Overdue,
    Important,
    Paused,
    Completed,
    Repeating,
    List
}

public enum TaskSortMode
{
    Smart,
    DueDate,
    Priority,
    CreatedAt,
    UpdatedAt
}

public sealed class TaskFilterItem
{
    public TaskFilterKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = "\uE71D";
    public string? ListName { get; init; }
    public int Count { get; init; }
    public string Group { get; init; } = "智能视图";
    public string Key => Kind == TaskFilterKind.List ? $"list:{ListName}" : Kind.ToString();
}

public sealed class TaskSortOption
{
    public TaskSortMode Mode { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// 任务中心 ViewModel：本地离线任务、清单、筛选、详情编辑、子步骤和提醒状态。
/// </summary>
public partial class TasksViewModel : ViewModelBase
{
    private readonly ITaskManagerService _taskService;
    private readonly ILogService _log;
    private bool _isLoadingEditor;
    private TaskItem? _lastDeletedTask;

    [ObservableProperty]
    private ObservableCollection<TaskItem> _allTasks = new();

    [ObservableProperty]
    private ObservableCollection<TaskItem> _visibleTasks = new();

    [ObservableProperty]
    private ObservableCollection<TaskFilterItem> _filters = new();

    [ObservableProperty]
    private TaskFilterItem? _selectedFilter;

    [ObservableProperty]
    private TaskItem? _selectedTask;

    public ObservableCollection<TaskSortOption> SortOptions { get; } = new()
    {
        new TaskSortOption { Mode = TaskSortMode.Smart, Name = "智能排序" },
        new TaskSortOption { Mode = TaskSortMode.DueDate, Name = "按到期时间" },
        new TaskSortOption { Mode = TaskSortMode.Priority, Name = "按重要性" },
        new TaskSortOption { Mode = TaskSortMode.CreatedAt, Name = "按创建时间" },
        new TaskSortOption { Mode = TaskSortMode.UpdatedAt, Name = "按更新时间" }
    };

    public IReadOnlyList<string> HourOptions { get; } = Enumerable.Range(0, 24)
        .Select(hour => hour.ToString("00"))
        .ToArray();

    public IReadOnlyList<string> MinuteOptions { get; } = Enumerable.Range(0, 60)
        .Select(minute => minute.ToString("00"))
        .ToArray();

    [ObservableProperty]
    private TaskSortOption? _selectedSortOption;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _openCount;

    [ObservableProperty]
    private int _todayCount;

    [ObservableProperty]
    private int _overdueCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private string _nextReminderSummary = "无提醒";

    [ObservableProperty]
    private bool _hasVisibleTasks;

    [ObservableProperty]
    private bool _hasSelectedTask;

    [ObservableProperty]
    private bool _canUndoDelete;

    [ObservableProperty]
    private string _emptyTitle = "还没有任务";

    [ObservableProperty]
    private string _emptyHint = "创建一个本地任务，Toolbox 会在指定时间提醒你。";

    [ObservableProperty]
    private string _quickTitle = string.Empty;

    [ObservableProperty]
    private DateTime? _quickDueDate = DateTime.Today;

    [ObservableProperty]
    private string _quickTimeText = string.Empty;

    [ObservableProperty]
    private string _quickHourText = string.Empty;

    [ObservableProperty]
    private string _quickMinuteText = string.Empty;

    [ObservableProperty]
    private bool _quickImportant;

    [ObservableProperty]
    private string _quickInputError = string.Empty;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    [ObservableProperty]
    private string _editListName = "收件箱";

    [ObservableProperty]
    private string _editTagsText = string.Empty;

    [ObservableProperty]
    private DateTime? _editDueDate;

    [ObservableProperty]
    private bool _editHasReminder;

    [ObservableProperty]
    private DateTime? _editReminderDate;

    [ObservableProperty]
    private string _editReminderTime = "09:00";

    [ObservableProperty]
    private string _editReminderHourText = "09";

    [ObservableProperty]
    private string _editReminderMinuteText = "00";

    [ObservableProperty]
    private TaskRepeatMode _editRepeatMode = TaskRepeatMode.Once;

    [ObservableProperty]
    private int _editCustomInterval = 1;

    [ObservableProperty]
    private string _editCustomIntervalUnit = "天";

    [ObservableProperty]
    private TaskImportance _editImportance = TaskImportance.Normal;

    [ObservableProperty]
    private string _editAlertSound = "SystemDefault";

    [ObservableProperty]
    private bool _editIsPaused;

    [ObservableProperty]
    private bool _editWeeklyMon;

    [ObservableProperty]
    private bool _editWeeklyTue;

    [ObservableProperty]
    private bool _editWeeklyWed;

    [ObservableProperty]
    private bool _editWeeklyThu;

    [ObservableProperty]
    private bool _editWeeklyFri;

    [ObservableProperty]
    private bool _editWeeklySat;

    [ObservableProperty]
    private bool _editWeeklySun;

    [ObservableProperty]
    private bool _isWeeklyEditorVisible;

    [ObservableProperty]
    private bool _isCustomEditorVisible;

    [ObservableProperty]
    private string _editValidationMessage = string.Empty;

    [ObservableProperty]
    private string _newStepTitle = string.Empty;

    public TasksViewModel(ITaskManagerService taskService, ILogService log)
    {
        _taskService = taskService;
        _log = log;
        SelectedSortOption = SortOptions[0];

        _taskService.TasksChanged += OnTasksChanged;
        _taskService.TaskTriggered += OnTaskTriggered;

        RefreshFromService();
        StatusMessage = "任务中心就绪 — 本地保存，切换页面不会丢失";
    }

    [RelayCommand]
    private void AddQuickTask()
    {
        QuickInputError = string.Empty;
        var title = QuickTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            QuickInputError = "请输入任务标题";
            return;
        }

        var reminder = BuildQuickReminder();
        if (!string.IsNullOrWhiteSpace(QuickInputError))
            return;

        var listName = SelectedFilter?.Kind == TaskFilterKind.List && !string.IsNullOrWhiteSpace(SelectedFilter.ListName)
            ? SelectedFilter.ListName!
            : "收件箱";

        var task = new TaskItem
        {
            Content = title,
            ListName = listName,
            DueDate = QuickDueDate?.Date,
            ReminderAt = reminder,
            Importance = QuickImportant ? TaskImportance.Important : TaskImportance.Normal
        };

        if (!_taskService.AddTask(task))
        {
            QuickInputError = "添加失败，任务数量已达到上限";
            return;
        }

        QuickTitle = string.Empty;
        QuickTimeText = string.Empty;
        QuickHourText = string.Empty;
        QuickMinuteText = string.Empty;
        QuickImportant = false;
        SelectedTask = task;
        StatusMessage = $"已添加任务：{task.Content}";
    }

    [RelayCommand]
    private void SetQuickDueToday() => QuickDueDate = DateTime.Today;

    [RelayCommand]
    private void SetQuickDueTomorrow() => QuickDueDate = DateTime.Today.AddDays(1);

    [RelayCommand]
    private void ClearQuickDue() => QuickDueDate = null;

    [RelayCommand]
    private void ToggleTaskComplete(TaskItem? task)
    {
        if (task == null) return;
        _taskService.ToggleComplete(task.Id);
        StatusMessage = task.IsCompleted ? $"已重新激活：{task.Content}" : $"已完成：{task.Content}";
    }

    [RelayCommand]
    private void ToggleTaskPause(TaskItem? task)
    {
        if (task == null) return;
        _taskService.TogglePause(task.Id);
        StatusMessage = task.IsPaused ? $"已恢复：{task.Content}" : $"已暂停：{task.Content}";
    }

    [RelayCommand]
    private void DeleteTask(TaskItem? task)
    {
        if (task == null) return;
        _lastDeletedTask = CloneTask(task);
        _taskService.DeleteTask(task.Id);
        CanUndoDelete = true;
        if (SelectedTask?.Id == task.Id)
            SelectedTask = null;
        StatusMessage = $"已删除“{task.Content}”，可撤销";
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (_lastDeletedTask == null) return;
        var restored = CloneTask(_lastDeletedTask);
        restored.Id = Guid.NewGuid().ToString("N");
        _taskService.AddTask(restored);
        SelectedTask = restored;
        _lastDeletedTask = null;
        CanUndoDelete = false;
        StatusMessage = $"已恢复：{restored.Content}";
    }

    [RelayCommand]
    private void ClearCompletedTasks()
    {
        var count = _taskService.ClearCompleted();
        StatusMessage = count > 0 ? $"已清除 {count} 个已完成任务" : "没有已完成任务可清除";
    }

    [RelayCommand]
    private void SaveSelectedTask()
    {
        if (SelectedTask == null) return;

        EditValidationMessage = string.Empty;
        var title = EditTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            EditValidationMessage = "任务标题不能为空";
            return;
        }

        var reminder = BuildEditorReminder();
        if (!string.IsNullOrWhiteSpace(EditValidationMessage))
            return;

        if (EditRepeatMode != TaskRepeatMode.Once && !reminder.HasValue)
        {
            EditValidationMessage = "重复任务需要设置提醒时间";
            return;
        }

        if (EditRepeatMode == TaskRepeatMode.Custom)
        {
            var unit = EditCustomIntervalUnit == "小时" ? "小时" : "天";
            var limit = unit == "小时" ? 8760 : 365;
            if (EditCustomInterval < 1 || EditCustomInterval > limit)
            {
                EditValidationMessage = unit == "小时" ? "小时间隔必须在 1-8760 之间" : "天数间隔必须在 1-365 之间";
                return;
            }
        }

        SelectedTask.Content = title;
        SelectedTask.Notes = EditNotes.Trim();
        SelectedTask.ListName = string.IsNullOrWhiteSpace(EditListName) ? "收件箱" : EditListName.Trim();
        SelectedTask.Tags = new ObservableCollection<string>(ParseTags(EditTagsText));
        SelectedTask.DueDate = EditDueDate?.Date;
        SelectedTask.ReminderAt = reminder;
        SelectedTask.RepeatMode = EditRepeatMode;
        SelectedTask.CustomInterval = Math.Max(1, EditCustomInterval);
        SelectedTask.CustomIntervalUnit = EditCustomIntervalUnit == "小时" ? "小时" : "天";
        SelectedTask.Importance = EditImportance;
        SelectedTask.AlertSound = EditAlertSound;
        SelectedTask.IsPaused = EditIsPaused;
        SelectedTask.WeeklyDays = BuildWeeklyDays();
        SelectedTask.MarkUpdated();

        _taskService.UpdateTask(SelectedTask);
        StatusMessage = $"已保存：{SelectedTask.Content}";
    }

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedTask == null) return;
        var title = NewStepTitle.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        SelectedTask.Steps.Add(new TaskStepItem { Title = title });
        NewStepTitle = string.Empty;
        _taskService.UpdateTask(SelectedTask);
        LoadEditorFromTask(SelectedTask);
    }

    [RelayCommand]
    private void ToggleStep(TaskStepItem? step)
    {
        if (SelectedTask == null || step == null) return;
        step.IsDone = !step.IsDone;
        _taskService.UpdateTask(SelectedTask);
        OnPropertyChanged(nameof(SelectedTask));
    }

    [RelayCommand]
    private void DeleteStep(TaskStepItem? step)
    {
        if (SelectedTask == null || step == null) return;
        SelectedTask.Steps.Remove(step);
        _taskService.UpdateTask(SelectedTask);
        LoadEditorFromTask(SelectedTask);
    }

    [RelayCommand]
    private void SelectTask(TaskItem? task) => SelectedTask = task;

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        ApplyFilters();
    }

    partial void OnSelectedFilterChanged(TaskFilterItem? value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSortOptionChanged(TaskSortOption? value)
    {
        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedTaskChanged(TaskItem? value)
    {
        HasSelectedTask = value != null;
        if (value != null)
            LoadEditorFromTask(value);
        else
            ClearEditor();
    }

    partial void OnEditRepeatModeChanged(TaskRepeatMode value)
    {
        if (_isLoadingEditor) return;
        RefreshEditorVisibility();
    }

    private void OnTasksChanged()
    {
        if (Application.Current?.Dispatcher?.CheckAccess() == true)
            RefreshFromService();
        else
            Application.Current?.Dispatcher?.InvokeAsync(RefreshFromService);
    }

    private void OnTaskTriggered(TaskItem task)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            StatusMessage = $"提醒：{task.Content}";
            RefreshFromService();
        });
    }

    private void RefreshFromService()
    {
        var selectedKey = SelectedFilter?.Key;
        var selectedTaskId = SelectedTask?.Id;
        var tasks = _taskService.Tasks
            .OrderBy(t => t.IsCompleted)
            .ThenBy(t => t.SortDate)
            .ToList();

        AllTasks = new ObservableCollection<TaskItem>(tasks);
        RebuildFilters(tasks, selectedKey);
        ApplyFilters(selectTaskId: selectedTaskId);
        RefreshStats(tasks);
    }

    private void RebuildFilters(IReadOnlyList<TaskItem> tasks, string? selectedKey)
    {
        var filters = new List<TaskFilterItem>
        {
            CreateFilter(TaskFilterKind.All, "全部未完成", "\uE71D", tasks.Count(t => !t.IsCompleted)),
            CreateFilter(TaskFilterKind.Today, "今天", "\uE787", tasks.Count(t => !t.IsCompleted && t.IsDueToday)),
            CreateFilter(TaskFilterKind.Upcoming, "未来 7 天", "\uE72A", tasks.Count(t => t.IsUpcoming)),
            CreateFilter(TaskFilterKind.Overdue, "已逾期", "\uE730", tasks.Count(t => t.IsOverdue)),
            CreateFilter(TaskFilterKind.Important, "重要", "\uE734", tasks.Count(t => !t.IsCompleted && t.Importance == TaskImportance.Important)),
            CreateFilter(TaskFilterKind.Paused, "已暂停", "\uE769", tasks.Count(t => !t.IsCompleted && t.IsPaused)),
            CreateFilter(TaskFilterKind.Repeating, "重复任务", "\uE72C", tasks.Count(t => !t.IsCompleted && t.RepeatMode != TaskRepeatMode.Once)),
            CreateFilter(TaskFilterKind.Completed, "已完成", "\uE73E", tasks.Count(t => t.IsCompleted))
        };

        foreach (var list in tasks.Select(t => t.ListDisplay).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s))
        {
            filters.Add(new TaskFilterItem
            {
                Kind = TaskFilterKind.List,
                Name = list,
                Icon = "\uE8B7",
                ListName = list,
                Count = tasks.Count(t => !t.IsCompleted && string.Equals(t.ListDisplay, list, StringComparison.OrdinalIgnoreCase)),
                Group = "清单"
            });
        }

        Filters = new ObservableCollection<TaskFilterItem>(filters);
        SelectedFilter = filters.FirstOrDefault(f => f.Key == selectedKey) ?? filters[0];
    }

    private static TaskFilterItem CreateFilter(TaskFilterKind kind, string name, string icon, int count)
        => new() { Kind = kind, Name = name, Icon = icon, Count = count, Group = "智能视图" };

    private void ApplyFilters(string? selectTaskId = null)
    {
        var filter = SelectedFilter;
        IEnumerable<TaskItem> query = AllTasks;

        query = filter?.Kind switch
        {
            TaskFilterKind.Today => query.Where(t => !t.IsCompleted && t.IsDueToday),
            TaskFilterKind.Upcoming => query.Where(t => t.IsUpcoming),
            TaskFilterKind.Overdue => query.Where(t => t.IsOverdue),
            TaskFilterKind.Important => query.Where(t => !t.IsCompleted && t.Importance == TaskImportance.Important),
            TaskFilterKind.Paused => query.Where(t => !t.IsCompleted && t.IsPaused),
            TaskFilterKind.Completed => query.Where(t => t.IsCompleted),
            TaskFilterKind.Repeating => query.Where(t => !t.IsCompleted && t.RepeatMode != TaskRepeatMode.Once),
            TaskFilterKind.List => query.Where(t => !t.IsCompleted && string.Equals(t.ListDisplay, filter.ListName, StringComparison.OrdinalIgnoreCase)),
            _ => query.Where(t => !t.IsCompleted)
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var key = SearchText.Trim();
            query = query.Where(t => Contains(t.Content, key) || Contains(t.Notes, key) ||
                                     Contains(t.ListDisplay, key) || t.Tags.Any(tag => Contains(tag, key)) ||
                                     t.Steps.Any(step => Contains(step.Title, key)));
        }

        query = SortTasks(query, SelectedSortOption?.Mode ?? TaskSortMode.Smart);
        var result = query.ToList();
        VisibleTasks = new ObservableCollection<TaskItem>(result);
        HasVisibleTasks = result.Count > 0;
        UpdateEmptyState(filter);

        if (selectTaskId != null)
            SelectedTask = result.FirstOrDefault(t => t.Id == selectTaskId) ?? AllTasks.FirstOrDefault(t => t.Id == selectTaskId);
        else if (SelectedTask != null && AllTasks.All(t => t.Id != SelectedTask.Id))
            SelectedTask = null;
    }

    private static IEnumerable<TaskItem> SortTasks(IEnumerable<TaskItem> tasks, TaskSortMode mode)
    {
        return mode switch
        {
            TaskSortMode.DueDate => tasks.OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenByDescending(t => t.Importance),
            TaskSortMode.Priority => tasks.OrderByDescending(t => t.Importance).ThenBy(t => t.DueDate ?? DateTime.MaxValue),
            TaskSortMode.CreatedAt => tasks.OrderByDescending(t => t.CreatedAt),
            TaskSortMode.UpdatedAt => tasks.OrderByDescending(t => t.UpdatedAt),
            _ => tasks
                .OrderBy(t => t.IsCompleted)
                .ThenBy(t => t.IsPaused)
                .ThenByDescending(t => t.IsOverdue)
                .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                .ThenByDescending(t => t.Importance)
                .ThenBy(t => t.CreatedAt)
        };
    }

    private void RefreshStats(IReadOnlyList<TaskItem> tasks)
    {
        TotalCount = tasks.Count;
        OpenCount = tasks.Count(t => !t.IsCompleted);
        TodayCount = tasks.Count(t => !t.IsCompleted && t.IsDueToday);
        OverdueCount = tasks.Count(t => t.IsOverdue);
        CompletedCount = tasks.Count(t => t.IsCompleted);

        var next = tasks.Where(t => !t.IsCompleted && !t.IsPaused && t.ReminderAt.HasValue)
            .OrderBy(t => t.NextTriggerTime)
            .FirstOrDefault();
        NextReminderSummary = next == null || next.NextTriggerTime == DateTime.MaxValue
            ? "无提醒"
            : $"{next.Content} · {next.NextTriggerTime:MM-dd HH:mm}";
    }

    private void LoadEditorFromTask(TaskItem task)
    {
        _isLoadingEditor = true;
        EditTitle = task.Content;
        EditNotes = task.Notes;
        EditListName = task.ListDisplay;
        EditTagsText = string.Join(", ", task.Tags);
        EditDueDate = task.DueDate;
        EditHasReminder = task.ReminderAt.HasValue;
        EditReminderDate = task.ReminderAt?.Date ?? task.DueDate ?? DateTime.Today;
        EditReminderTime = task.ReminderAt?.ToString("HH:mm") ?? "09:00";
        EditReminderHourText = task.ReminderAt?.ToString("HH") ?? "09";
        EditReminderMinuteText = task.ReminderAt?.ToString("mm") ?? "00";
        EditRepeatMode = task.RepeatMode;
        EditCustomInterval = Math.Max(1, task.CustomInterval);
        EditCustomIntervalUnit = task.CustomIntervalUnit == "小时" ? "小时" : "天";
        EditImportance = task.Importance;
        EditAlertSound = task.AlertSound;
        EditIsPaused = task.IsPaused;
        LoadWeeklyDays(task.WeeklyDays);
        EditValidationMessage = string.Empty;
        NewStepTitle = string.Empty;
        _isLoadingEditor = false;
        RefreshEditorVisibility();
    }

    private void ClearEditor()
    {
        _isLoadingEditor = true;
        EditTitle = string.Empty;
        EditNotes = string.Empty;
        EditListName = "收件箱";
        EditTagsText = string.Empty;
        EditDueDate = null;
        EditHasReminder = false;
        EditReminderDate = DateTime.Today;
        EditReminderTime = "09:00";
        EditReminderHourText = "09";
        EditReminderMinuteText = "00";
        EditRepeatMode = TaskRepeatMode.Once;
        EditCustomInterval = 1;
        EditCustomIntervalUnit = "天";
        EditImportance = TaskImportance.Normal;
        EditAlertSound = "SystemDefault";
        EditIsPaused = false;
        EditWeeklyMon = EditWeeklyTue = EditWeeklyWed = EditWeeklyThu = EditWeeklyFri = EditWeeklySat = EditWeeklySun = false;
        EditValidationMessage = string.Empty;
        NewStepTitle = string.Empty;
        _isLoadingEditor = false;
        RefreshEditorVisibility();
    }

    private void RefreshEditorVisibility()
    {
        IsWeeklyEditorVisible = EditRepeatMode == TaskRepeatMode.Weekly;
        IsCustomEditorVisible = EditRepeatMode == TaskRepeatMode.Custom;
    }

    private DateTime? BuildQuickReminder()
    {
        var quickTime = BuildTimeText(QuickHourText, QuickMinuteText, QuickTimeText);
        if (string.IsNullOrWhiteSpace(quickTime))
            return null;

        if (!TryParseTime(quickTime, out var time))
        {
            QuickInputError = "提醒时间请选择 0-23 点、0-59 分，或输入 HH:mm";
            return null;
        }

        var date = QuickDueDate?.Date ?? DateTime.Today;
        var reminder = date.Add(time);
        if (reminder < DateTime.Now.AddMinutes(-1))
            reminder = reminder.AddDays(1);
        return reminder;
    }

    private DateTime? BuildEditorReminder()
    {
        if (!EditHasReminder)
            return null;

        var editTime = BuildTimeText(EditReminderHourText, EditReminderMinuteText, EditReminderTime);
        if (!TryParseTime(editTime, out var time))
        {
            EditValidationMessage = "提醒时间请选择 0-23 点、0-59 分，或输入 HH:mm";
            return null;
        }

        var date = (EditReminderDate ?? EditDueDate ?? DateTime.Today).Date;
        return date.Add(time);
    }

    private string BuildWeeklyDays()
    {
        var days = new List<int>();
        if (EditWeeklyMon) days.Add(1);
        if (EditWeeklyTue) days.Add(2);
        if (EditWeeklyWed) days.Add(3);
        if (EditWeeklyThu) days.Add(4);
        if (EditWeeklyFri) days.Add(5);
        if (EditWeeklySat) days.Add(6);
        if (EditWeeklySun) days.Add(7);
        return string.Join(",", days);
    }

    private void LoadWeeklyDays(string? weeklyDays)
    {
        var days = (weeklyDays ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var value) ? value : -1)
            .ToHashSet();

        EditWeeklyMon = days.Contains(1);
        EditWeeklyTue = days.Contains(2);
        EditWeeklyWed = days.Contains(3);
        EditWeeklyThu = days.Contains(4);
        EditWeeklyFri = days.Contains(5);
        EditWeeklySat = days.Contains(6);
        EditWeeklySun = days.Contains(7);
    }

    private void UpdateEmptyState(TaskFilterItem? filter)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "没有符合搜索条件的任务";
            EmptyHint = "换个关键词，或清空搜索后查看全部任务。";
            return;
        }

        EmptyTitle = filter?.Kind switch
        {
            TaskFilterKind.Today => "今天没有待办任务",
            TaskFilterKind.Upcoming => "未来 7 天没有任务",
            TaskFilterKind.Overdue => "没有逾期任务",
            TaskFilterKind.Important => "没有重要任务",
            TaskFilterKind.Paused => "没有暂停任务",
            TaskFilterKind.Completed => "还没有已完成任务",
            TaskFilterKind.List => $"“{filter.Name}”清单没有未完成任务",
            _ => TotalCount == 0 ? "还没有任务" : "当前视图没有任务"
        };

        EmptyHint = filter?.Kind == TaskFilterKind.Completed
            ? "完成任务后会出现在这里。"
            : "可以使用上方快速添加创建一个新任务。";
    }

    private static string BuildTimeText(string hourText, string minuteText, string fallbackText)
    {
        var hour = hourText?.Trim() ?? string.Empty;
        var minute = minuteText?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(hour) || !string.IsNullOrWhiteSpace(minute))
            return $"{hour}:{minute}";

        return fallbackText?.Trim() ?? string.Empty;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var normalized = text.Trim().Replace('：', ':');

        var parts = normalized.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var hour)) return false;
        if (!int.TryParse(parts[1], out var minute)) return false;
        if (hour is < 0 or > 23) return false;
        if (minute is < 0 or > 59) return false;

        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    private static IEnumerable<string> ParseTags(string text)
    {
        return (text ?? string.Empty)
            .Replace('，', ',')
            .Split(new[] { ',', ' ', ';', '；', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool Contains(string? source, string value)
        => !string.IsNullOrWhiteSpace(source) && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static TaskItem CloneTask(TaskItem source)
    {
        var clone = new TaskItem
        {
            Id = source.Id,
            Content = source.Content,
            Notes = source.Notes,
            ListName = source.ListName,
            DueDate = source.DueDate,
            ReminderAt = source.ReminderAt,
            TriggerTime = source.TriggerTime,
            RepeatMode = source.RepeatMode,
            CustomInterval = source.CustomInterval,
            CustomIntervalUnit = source.CustomIntervalUnit,
            WeeklyDays = source.WeeklyDays,
            Importance = source.Importance,
            AlertSound = source.AlertSound,
            AlertSoundPath = source.AlertSoundPath,
            IsCompleted = source.IsCompleted,
            IsPaused = source.IsPaused,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            CompletedAt = source.CompletedAt,
            LastTriggeredAt = source.LastTriggeredAt,
            NextTriggerTime = source.NextTriggerTime,
            Tags = new ObservableCollection<string>(source.Tags),
            Steps = new ObservableCollection<TaskStepItem>(source.Steps.Select(s => new TaskStepItem
            {
                Id = s.Id,
                Title = s.Title,
                IsDone = s.IsDone,
                CreatedAt = s.CreatedAt
            }))
        };
        clone.NormalizeAfterLoad();
        return clone;
    }
}
