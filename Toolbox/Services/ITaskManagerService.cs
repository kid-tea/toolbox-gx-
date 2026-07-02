using Toolbox.Models;

namespace Toolbox.Services;

/// <summary>
/// 任务管理服务接口
/// 负责任务存储、增删改查、触发检测、提示音播放
/// </summary>
public interface ITaskManagerService
{
    /// <summary>任务列表（只读副本）</summary>
    IReadOnlyList<TaskItem> Tasks { get; }

    /// <summary>任务触发事件</summary>
    event Action<TaskItem>? TaskTriggered;

    /// <summary>任务列表变更事件</summary>
    event Action? TasksChanged;

    /// <summary>保存任务到 tasks.json</summary>
    void SaveTasks();

    /// <summary>添加新任务（上限 200 个）</summary>
    bool AddTask(TaskItem task);

    /// <summary>更新已有任务</summary>
    void UpdateTask(TaskItem task);

    /// <summary>删除任务</summary>
    void DeleteTask(string taskId);

    /// <summary>标记任务完成/取消完成</summary>
    void ToggleComplete(string taskId);

    /// <summary>标记任务暂停/恢复</summary>
    void TogglePause(string taskId);

    /// <summary>清除所有已完成任务</summary>
    int ClearCompleted();

    /// <summary>播放提示音</summary>
    void PlayAlertSound(TaskItem task);
}
