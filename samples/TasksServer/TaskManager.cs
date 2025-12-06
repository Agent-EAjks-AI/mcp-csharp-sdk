using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace TasksServer;

/// <summary>
/// Thread-safe manager for task state and results.
/// Demonstrates task lifecycle management per MCP Tasks specification.
/// </summary>
public sealed class TaskManager
{
    private readonly ConcurrentDictionary<string, ManagedTask> _tasks = new();
    private readonly TimeProvider _timeProvider;

    public TaskManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a new task and returns its initial state.
    /// </summary>
    public TaskInfo CreateTask(long? requestedTtl = null, long pollInterval = 1000)
    {
        var taskId = Guid.NewGuid().ToString();
        var now = _timeProvider.GetUtcNow();
        var isoNow = now.ToString("O");

        var task = new ManagedTask
        {
            Info = new TaskInfo
            {
                TaskId = taskId,
                Status = TaskStatus.Working,
                CreatedAt = isoNow,
                LastUpdatedAt = isoNow,
                Ttl = requestedTtl ?? 60_000, // Default 60 seconds
                PollInterval = pollInterval
            },
            ResultCompletion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        _tasks[taskId] = task;
        return task.Info;
    }

    /// <summary>
    /// Gets the current state of a task.
    /// </summary>
    public TaskInfo? GetTask(string taskId)
    {
        return _tasks.TryGetValue(taskId, out var task) ? CloneTaskInfo(task.Info) : null;
    }

    /// <summary>
    /// Updates the status of a task.
    /// </summary>
    public bool UpdateStatus(string taskId, TaskStatus status, string? statusMessage = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return false;

        lock (task)
        {
            task.Info.Status = status;
            task.Info.StatusMessage = statusMessage;
            task.Info.LastUpdatedAt = _timeProvider.GetUtcNow().ToString("O");
        }
        return true;
    }

    /// <summary>
    /// Completes a task with a result.
    /// </summary>
    public void CompleteTask(string taskId, JsonNode? result, bool isError = false)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return;

        lock (task)
        {
            task.Info.Status = isError ? TaskStatus.Failed : TaskStatus.Completed;
            task.Info.LastUpdatedAt = _timeProvider.GetUtcNow().ToString("O");
            task.Result = result;
            task.IsError = isError;
            task.ResultCompletion.TrySetResult(result);
        }
    }

    /// <summary>
    /// Cancels a task.
    /// </summary>
    public TaskInfo? CancelTask(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        lock (task)
        {
            // Cannot cancel terminal tasks
            if (task.Info.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled)
                return null;

            task.Info.Status = TaskStatus.Cancelled;
            task.Info.StatusMessage = "Task was cancelled by request.";
            task.Info.LastUpdatedAt = _timeProvider.GetUtcNow().ToString("O");
            task.ResultCompletion.TrySetCanceled();
        }
        return CloneTaskInfo(task.Info);
    }

    /// <summary>
    /// Waits for a task to complete and returns its result.
    /// </summary>
    public async Task<(JsonNode? Result, bool IsError)?> GetResultAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        try
        {
            await task.ResultCompletion.Task.WaitAsync(cancellationToken);
            return (task.Result, task.IsError);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all tasks (with pagination support).
    /// </summary>
    public TaskListResult ListTasks(string? cursor = null, int pageSize = 100)
    {
        var allTasks = _tasks.Values
            .Select(t => CloneTaskInfo(t.Info))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        int startIndex = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var idx))
            startIndex = idx;

        var page = allTasks.Skip(startIndex).Take(pageSize).ToList();
        var nextCursor = startIndex + page.Count < allTasks.Count
            ? (startIndex + page.Count).ToString()
            : null;

        return new TaskListResult { Tasks = page, NextCursor = nextCursor };
    }

    private static TaskInfo CloneTaskInfo(TaskInfo info)
    {
        lock (info)
        {
            return new TaskInfo
            {
                TaskId = info.TaskId,
                Status = info.Status,
                StatusMessage = info.StatusMessage,
                CreatedAt = info.CreatedAt,
                LastUpdatedAt = info.LastUpdatedAt,
                Ttl = info.Ttl,
                PollInterval = info.PollInterval
            };
        }
    }

    private sealed class ManagedTask
    {
        public required TaskInfo Info { get; init; }
        public required TaskCompletionSource<JsonNode?> ResultCompletion { get; init; }
        public JsonNode? Result { get; set; }
        public bool IsError { get; set; }
    }
}
