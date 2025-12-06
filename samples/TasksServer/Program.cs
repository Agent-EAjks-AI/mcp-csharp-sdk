// MCP Tasks Sample Server
// Demonstrates using AddMessageFilter to implement MCP Tasks (2025-11-25 spec)
// See: https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/tasks

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TasksServer;

var builder = WebApplication.CreateBuilder(args);

// Register TaskManager as a singleton
builder.Services.AddSingleton<TaskManager>();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<AnalysisTools>()
    // Use AddMessageFilter to intercept all tasks/* methods
    // This demonstrates the power of AddMessageFilter: handling entirely custom
    // protocol extensions without modifying the SDK core
    .AddMessageFilter(next => async (context, cancellationToken) =>
    {
        // Only intercept JSON-RPC requests for tasks/* methods
        if (context.JsonRpcMessage is not JsonRpcRequest request)
        {
            await next(context, cancellationToken);
            return;
        }

        // Route tasks/* methods to our handler
        var response = request.Method switch
        {
            "tasks/get" => HandleTasksGet(context, request),
            "tasks/result" => await HandleTasksResultAsync(context, request, cancellationToken),
            "tasks/cancel" => HandleTasksCancel(context, request),
            "tasks/list" => HandleTasksList(context, request),
            _ => null // Not a tasks/* method, continue to default handler
        };

        if (response is not null)
        {
            // Send response and don't call next (we handled it)
            await context.Server.SendMessageAsync(response, cancellationToken);
            return;
        }

        // Not a tasks/* method, continue to default handler
        await next(context, cancellationToken);
    });

var app = builder.Build();

app.MapMcp();

app.Run();

// ============================================================================
// Tasks method handlers - Pure functions that produce JsonRpcMessages
// ============================================================================

static JsonRpcMessage HandleTasksGet(MessageContext context, JsonRpcRequest request)
{
    var taskManager = context.Services!.GetRequiredService<TaskManager>();
    var taskParams = Deserialize<TaskIdParams>(request.Params);

    if (taskParams?.TaskId is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams, "Missing taskId parameter");
    }

    var taskInfo = taskManager.GetTask(taskParams.TaskId);
    if (taskInfo is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams, "Task not found");
    }

    return new JsonRpcResponse
    {
        Id = request.Id,
        Result = JsonSerializer.SerializeToNode(taskInfo)
    };
}

static async Task<JsonRpcMessage> HandleTasksResultAsync(
    MessageContext context, JsonRpcRequest request, CancellationToken cancellationToken)
{
    var taskManager = context.Services!.GetRequiredService<TaskManager>();
    var taskParams = Deserialize<TaskIdParams>(request.Params);

    if (taskParams?.TaskId is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams, "Missing taskId parameter");
    }

    var taskInfo = taskManager.GetTask(taskParams.TaskId);
    if (taskInfo is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams, "Task not found");
    }

    // If task is terminal, return result immediately
    // Otherwise, block until completion (per spec)
    var result = await taskManager.GetResultAsync(taskParams.TaskId, cancellationToken);
    if (result is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams, "Task not found or cancelled");
    }

    // The stored result already contains the CallToolResult structure
    // Add the related-task metadata per spec
    var resultNode = result.Value.Result?.DeepClone() ?? new JsonObject();
    if (resultNode is JsonObject resultObj)
    {
        resultObj["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/related-task"] = new JsonObject
            {
                ["taskId"] = taskParams.TaskId
            }
        };
    }

    return new JsonRpcResponse
    {
        Id = request.Id,
        Result = resultNode
    };
}

static JsonRpcMessage HandleTasksCancel(MessageContext context, JsonRpcRequest request)
{
    var taskManager = context.Services!.GetRequiredService<TaskManager>();
    var taskParams = Deserialize<TaskIdParams>(request.Params);

    if (taskParams?.TaskId is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams, "Missing taskId parameter");
    }

    var taskInfo = taskManager.CancelTask(taskParams.TaskId);
    if (taskInfo is null)
    {
        return CreateError(request.Id, McpErrorCode.InvalidParams,
            "Task not found or already in terminal status");
    }

    return new JsonRpcResponse
    {
        Id = request.Id,
        Result = JsonSerializer.SerializeToNode(taskInfo)
    };
}

static JsonRpcMessage HandleTasksList(MessageContext context, JsonRpcRequest request)
{
    var taskManager = context.Services!.GetRequiredService<TaskManager>();
    var listParams = Deserialize<TaskListParams>(request.Params);

    var result = taskManager.ListTasks(listParams?.Cursor);

    return new JsonRpcResponse
    {
        Id = request.Id,
        Result = JsonSerializer.SerializeToNode(result)
    };
}

// ============================================================================
// Helpers
// ============================================================================

static T? Deserialize<T>(JsonNode? node) where T : class
{
    if (node is null) return null;
    return JsonSerializer.Deserialize<T>(node);
}

static JsonRpcError CreateError(RequestId id, McpErrorCode code, string message)
{
    return new JsonRpcError
    {
        Id = id,
        Error = new JsonRpcErrorDetail
        {
            Code = (int)code,
            Message = message
        }
    };
}
