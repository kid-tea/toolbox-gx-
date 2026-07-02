using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 任务 ViewModel
/// 管理任务 CRUD、勾选框完成状态、重要性/时间排序、悬浮面板控制
/// </summary>
public partial class TasksViewModel : ViewModelBase
{
    private readonly ITaskManagerService _taskService;
    private readonly ILogService _log;

    // ==================== 任务列表 ====================

    /// <summary>所有任务的显示列表</summary>
    [ObservableProperty]
    private ObservableCollection<TaskItem> _tasks = new();

    /// <summary>任务总数</summary>
    [ObservableProperty]
    private int _taskCount;

    /// <summary>是否按重要性排序（true=按重要性，false=按时间）</summary>
    [ObservableProperty]
    private bool _sortByImportance;

    // ==================== 新建任务表单 ====================

    /// <summary>新任务内容</summary>
    [ObservableProperty]
    private string _newTaskContent = "";

    /// <summary>新任务日期</summary>
    [ObservableProperty]
    private DateTime _newTaskDate = DateTime.Today;

    /// <summary>新任务小时</summary>
    [ObservableProperty]
    private string _newTaskHour = DateTime.Now.AddMinutes(5).Hour.ToString("D2");

    /// <summary>新任务分钟</summary>
    [ObservableProperty]
    private string _newTaskMinute = DateTime.Now.AddMinutes(5).Minute.ToString("D2");

    /// <summary>新任务秒</summary>
    [ObservableProperty]
    private string _newTaskSecond = DateTime.Now.AddMinutes(5).Second.ToString("D2");

    /// <summary>新任务触发时间</summary>
    [ObservableProperty]
    private DateTime _newTaskTime = DateTime.Now.AddMinutes(5);

    /// <summary>新任务重复模式</summary>
    [ObservableProperty]
    private TaskRepeatMode _newTaskRepeatMode = TaskRepeatMode.Once;

    /// <summary>新任务自定义间隔</summary>
    [ObservableProperty]
    private int _newTaskCustomInterval = 1;

    /// <summary>新任务自定义间隔单位</summary>
    [ObservableProperty]
    private string _newTaskCustomIntervalUnit = "天";

    /// <summary>新任务重要性</summary>
    [ObservableProperty]
    private TaskImportance _newTaskImportance = TaskImportance.Normal;

    /// <summary>新任务提示音</summary>
    [ObservableProperty]
    private string _newTaskAlertSound = "SystemDefault";

    /// <summary>每周重复：星期一</summary>
    [ObservableProperty]
    private bool _weeklyMon;

    /// <summary>每周重复：星期二</summary>
    [ObservableProperty]
    private bool _weeklyTue;

    /// <summary>每周重复：星期三</summary>
    [ObservableProperty]
    private bool _weeklyWed;

    /// <summary>每周重复：星期四</summary>
    [ObservableProperty]
    private bool _weeklyThu;

    /// <summary>每周重复：星期五</summary>
    [ObservableProperty]
    private bool _weeklyFri;

    /// <summary>每周重复：星期六</summary>
    [ObservableProperty]
    private bool _weeklySat;

    /// <summary>每周重复：星期日</summary>
    [ObservableProperty]
    private bool _weeklySun;

    /// <summary>自定义间隔是否合法</summary>
    [ObservableProperty]
    private bool _isCustomIntervalValid = true;

    /// <summary>自定义间隔错误提示</summary>
    [ObservableProperty]
    private string _customIntervalError = "";

    // ==================== 悬浮面板状态 ====================

    /// <summary>悬浮面板是否可见</summary>
    [ObservableProperty]
    private bool _isFloatingPanelOpen;

    /// <summary>悬浮面板是否置顶</summary>
    [ObservableProperty]
    private bool _floatingPanelTopmost = true;

    /// <summary>悬浮面板透明度</summary>
    [ObservableProperty]
    private double _floatingPanelOpacity = 0.9;

    /// <summary>防抖锁</summary>
    private bool _isProcessingClick;

    /// <summary>
    /// 构造函数 — 依赖注入
    /// </summary>
    public TasksViewModel(ITaskManagerService taskService, ILogService log)
    {
        _taskService = taskService;
        _log = log;

        // 加载已有任务
        RefreshTasks();

        // 监听任务触发
        _taskService.TaskTriggered += OnTaskTriggered;
        _taskService.TasksChanged += RefreshTasks;

        StatusMessage = "就绪 — 创建新任务或管理已有任务";
    }

    /// <summary>
    /// 刷新任务列表
    /// 已完成任务沉底，暂停任务显示虚线样式
    /// </summary>
    private void RefreshTasks()
    {
        var allTasks = _taskService.Tasks;

        // 排序逻辑
        IEnumerable<TaskItem> sorted;
        if (SortByImportance)
        {
            // 按重要性排序：重要 > 普通，已完成沉底
            sorted = allTasks
                .OrderBy(t => t.IsCompleted)
                .ThenByDescending(t => t.Importance == TaskImportance.Important)
                .ThenBy(t => t.NextTriggerTime);
        }
        else
        {
            // 按时间排序：未完成按时间升序，已完成沉底
            sorted = allTasks
                .OrderBy(t => t.IsCompleted)
                .ThenBy(t => t.NextTriggerTime);
        }

        Tasks = new ObservableCollection<TaskItem>(sorted);
        TaskCount = Tasks.Count;
    }

    // ==================== 新建任务 ====================

    /// <summary>
    /// 添加新任务（带防抖）
    /// </summary>
    [RelayCommand]
    private void AddTask()
    {
        if (_isProcessingClick) return;

        try
        {
            _isProcessingClick = true;

            if (string.IsNullOrWhiteSpace(NewTaskContent))
            {
                StatusMessage = "请输入任务内容";
                return;
            }

            // 检查上限
            if (_taskService.Tasks.Count >= 200)
            {
                StatusMessage = "任务数量已达上限（200），无法添加新任务";
                return;
            }

            // 组合日期 + 小时:分钟:秒
            if (!int.TryParse(NewTaskHour, out var hour) || hour < 0 || hour > 23)
            {
                StatusMessage = "小时必须在 00-23 之间";
                return;
            }
            if (!int.TryParse(NewTaskMinute, out var minute) || minute < 0 || minute > 59)
            {
                StatusMessage = "分钟必须在 00-59 之间";
                return;
            }
            if (!int.TryParse(NewTaskSecond, out var second) || second < 0 || second > 59)
            {
                StatusMessage = "秒钟必须在 00-59 之间";
                return;
            }

            var triggerTime = new DateTime(NewTaskDate.Year, NewTaskDate.Month, NewTaskDate.Day, hour, minute, second);

            // 构建星期几字符串
            var weeklyDays = new List<int>();
            if (WeeklyMon) weeklyDays.Add(1);
            if (WeeklyTue) weeklyDays.Add(2);
            if (WeeklyWed) weeklyDays.Add(3);
            if (WeeklyThu) weeklyDays.Add(4);
            if (WeeklyFri) weeklyDays.Add(5);
            if (WeeklySat) weeklyDays.Add(6);
            if (WeeklySun) weeklyDays.Add(7);

            var task = new TaskItem
            {
                Content = NewTaskContent,
                TriggerTime = triggerTime,
                RepeatMode = NewTaskRepeatMode,
                CustomInterval = NewTaskCustomInterval,
                CustomIntervalUnit = NewTaskCustomIntervalUnit,
                WeeklyDays = string.Join(",", weeklyDays),
                Importance = NewTaskImportance,
                AlertSound = NewTaskAlertSound
            };

            if (!_taskService.AddTask(task))
            {
                StatusMessage = "添加任务失败，已达上限";
                return;
            }

            // 重置表单
            NewTaskContent = "";
            NewTaskDate = DateTime.Today;
            var resetTime = DateTime.Now.AddMinutes(5);
            NewTaskHour = resetTime.Hour.ToString("D2");
            NewTaskMinute = resetTime.Minute.ToString("D2");
            NewTaskSecond = resetTime.Second.ToString("D2");
            NewTaskTime = resetTime;
            NewTaskRepeatMode = TaskRepeatMode.Once;
            NewTaskCustomInterval = 1;
            NewTaskCustomIntervalUnit = "天";
            NewTaskImportance = TaskImportance.Normal;
            NewTaskAlertSound = "SystemDefault";
            WeeklyMon = WeeklyTue = WeeklyWed = WeeklyThu = WeeklyFri = WeeklySat = WeeklySun = false;

            RefreshTasks();
            StatusMessage = $"任务已添加: {task.Content}";
            _log.LogInfo($"新任务已创建: {task.Id}");
        }
        finally
        {
            _isProcessingClick = false;
        }
    }

    // ==================== 任务操作 ====================

    /// <summary>
    /// 切换任务完成状态
    /// </summary>
    [RelayCommand]
    private void ToggleTaskComplete(TaskItem? task)
    {
        if (task == null) return;
        _taskService.ToggleComplete(task.Id);
        RefreshTasks();
        StatusMessage = task.IsCompleted ? $"任务已完成: {task.Content}" : $"任务已重新激活: {task.Content}";
    }

    /// <summary>
    /// 切换任务暂停状态
    /// </summary>
    [RelayCommand]
    private void ToggleTaskPause(TaskItem? task)
    {
        if (task == null) return;
        _taskService.TogglePause(task.Id);
        RefreshTasks();
        StatusMessage = task.IsPaused ? $"任务已暂停: {task.Content}" : $"任务已恢复: {task.Content}";
    }

    /// <summary>
    /// 删除任务
    /// </summary>
    [RelayCommand]
    private void DeleteTask(TaskItem? task)
    {
        if (task == null) return;
        _taskService.DeleteTask(task.Id);
        RefreshTasks();
        StatusMessage = $"任务已删除: {task.Content}";
    }

    /// <summary>
    /// 一键清除所有已完成任务
    /// </summary>
    [RelayCommand]
    private void ClearCompletedTasks()
    {
        int count = _taskService.ClearCompleted();
        RefreshTasks();
        StatusMessage = count > 0 ? $"已清除 {count} 个已完成任务" : "没有已完成的任务可清除";
    }

    /// <summary>
    /// 切换排序方式
    /// </summary>
    [RelayCommand]
    private void ToggleSort()
    {
        SortByImportance = !SortByImportance;
        RefreshTasks();
        StatusMessage = SortByImportance ? "按重要性排序" : "按时间排序";
    }

    // ==================== 悬浮面板 ====================

    /// <summary>
    /// 打开/关闭悬浮面板
    /// </summary>
    [RelayCommand]
    private void ToggleFloatingPanel()
    {
        IsFloatingPanelOpen = !IsFloatingPanelOpen;
        StatusMessage = IsFloatingPanelOpen ? "悬浮面板已打开" : "悬浮面板已关闭";
    }

    /// <summary>
    /// 自定义间隔验证
    /// </summary>
    partial void OnNewTaskCustomIntervalChanged(int value)
    {
        if (NewTaskRepeatMode == TaskRepeatMode.Custom)
        {
            if (value < 1)
            {
                IsCustomIntervalValid = false;
                CustomIntervalError = "间隔必须大于 0";
            }
            else if ((NewTaskCustomIntervalUnit == "天" && value > 365) || (NewTaskCustomIntervalUnit == "小时" && value > 8760))
            {
                IsCustomIntervalValid = false;
                CustomIntervalError = NewTaskCustomIntervalUnit == "天" ? "天数不能超过 365" : "小时不能超过 8760";
            }
            else
            {
                IsCustomIntervalValid = true;
                CustomIntervalError = "";
            }
        }
    }

    partial void OnNewTaskCustomIntervalUnitChanged(string value)
    {
        OnNewTaskCustomIntervalChanged(NewTaskCustomInterval);
    }

    partial void OnSortByImportanceChanged(bool value)
    {
        StatusMessage = value ? "当前按重要性排序" : "当前按时间排序";
        RefreshTasks();
    }

    /// <summary>
    /// 任务触发回调
    /// 重要任务需要循环提示音
    /// </summary>
    private void OnTaskTriggered(TaskItem task)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _taskService.PlayAlertSound(task);
            StatusMessage = $"任务触发: {task.Content}";
            RefreshTasks();
        });
    }
}
