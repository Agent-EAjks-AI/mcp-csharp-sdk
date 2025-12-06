using System.Text.Json.Serialization;

namespace TasksServer;

/// <summary>
/// Status values for a task's execution state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskStatus>))]
public enum TaskStatus
{
    /// <summary>The request is currently being processed.</summary>
    [JsonStringEnumMemberName("working")]
    Working,

    /// <summary>The receiver needs input from the requestor.</summary>
    [JsonStringEnumMemberName("input_required")]
    InputRequired,

    /// <summary>The request completed successfully and results are available.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>The associated request did not complete successfully.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The request was cancelled before completion.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>
/// Represents a task's execution state.
/// </summary>
public sealed class TaskInfo
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; set; }

    [JsonPropertyName("status")]
    public TaskStatus Status { get; set; } = TaskStatus.Working;

    [JsonPropertyName("statusMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusMessage { get; set; }

    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; set; }

    [JsonPropertyName("lastUpdatedAt")]
    public required string LastUpdatedAt { get; set; }

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ttl { get; set; }

    [JsonPropertyName("pollInterval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PollInterval { get; set; }
}

/// <summary>
/// Parameters for tasks/get and tasks/cancel requests.
/// </summary>
public sealed class TaskIdParams
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
}

/// <summary>
/// Parameters for tasks/list request.
/// </summary>
public sealed class TaskListParams
{
    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cursor { get; set; }
}

/// <summary>
/// Result of tasks/list request.
/// </summary>
public sealed class TaskListResult
{
    [JsonPropertyName("tasks")]
    public required List<TaskInfo> Tasks { get; set; }

    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; set; }
}

/// <summary>
/// Result returned when a task-augmented request is accepted.
/// </summary>
public sealed class CreateTaskResult
{
    [JsonPropertyName("task")]
    public required TaskInfo Task { get; set; }
}

/// <summary>
/// Task parameters included in task-augmented requests.
/// </summary>
public sealed class TaskParams
{
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ttl { get; set; }
}
