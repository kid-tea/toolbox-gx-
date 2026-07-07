using Toolbox.Models;

namespace Toolbox.Services;

/// <summary>
/// 本地任务管理服务：负责 JSON 持久化、任务 CRUD、提醒触发和提示音。
/// </summary>
public interface ITaskManagerService
{
    IReadOnlyList<TaskItem> Tasks { get; }

    event Action<TaskItem>? TaskTriggered;
    event Action? TasksChanged;

    string TasksFilePath { get; }

    void SaveTasks();
    bool AddTask(TaskItem task);
    void UpdateTask(TaskItem task);
    void DeleteTask(string taskId);
    void ToggleComplete(string taskId);
    void TogglePause(string taskId);
    int ClearCompleted();
    void PlayAlertSound(TaskItem task);
}
