using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Text.Json;
using Toolbox.Models;

namespace Toolbox.Services;

/// <summary>
/// 任务服务
/// 负责任务存储到 tasks.json、定时触发管理、错过触发检测
/// 使用后台计时器检测任务触发时间
/// </summary>
public class TaskManagerService : ITaskManagerService
{
    private readonly ILogService _log;
    private readonly System.Timers.Timer _checkTimer;
    private readonly string _tasksFilePath;
    private readonly List<TaskItem> _tasks = new();

    /// <summary>任务列表（只读副本）</summary>
    public IReadOnlyList<TaskItem> Tasks => _tasks.AsReadOnly();

    /// <summary>任务触发事件</summary>
    public event Action<TaskItem>? TaskTriggered;

    /// <summary>任务列表变更事件</summary>
    public event Action? TasksChanged;

    /// <summary>
    /// 构造函数
    /// 加载已保存的任务并启动定时检测
    /// </summary>
    public TaskManagerService(ILogService log)
    {
        _log = log;

        // 任务文件路径
        _tasksFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "工具箱", "tasks.json");

        // 加载已保存的任务
        LoadTasks();

        // 每秒检测一次任务触发
        _checkTimer = new System.Timers.Timer(1000);
        _checkTimer.Elapsed += (_, _) => CheckTaskTriggers();
        _checkTimer.AutoReset = true;
        _checkTimer.Start();

        _log.LogInfo($"任务服务已启动，加载了 {_tasks.Count} 个任务");
    }

    /// <summary>
    /// 从 tasks.json 加载任务
    /// </summary>
    private void LoadTasks()
    {
        try
        {
            if (!File.Exists(_tasksFilePath))
                return;

            var json = File.ReadAllText(_tasksFilePath);
            var loaded = JsonSerializer.Deserialize<List<TaskItem>>(json);
            if (loaded != null)
            {
                _tasks.Clear();
                _tasks.AddRange(loaded);

                // 更新每个任务的下次触发时间
                foreach (var task in _tasks)
                {
                    task.NextTriggerTime = task.CalculateNextTriggerTime();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError("加载任务文件失败", ex);
        }
    }

    /// <summary>
    /// 保存任务到 tasks.json
    /// </summary>
    public void SaveTasks()
    {
        try
        {
            var dir = Path.GetDirectoryName(_tasksFilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(_tasks, options);
            File.WriteAllText(_tasksFilePath, json);

            TasksChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError("保存任务文件失败", ex);
        }
    }

    /// <summary>
    /// 添加新任务（上限 200 个）
    /// </summary>
    /// <returns>添加成功返回 true，超过上限返回 false</returns>
    public bool AddTask(TaskItem task)
    {
        if (_tasks.Count >= 200)
        {
            _log.LogWarning("任务数量已达上限 200");
            return false;
        }

        task.NextTriggerTime = task.CalculateNextTriggerTime();
        _tasks.Add(task);
        SaveTasks();
        _log.LogInfo($"任务已添加: {task.Content} (ID: {task.Id})");
        return true;
    }

    /// <summary>
    /// 更新已有任务
    /// </summary>
    public void UpdateTask(TaskItem task)
    {
        var existing = _tasks.FirstOrDefault(t => t.Id == task.Id);
        if (existing == null) return;

        existing.NextTriggerTime = existing.CalculateNextTriggerTime();
        SaveTasks();
    }

    /// <summary>
    /// 删除任务
    /// </summary>
    public void DeleteTask(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return;

        _tasks.Remove(task);
        SaveTasks();
        _log.LogInfo($"任务已删除: {task.Content}");
    }

    /// <summary>
    /// 标记任务完成/取消完成
    /// </summary>
    public void ToggleComplete(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return;

        task.IsCompleted = !task.IsCompleted;
        if (task.RepeatMode != TaskRepeatMode.Once && !task.IsCompleted)
        {
            // 重新激活重复任务，计算下次触发时间
            task.NextTriggerTime = task.CalculateNextTriggerTime();
        }
        SaveTasks();
    }

    /// <summary>
    /// 标记任务暂停/恢复
    /// </summary>
    public void TogglePause(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return;

        task.IsPaused = !task.IsPaused;
        if (!task.IsPaused)
        {
            task.NextTriggerTime = task.CalculateNextTriggerTime();
        }
        SaveTasks();
    }

    /// <summary>
    /// 清除所有已完成任务
    /// </summary>
    public int ClearCompleted()
    {
        int count = _tasks.RemoveAll(t => t.IsCompleted);
        if (count > 0)
        {
            SaveTasks();
            _log.LogInfo($"已清除 {count} 个已完成任务");
        }
        return count;
    }

    /// <summary>
    /// 检测任务触发 — 每秒执行一次
    /// 处理错过触发的任务：重要→补触发，普通→跳过
    /// </summary>
    private void CheckTaskTriggers()
    {
        var now = DateTime.Now;
        var triggered = new List<TaskItem>();

        foreach (var task in _tasks)
        {
            if (task.IsCompleted || task.IsPaused) continue;
            if (task.NextTriggerTime > now) continue;

            // 任务已到触发时间
            triggered.Add(task);

            // 检测是否错过触发（超过 5 分钟）
            bool isMissed = (now - task.NextTriggerTime).TotalMinutes > 5;

            if (isMissed && task.Importance == TaskImportance.Normal)
            {
                // 普通任务错过触发 → 跳过，计算下次时间
                _log.LogWarning($"普通任务错过触发，已跳过: {task.Content}");
                task.NextTriggerTime = task.CalculateNextTriggerTime();
                SaveTasks();
            }
            else
            {
                // 重要任务 → 补触发
                task.LastTriggeredAt = now;
                if (task.RepeatMode != TaskRepeatMode.Once)
                {
                    task.NextTriggerTime = task.CalculateNextTriggerTime();
                }
                SaveTasks();

                // 触发通知
                if (isMissed)
                    _log.LogInfo($"重要任务补触发: {task.Content}");

                TaskTriggered?.Invoke(task);
            }
        }
    }

    /// <summary>
    /// 播放提示音
    /// </summary>
    public void PlayAlertSound(TaskItem task)
    {
        try
        {
            if (task.AlertSound == "Silent") return;

            if (task.AlertSound == "CustomWav" && File.Exists(task.AlertSoundPath))
            {
                var player = new SoundPlayer(task.AlertSoundPath);
                player.Play();
            }
            else
            {
                SystemSounds.Beep.Play();
            }
        }
        catch (Exception ex)
        {
            _log.LogError("播放提示音失败", ex);
        }
    }

    /// <summary>
    /// 获取 DST 夏令时偏移（处理夏令时切换）
    /// </summary>
    public static TimeSpan GetDstOffset()
    {
        var now = DateTime.Now;
        var utc = now.ToUniversalTime();
        return now - utc;
    }
}
