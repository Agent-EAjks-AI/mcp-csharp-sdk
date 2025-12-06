using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TasksServer;

/// <summary>
/// Sample tools demonstrating task-augmented execution.
/// The LongRunningAnalysis tool uses execution.taskSupport = "optional" pattern.
/// </summary>
[McpServerToolType]
public sealed class AnalysisTools(TaskManager taskManager)
{
    /// <summary>
    /// A long-running analysis tool that can be invoked as a task.
    /// When invoked with a "task" field in params, returns a TaskInfo immediately.
    /// Otherwise, runs synchronously.
    /// </summary>
    [McpServerTool, Description("Performs a long-running data analysis. Supports task-augmented execution.")]
    public async Task<JsonNode> LongRunningAnalysis(
        [Description("The data to analyze")] string data,
        [Description("Analysis duration in seconds (1-30)")] int durationSeconds = 5,
        JsonElement? task = null, // Task params from request (indicates task-augmented execution)
        CancellationToken cancellationToken = default)
    {
        durationSeconds = Math.Clamp(durationSeconds, 1, 30);

        // If task params provided, execute as a background task
        if (task is not null)
        {
            var taskParams = task.Value.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<TaskParams>(task.Value)
                : null;

            var taskInfo = taskManager.CreateTask(taskParams?.Ttl, pollInterval: 1000);

            // Start background work
            _ = Task.Run(async () =>
            {
                try
                {
                    // Simulate long-running work
                    for (int i = 1; i <= durationSeconds; i++)
                    {
                        await Task.Delay(1000, CancellationToken.None);
                        taskManager.UpdateStatus(taskInfo.TaskId, TaskStatus.Working,
                            $"Processing... {i}/{durationSeconds} seconds elapsed");
                    }

                    var result = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = $"Analysis complete for: {data}\nDuration: {durationSeconds}s\nResult: Data processed successfully with 42 insights found."
                            }
                        },
                        ["isError"] = false
                    };

                    taskManager.CompleteTask(taskInfo.TaskId, result);
                }
                catch (Exception ex)
                {
                    var errorResult = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = $"Analysis failed: {ex.Message}"
                            }
                        },
                        ["isError"] = true
                    };
                    taskManager.CompleteTask(taskInfo.TaskId, errorResult, isError: true);
                }
            });

            // Return CreateTaskResult immediately
            return JsonSerializer.SerializeToNode(new CreateTaskResult { Task = taskInfo })!;
        }

        // Synchronous execution (no task augmentation)
        await Task.Delay(durationSeconds * 1000, cancellationToken);
        return JsonValue.Create($"Analysis complete for: {data}\nDuration: {durationSeconds}s\nResult: Data processed successfully with 42 insights found.")!;
    }

    /// <summary>
    /// A simple synchronous tool for comparison.
    /// </summary>
    [McpServerTool, Description("Returns server status immediately.")]
    public static string GetStatus() => "Server is running. Task support enabled.";
}
