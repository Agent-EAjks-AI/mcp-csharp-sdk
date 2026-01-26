using ConformanceServer.Prompts;
using ConformanceServer.Resources;
using ConformanceServer.Tools;
using ModelContextProtocol.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace ModelContextProtocol.ConformanceServer;

public class Program
{
    // Valid localhost values for DNS rebinding protection
    private static readonly HashSet<string> ValidLocalhostHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "[::1]",
        "::1"
    };

    public static async Task MainAsync(string[] args, ILoggerProvider? loggerProvider = null, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);

        if (loggerProvider != null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(loggerProvider);
        }

        // Dictionary of session IDs to a set of resource URIs they are subscribed to
        // The value is a ConcurrentDictionary used as a thread-safe HashSet
        // because .NET does not have a built-in concurrent HashSet
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> subscriptions = new();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<ConformanceTools>()
            .WithPrompts<ConformancePrompts>()
            .WithResources<ConformanceResources>()
            .WithSubscribeToResourcesHandler(async (ctx, ct) =>
            {
                if (ctx.Server.SessionId == null)
                {
                    throw new McpException("Cannot add subscription for server with null SessionId");
                }
                if (ctx.Params?.Uri is { } uri)
                {
                    var sessionSubscriptions = subscriptions.GetOrAdd(ctx.Server.SessionId, _ => new());
                    sessionSubscriptions.TryAdd(uri, 0);
                }

                return new EmptyResult();
            })
            .WithUnsubscribeFromResourcesHandler(async (ctx, ct) =>
            {
                if (ctx.Server.SessionId == null)
                {
                    throw new McpException("Cannot remove subscription for server with null SessionId");
                }
                if (ctx.Params?.Uri is { } uri)
                {
                    subscriptions[ctx.Server.SessionId].TryRemove(uri, out _);
                }

                return new EmptyResult();
            })
            .WithCompleteHandler(async (ctx, ct) =>
            {
                // Basic completion support - returns empty array for conformance
                // Real implementations would provide contextual suggestions
                return new CompleteResult
                {
                    Completion = new Completion
                    {
                        Values = [],
                        HasMore = false,
                        Total = 0
                    }
                };
            })
            .WithSetLoggingLevelHandler(async (ctx, ct) =>
            {
                if (ctx.Params?.Level is null)
                {
                    throw new McpProtocolException("Missing required argument 'level'", McpErrorCode.InvalidParams);
                }

                // The SDK updates the LoggingLevel field of the McpServer
                // Send a log notification to confirm the level was set
                await ctx.Server.SendNotificationAsync("notifications/message", new LoggingMessageNotificationParams
                {
                    Level = LoggingLevel.Info,
                    Logger = "conformance-test-server",
                    Data = JsonElement.Parse($"\"Log level set to: {ctx.Params.Level}\""),
                }, cancellationToken: ct);

                return new EmptyResult();
            });

        var app = builder.Build();

        // DNS rebinding protection middleware
        // Rejects requests with non-localhost Host/Origin headers for localhost servers
        // See: https://github.com/modelcontextprotocol/typescript-sdk/security/advisories/GHSA-w48q-cv73-mx4w
        app.Use(async (context, next) =>
        {
            // Check if this is a localhost server
            var localEndpoint = context.Connection.LocalIpAddress;
            bool isLocalhostServer = localEndpoint == null ||
                                     IPAddress.IsLoopback(localEndpoint) ||
                                     localEndpoint.Equals(IPAddress.IPv6Loopback);

            if (isLocalhostServer)
            {
                // Validate Host header
                var host = context.Request.Host.Host;
                if (!IsValidLocalhostHost(host))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Forbidden: Invalid Host header for localhost server");
                    return;
                }

                // Validate Origin header if present
                if (context.Request.Headers.TryGetValue("Origin", out var originValues))
                {
                    var origin = originValues.FirstOrDefault();
                    if (!string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                    {
                        if (!IsValidLocalhostHost(originUri.Host))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            await context.Response.WriteAsync("Forbidden: Invalid Origin header for localhost server");
                            return;
                        }
                    }
                }
            }

            await next();
        });

        app.MapMcp();

        app.MapGet("/health", () => TypedResults.Ok("Healthy"));

        await app.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Checks if the host is a valid localhost value.
    /// Valid values: localhost, 127.0.0.1, [::1], ::1 (with optional port)
    /// </summary>
    private static bool IsValidLocalhostHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Remove port if present (e.g., "localhost:3001" -> "localhost")
        var hostWithoutPort = host;
        if (host.StartsWith('['))
        {
            // IPv6 address with brackets, e.g., "[::1]:3001"
            var bracketEnd = host.IndexOf(']');
            if (bracketEnd > 0)
            {
                hostWithoutPort = host[..(bracketEnd + 1)];
            }
        }
        else
        {
            var colonIndex = host.LastIndexOf(':');
            if (colonIndex > 0)
            {
                hostWithoutPort = host[..colonIndex];
            }
        }

        return ValidLocalhostHosts.Contains(hostWithoutPort);
    }

    public static async Task Main(string[] args)
    {
        await MainAsync(args);
    }
}
