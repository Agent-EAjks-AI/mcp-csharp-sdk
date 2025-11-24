using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol;

/// <summary>
/// Contains optional parameters for MCP requests.
/// </summary>
public sealed class RequestOptions
{
    /// <summary>
    /// Gets or sets optional metadata to include in the request.
    /// </summary>
    public JsonObject? Meta { get; set; }

    /// <summary>
    /// Gets or sets the JSON serializer options to use for serialization and deserialization.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// Gets or sets the progress token for tracking long-running operations.
    /// </summary>
    public ProgressToken? ProgressToken { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestOptions"/> class.
    /// </summary>
    public RequestOptions()
    {
    }
}