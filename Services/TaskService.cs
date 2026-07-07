using System.IO;
using System.Media;
using System.Text.Encodings.Web;
using System.Text.Json;
using Toolbox.Models;

namespace Toolbox.Services;

/// <summary>
/// 本地任务服务。
/// 数据保存在 %AppData%\Toolbox\tasks.json，并兼容读取旧版 %AppData%\工具箱\tasks.json。
/// </summary>
public sealed class TaskManagerService : ITaskManagerService, IDisposable
{
    private const int MaxTaskCount = 1000;
    private readonly ILogService _log;
    private readonly System.Timers.Timer _checkTimer;
    private readonly object _sync = new();
    private readonly List<TaskItem> _tasks = new();
    private readonly string? _legacyTasksFilePath;

    public IReadOnlyList<TaskItem> Tasks
    {
        get
        {
            lock (_sync)
                return _tasks.ToList();
        }
    }

    public string TasksFilePath { get; }

    public event Action<TaskItem>? TaskTriggered;
    public event Action? TasksChanged;

    public TaskManagerService(ILogService log)
        : this(log, null)
    {
    }

    public TaskManagerService(ILogService log, string? tasksFilePath)
    {
        _log = log;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        TasksFilePath = tasksFilePath ?? Path.Combine(appData, "Toolbox", "tasks.json");
        _legacyTasksFilePath = tasksFilePath == null ? Path.Combine(appData, "工具箱", "tasks.json") : null;

        LoadTasks();

        _checkTimer = new System.Timers.Timer(15_000);
        _checkTimer.Elapsed += (_, _) => CheckTaskTriggers();
        _checkTimer.AutoReset = true;
        _checkTimer.Start();

        _log.LogInfo($"任务服务已启动，加载 {_tasks.Count} 个任务，数据文件：{TasksFilePath}");
    }

    public bool AddTask(TaskItem task)
    {
        if (task == null) return false;

        lock (_sync)
        {
            if (_tasks.Count >= MaxTaskCount)
            {
                _log.LogWarning($"任务数量已达到上限 {MaxTaskCount}");
                return false;
            }

            PrepareTask(task, isNew: true);
            _tasks.Add(task);
        }

        SaveTasks();
        _log.LogInfo($"任务已添加：{task.Content}");
        return true;
    }

    public void UpdateTask(TaskItem task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.Id)) return;

        lock (_sync)
        {
            var index = _tasks.FindIndex(t => t.Id == task.Id);
            if (index < 0) return;

            PrepareTask(task, isNew: false);
            if (!ReferenceEquals(_tasks[index], task))
                _tasks[index] = task;
        }

        SaveTasks();
    }

    public void DeleteTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        TaskItem? removed = null;
        lock (_sync)
        {
            removed = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (removed != null)
                _tasks.Remove(removed);
        }

        if (removed == null) return;
        SaveTasks();
        _log.LogInfo($"任务已删除：{removed.Content}");
    }

    public void ToggleComplete(string taskId)
    {
        TaskItem? task;
        lock (_sync)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            task.IsCompleted = !task.IsCompleted;
            task.CompletedAt = task.IsCompleted ? DateTime.Now : null;
            task.MarkUpdated();
        }

        SaveTasks();
    }

    public void TogglePause(string taskId)
    {
        lock (_sync)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            task.IsPaused = !task.IsPaused;
            task.MarkUpdated();
        }

        SaveTasks();
    }

    public int ClearCompleted()
    {
        int count;
        lock (_sync)
            count = _tasks.RemoveAll(t => t.IsCompleted);

        if (count > 0)
        {
            SaveTasks();
            _log.LogInfo($"已清除 {count} 个已完成任务");
        }

        return count;
    }

    public void SaveTasks()
    {
        try
        {
            List<TaskItem> snapshot;
            lock (_sync)
                snapshot = _tasks.ToList();

            var dir = Path.GetDirectoryName(TasksFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            File.WriteAllText(TasksFilePath, JsonSerializer.Serialize(snapshot, options));
            TasksChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError("保存任务文件失败", ex);
        }
    }

    public void PlayAlertSound(TaskItem task)
    {
        try
        {
            if (task.AlertSound == "Silent") return;

            if (task.AlertSound == "CustomWav" && File.Exists(task.AlertSoundPath))
            {
                using var player = new SoundPlayer(task.AlertSoundPath);
                player.Play();
                return;
            }

            SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            _log.LogError("播放任务提醒音失败", ex);
        }
    }

    public void Dispose()
    {
        _checkTimer.Stop();
        _checkTimer.Dispose();
    }

    private void LoadTasks()
    {
        try
        {
            var path = File.Exists(TasksFilePath) ? TasksFilePath :
                (!string.IsNullOrWhiteSpace(_legacyTasksFilePath) && File.Exists(_legacyTasksFilePath) ? _legacyTasksFilePath : null);

            if (path == null) return;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<TaskItem>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<TaskItem>();

            lock (_sync)
            {
                _tasks.Clear();
                foreach (var task in loaded.Where(t => t != null))
                {
                    PrepareTask(task, isNew: false);
                    _tasks.Add(task);
                }
            }

            if (!string.Equals(path, TasksFilePath, StringComparison.OrdinalIgnoreCase))
                SaveTasks();
        }
        catch (Exception ex)
        {
            _log.LogError("加载任务文件失败", ex);
        }
    }

    private void CheckTaskTriggers()
    {
        var now = DateTime.Now;
        var triggered = new List<TaskItem>();
        var changed = false;

        lock (_sync)
        {
            foreach (var task in _tasks)
            {
                if (task.IsCompleted || task.IsPaused || !task.ReminderAt.HasValue)
                    continue;

                var dueReminder = task.CalculateNextReminderTime(now.AddMilliseconds(-1));
                if (!dueReminder.HasValue || dueReminder.Value > now)
                    continue;

                if (task.LastTriggeredAt.HasValue && task.LastTriggeredAt.Value >= dueReminder.Value)
                    continue;

                task.LastTriggeredAt = now;
                triggered.Add(task);

                if (task.RepeatMode != TaskRepeatMode.Once)
                {
                    task.ReminderAt = task.CalculateNextReminderTime(now);
                    task.RecalculateNextTriggerTime();
                }

                task.UpdatedAt = now;
                changed = true;
            }
        }

        foreach (var task in triggered)
        {
            _log.LogInfo($"任务提醒触发：{task.Content}");
            PlayAlertSound(task);
            TaskTriggered?.Invoke(task);
        }

        if (changed)
            SaveTasks();
    }

    private static void PrepareTask(TaskItem task, bool isNew)
    {
        task.NormalizeAfterLoad();
        if (isNew)
        {
            task.CreatedAt = DateTime.Now;
            task.LastTriggeredAt = null;
        }

        task.MarkUpdated();
    }
}
