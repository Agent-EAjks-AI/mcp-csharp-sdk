# TasksServer Sample

This sample demonstrates how to use the new `AddMessageFilter` API to implement **MCP Tasks** support as defined in the [MCP 2025-11-25 specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/tasks).

## Key Features

- **Capability Negotiation**: Advertises `tasks` capability via `ServerCapabilities.Experimental` so clients know task-augmented tool calls are supported.

- **Tool-Level Task Support**: Uses `AddListToolsFilter` to add `execution.taskSupport = "optional"` to tools, conforming to the MCP spec's tool-level negotiation.

- **Protocol Extension via Message Filter**: Shows how `AddMessageFilter` can intercept and handle entirely custom protocol methods (`tasks/get`, `tasks/result`, `tasks/cancel`, `tasks/list`) without modifying the SDK core.

- **Task-Augmented Tool Execution**: The `LongRunningAnalysis` tool demonstrates how a tool can detect the `task` parameter and return a `CreateTaskResult` immediately while processing continues in the background.

- **Concurrent Task Management**: `TaskManager` provides thread-safe task lifecycle management with proper status transitions per the spec.

## Running the Sample

```bash
cd samples/TasksServer
dotnet run
```

The server will start on `http://localhost:5000` (or the configured port).

## How It Works

### Capability Negotiation (Initialize Response)

The server advertises task support via `ServerCapabilities.Experimental` (since the SDK doesn't have a `Tasks` property yet):

```csharp
builder.Services.Configure<McpServerOptions>(options =>
{
    options.Capabilities ??= new();
    options.Capabilities.Experimental ??= new Dictionary<string, object>();

    // Advertise tasks capability per MCP 2025-11-25 spec
    options.Capabilities.Experimental["tasks"] = new JsonObject
    {
        ["list"] = new JsonObject(),
        ["cancel"] = new JsonObject(),
        ["requests"] = new JsonObject
        {
            ["tools"] = new JsonObject
            {
                ["call"] = new JsonObject()
            }
        }
    };
});
```

This tells clients that:
- `tasks/list` and `tasks/cancel` are supported
- `tools/call` requests can be augmented with tasks

### Tool-Level Negotiation (List Tools Response)

Per the spec, tools declare support for tasks via `execution.taskSupport`. We use `AddListToolsFilter` to add this to the response:

```csharp
.AddListToolsFilter(next => async (context, cancellationToken) =>
{
    var result = await next(context, cancellationToken);

    foreach (var tool in result.Tools)
    {
        if (taskSupportedTools.Contains(tool.Name))
        {
            // Add execution.taskSupport = "optional" per spec
            tool.Meta ??= new JsonObject();
            tool.Meta["execution"] = new JsonObject
            {
                ["taskSupport"] = "optional"
            };
        }
    }

    return result;
})
```

### Message Filter Pattern

The power of `AddMessageFilter` is shown in [Program.cs](Program.cs):

```csharp
.AddMessageFilter(next => async (context, cancellationToken) =>
{
    if (context.JsonRpcMessage is JsonRpcRequest request)
    {
        var response = request.Method switch
        {
            "tasks/get" => HandleTasksGet(context, request),
            "tasks/result" => await HandleTasksResultAsync(context, request, cancellationToken),
            "tasks/cancel" => HandleTasksCancel(context, request),
            "tasks/list" => HandleTasksList(context, request),
            _ => null
        };

        if (response is not null)
        {
            await context.Server.SendMessageAsync(response, cancellationToken);
            return; // Don't call next - we handled it
        }
    }

    await next(context, cancellationToken); // Continue to default handlers
});
```

This pattern allows complete control over message routing while composing with the SDK's default handlers.

### Task-Augmented Tool Pattern

The `LongRunningAnalysis` tool in [AnalysisTools.cs](AnalysisTools.cs) shows the task-augmented execution pattern:

```csharp
[McpServerTool]
public async Task<JsonNode> LongRunningAnalysis(
    string data,
    int durationSeconds = 5,
    JsonElement? task = null, // Presence indicates task-augmented request
    CancellationToken cancellationToken = default)
{
    if (task is not null)
    {
        var taskInfo = taskManager.CreateTask(...);
        _ = Task.Run(async () => { /* Background work */ });
        return JsonSerializer.SerializeToNode(new CreateTaskResult { Task = taskInfo })!;
    }

    // Synchronous execution
    await Task.Delay(durationSeconds * 1000, cancellationToken);
    return JsonValue.Create($"Analysis complete for: {data}")!;
}
```

## SDK API Changes Needed for First-Class Tasks Support

While this sample demonstrates that Tasks can be implemented today using `AddMessageFilter`, the following SDK changes would improve the developer experience:

### 1. Tool.Execution Property

The MCP spec defines `execution.taskSupport` on tools. The SDK's `Tool` class needs an `Execution` property:

```csharp
// In ModelContextProtocol.Core/Protocol/Tool.cs
public sealed class ToolExecution
{
    [JsonPropertyName("taskSupport")]
    public string? TaskSupport { get; set; } // "required" | "optional" | "forbidden"
}

public sealed class Tool
{
    // Add:
    [JsonPropertyName("execution")]
    public ToolExecution? Execution { get; set; }
}
```

### 2. ServerCapabilities.Tasks Property

Per the spec, servers must advertise task support in capabilities:

```csharp
// In ModelContextProtocol.Core/Protocol/ServerCapabilities.cs
public sealed class TasksCapability
{
    [JsonPropertyName("list")]
    public EmptyObject? List { get; set; }

    [JsonPropertyName("cancel")]
    public EmptyObject? Cancel { get; set; }

    [JsonPropertyName("requests")]
    public TaskRequestsCapability? Requests { get; set; }
}

public sealed class TaskRequestsCapability
{
    [JsonPropertyName("tools")]
    public TaskToolsCapability? Tools { get; set; }
}

public sealed class TaskToolsCapability
{
    [JsonPropertyName("call")]
    public EmptyObject? Call { get; set; }
}

public sealed class ServerCapabilities
{
    // Add:
    [JsonPropertyName("tasks")]
    public TasksCapability? Tasks { get; set; }
}
```

### 3. Built-in Tasks Method Constants

Add to `RequestMethods`:

```csharp
public const string TasksGet = "tasks/get";
public const string TasksResult = "tasks/result";
public const string TasksCancel = "tasks/cancel";
public const string TasksList = "tasks/list";
```

### 4. McpServerTool Task Support Attribute

A declarative way to mark tools as supporting tasks:

```csharp
[AttributeUsage(AttributeTargets.Method)]
public class McpServerToolTaskSupportAttribute : Attribute
{
    public TaskSupport TaskSupport { get; }
    public McpServerToolTaskSupportAttribute(TaskSupport support) => TaskSupport = support;
}

public enum TaskSupport { Forbidden, Optional, Required }

// Usage:
[McpServerTool, McpServerToolTaskSupport(TaskSupport.Optional)]
public Task<JsonNode> LongRunningAnalysis(...) { ... }
```

### 5. Built-in Task Protocol Types

Move `TaskInfo`, `TaskStatus`, `TaskParams`, etc. from this sample to `ModelContextProtocol.Core/Protocol/`.

### 6. Integration with McpServerFilters

Add dedicated task filters similar to tool/prompt/resource filters:

```csharp
public sealed class McpServerFilters
{
    // Add:
    public List<McpRequestFilter<TaskIdParams, TaskInfo>> GetTaskFilters { get; } = [];
    public List<McpRequestFilter<TaskIdParams, JsonNode>> GetTaskResultFilters { get; } = [];
    public List<McpRequestFilter<TaskIdParams, TaskInfo>> CancelTaskFilters { get; } = [];
    public List<McpRequestFilter<TaskListParams, TaskListResult>> ListTasksFilters { get; } = [];
}
```

## Testing with MCP Inspector

You can test this server using the [MCP Inspector](https://github.com/modelcontextprotocol/inspector):

1. Start the server: `dotnet run`
2. Connect with the inspector
3. Call `LongRunningAnalysis` with `task: { "ttl": 60000 }` parameter
4. Use `tasks/get` and `tasks/result` to poll for completion

## License

MIT - See repository root for full license.
