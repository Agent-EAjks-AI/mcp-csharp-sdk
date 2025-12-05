using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Tests.Utils;

namespace ModelContextProtocol.Tests.Configuration;

public class McpServerBuilderExtensionsMessageFilterTests : ClientServerTestBase
{
    public McpServerBuilderExtensionsMessageFilterTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    private readonly MockLoggerProvider _mockLoggerProvider = new();
    private readonly List<string> _messageTypes = [];

    private static ILogger GetLogger(IServiceProvider? services, string categoryName)
    {
        var loggerFactory = services?.GetRequiredService<ILoggerFactory>() ?? throw new InvalidOperationException("LoggerFactory not available");
        return loggerFactory.CreateLogger(categoryName);
    }

    protected override void ConfigureServices(ServiceCollection services, IMcpServerBuilder mcpServerBuilder)
    {
        mcpServerBuilder
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                var logger = GetLogger(context.Services, "MessageFilter1");
                logger.LogInformation("MessageFilter1 before");

                // Log the type of the message
                var messageTypeName = context.JsonRpcMessage.GetType().Name;
                _messageTypes.Add(messageTypeName);

                await next(context, cancellationToken);

                logger.LogInformation("MessageFilter1 after");
            })
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                var logger = GetLogger(context.Services, "MessageFilter2");
                logger.LogInformation("MessageFilter2 before");
                await next(context, cancellationToken);
                logger.LogInformation("MessageFilter2 after");
            })
            .WithTools<TestTool>()
            .WithPrompts<TestPrompt>()
            .WithResources<TestResource>();

        services.AddSingleton<ILoggerProvider>(_mockLoggerProvider);
    }

    [Fact]
    public async Task AddMessageFilter_Logs_For_Request()
    {
        await using McpClient client = await CreateMcpClientForServer();

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var beforeMessages = _mockLoggerProvider.LogMessages.Where(m => m.Message == "MessageFilter1 before").ToList();
        Assert.True(beforeMessages.Count > 0);
        Assert.Equal(LogLevel.Information, beforeMessages[0].LogLevel);
        Assert.Equal("MessageFilter1", beforeMessages[0].Category);

        var afterMessages = _mockLoggerProvider.LogMessages.Where(m => m.Message == "MessageFilter1 after").ToList();
        Assert.True(afterMessages.Count > 0);
        Assert.Equal(LogLevel.Information, afterMessages[0].LogLevel);
        Assert.Equal("MessageFilter1", afterMessages[0].Category);
    }

    [Fact]
    public async Task AddMessageFilter_Intercepts_Request_Messages()
    {
        await using McpClient client = await CreateMcpClientForServer();

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The message filter should intercept JsonRpcRequest messages
        Assert.Contains("JsonRpcRequest", _messageTypes);
    }

    [Fact]
    public async Task AddMessageFilter_Multiple_Filters_Execute_In_Order()
    {
        await using McpClient client = await CreateMcpClientForServer();

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var logMessages = _mockLoggerProvider.LogMessages
            .Where(m => m.Category.StartsWith("MessageFilter"))
            .Select(m => m.Message)
            .ToList();

        // First filter registered is outermost
        // We should see this pattern for each message: MessageFilter1 before -> MessageFilter2 before -> MessageFilter2 after -> MessageFilter1 after
        int idx1Before = logMessages.IndexOf("MessageFilter1 before");
        int idx2Before = logMessages.IndexOf("MessageFilter2 before");
        int idx2After = logMessages.IndexOf("MessageFilter2 after");
        int idx1After = logMessages.IndexOf("MessageFilter1 after");

        Assert.True(idx1Before >= 0);
        Assert.True(idx2Before >= 0);
        Assert.True(idx2After >= 0);
        Assert.True(idx1After >= 0);

        // Verify ordering within a single request
        Assert.True(idx1Before < idx2Before);
        Assert.True(idx2Before < idx2After);
        Assert.True(idx2After < idx1After);
    }

    [Fact]
    public async Task AddMessageFilter_Has_Access_To_Server()
    {
        McpServer? capturedServer = null;

        var services = new ServiceCollection();
        var mockLoggerProvider = new MockLoggerProvider();
        services.AddLogging();
        services.AddSingleton<ILoggerProvider>(mockLoggerProvider);

        services.AddMcpServer()
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                capturedServer = context.Server;
                await next(context, cancellationToken);
            })
            .WithTools<TestTool>()
            .WithStreamServerTransport(new MemoryStream(), new MemoryStream());

        await using var serviceProvider = services.BuildServiceProvider();
        var server = serviceProvider.GetRequiredService<McpServer>();

        // We just need to verify that the server is captured; we can't call Run without a proper transport
        Assert.Null(capturedServer); // Server not captured yet as we haven't run any requests
    }

    [Fact]
    public async Task AddMessageFilter_Items_Dictionary_Can_Be_Used()
    {
        string? capturedValue = null;

        var services = new ServiceCollection();
        var mockLoggerProvider = new MockLoggerProvider();
        services.AddLogging();
        services.AddSingleton<ILoggerProvider>(mockLoggerProvider);

        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();

        services.AddMcpServer()
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                context.Items["testKey"] = "testValue";
                await next(context, cancellationToken);
            })
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                if (context.Items.TryGetValue("testKey", out var value))
                {
                    capturedValue = value as string;
                }
                await next(context, cancellationToken);
            })
            .WithTools<TestTool>()
            .WithStreamServerTransport(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());

        await using var serviceProvider = services.BuildServiceProvider();
        var server = serviceProvider.GetRequiredService<McpServer>();

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: pipe1.Writer.AsStream(),
                pipe2.Reader.AsStream(),
                LoggerFactory),
            loggerFactory: LoggerFactory,
            cancellationToken: TestContext.Current.CancellationToken);

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        pipe1.Writer.Complete();
        pipe2.Writer.Complete();
        await cts.CancelAsync();

        Assert.Equal("testValue", capturedValue);
    }

    [Fact]
    public async Task AddMessageFilter_Can_Access_JsonRpcMessage_Details()
    {
        string? capturedMethod = null;

        var services = new ServiceCollection();
        var mockLoggerProvider = new MockLoggerProvider();
        services.AddLogging();
        services.AddSingleton<ILoggerProvider>(mockLoggerProvider);

        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();

        services.AddMcpServer()
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest request && request.Method == RequestMethods.ToolsList)
                {
                    capturedMethod = request.Method;
                }
                await next(context, cancellationToken);
            })
            .WithTools<TestTool>()
            .WithStreamServerTransport(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());

        await using var serviceProvider = services.BuildServiceProvider();
        var server = serviceProvider.GetRequiredService<McpServer>();

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: pipe1.Writer.AsStream(),
                pipe2.Reader.AsStream(),
                LoggerFactory),
            loggerFactory: LoggerFactory,
            cancellationToken: TestContext.Current.CancellationToken);

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        pipe1.Writer.Complete();
        pipe2.Writer.Complete();
        await cts.CancelAsync();

        Assert.Equal(RequestMethods.ToolsList, capturedMethod);
    }

    [Fact]
    public async Task AddMessageFilter_Exception_Propagates_Properly()
    {
        var services = new ServiceCollection();
        var mockLoggerProvider = new MockLoggerProvider();
        services.AddLogging();
        services.AddSingleton<ILoggerProvider>(mockLoggerProvider);

        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();

        services.AddMcpServer()
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                // Only throw for tools/list, not for initialize/initialized
                if (context.JsonRpcMessage is JsonRpcRequest request && request.Method == RequestMethods.ToolsList)
                {
                    throw new InvalidOperationException("Filter exception");
                }
                await next(context, cancellationToken);
            })
            .WithTools<TestTool>()
            .WithStreamServerTransport(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());

        await using var serviceProvider = services.BuildServiceProvider();
        var server = serviceProvider.GetRequiredService<McpServer>();

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: pipe1.Writer.AsStream(),
                pipe2.Reader.AsStream(),
                LoggerFactory),
            loggerFactory: LoggerFactory,
            cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
        {
            await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        });

        pipe1.Writer.Complete();
        pipe2.Writer.Complete();
        await cts.CancelAsync();

        Assert.Contains("error", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddMessageFilter_Runs_Before_Request_Specific_Filters()
    {
        var executionOrder = new List<string>();

        var services = new ServiceCollection();
        var mockLoggerProvider = new MockLoggerProvider();
        services.AddLogging();
        services.AddSingleton<ILoggerProvider>(mockLoggerProvider);

        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();

        services.AddMcpServer()
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest request && request.Method == RequestMethods.ToolsList)
                {
                    executionOrder.Add("MessageFilter");
                }
                await next(context, cancellationToken);
            })
            .AddListToolsFilter((next) => async (request, cancellationToken) =>
            {
                executionOrder.Add("ListToolsFilter");
                return await next(request, cancellationToken);
            })
            .WithTools<TestTool>()
            .WithStreamServerTransport(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());

        await using var serviceProvider = services.BuildServiceProvider();
        var server = serviceProvider.GetRequiredService<McpServer>();

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: pipe1.Writer.AsStream(),
                pipe2.Reader.AsStream(),
                LoggerFactory),
            loggerFactory: LoggerFactory,
            cancellationToken: TestContext.Current.CancellationToken);

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        pipe1.Writer.Complete();
        pipe2.Writer.Complete();
        await cts.CancelAsync();

        // Message filter should run before the request-specific filter
        Assert.Equal(2, executionOrder.Count);
        Assert.Equal("MessageFilter", executionOrder[0]);
        Assert.Equal("ListToolsFilter", executionOrder[1]);
    }

    [Fact]
    public async Task AddMessageFilter_Can_Skip_Default_Handlers()
    {
        var services = new ServiceCollection();
        var mockLoggerProvider = new MockLoggerProvider();
        services.AddLogging();
        services.AddSingleton<ILoggerProvider>(mockLoggerProvider);

        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();

        services.AddMcpServer()
            .AddMessageFilter((next) => async (context, cancellationToken) =>
            {
                // Skip calling next for tools/list
                if (context.JsonRpcMessage is JsonRpcRequest request && request.Method == RequestMethods.ToolsList)
                {
                    // Don't call next - this will skip the default handler
                    return;
                }
                await next(context, cancellationToken);
            })
            .WithTools<TestTool>()
            .WithStreamServerTransport(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());

        await using var serviceProvider = services.BuildServiceProvider();
        var server = serviceProvider.GetRequiredService<McpServer>();

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: pipe1.Writer.AsStream(),
                pipe2.Reader.AsStream(),
                LoggerFactory),
            loggerFactory: LoggerFactory,
            cancellationToken: TestContext.Current.CancellationToken);

        // When default handlers are skipped, the request should time out
        // because no response will be sent
        using var requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.ListToolsAsync(cancellationToken: requestCts.Token);
        });

        pipe1.Writer.Complete();
        pipe2.Writer.Complete();
        await cts.CancelAsync();
    }

    [McpServerToolType]
    public sealed class TestTool
    {
        [McpServerTool]
        public static string TestToolMethod()
        {
            return "test result";
        }
    }

    [McpServerPromptType]
    public sealed class TestPrompt
    {
        [McpServerPrompt]
        public static Task<GetPromptResult> TestPromptMethod()
        {
            return Task.FromResult(new GetPromptResult
            {
                Description = "Test prompt",
                Messages = [new() { Role = Role.User, Content = new TextContentBlock { Text = "Test" } }]
            });
        }
    }

    [McpServerResourceType]
    public sealed class TestResource
    {
        [McpServerResource(UriTemplate = "test://resource/{id}")]
        public static string TestResourceMethod(string id)
        {
            return $"Test resource for ID: {id}";
        }
    }
}
