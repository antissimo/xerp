using System.Reflection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Xerp.Api.Mcp;

/// <summary>
/// The MCP server (ADR-0009): official SDK, stateless Streamable HTTP, tools only. Authentication and the
/// tenant are established by <c>ApiV1Middleware</c> for the HTTP request that carries the JSON-RPC message,
/// exactly as for <c>/api/v1</c>; nothing is kept between requests.
/// </summary>
public static class McpServerSetup
{
    public const string ServerName = "xerp";

    /// <summary>Static text: the same for every tenant (spec 003, T6).</summary>
    public const string Instructions =
        "Xerp is an ERP. All data read or written through these tools belongs to the tenant of the API key that "
        + "authenticates the connection; there is no way to address another tenant. Call `whoami` to see the tenant "
        + "and the actor your calls are attributed to. Records are referenced by their `id` (a UUID); a `code` is a "
        + "human-readable label used for lookup (`*_get` with `code`) and can change. Field names, rules and results "
        + "are identical to the HTTP API. A failed call returns a tool error whose text is JSON "
        + "`{ \"code\", \"detail\", \"errors\"?, \"rules\"? }`: branch on `code` (stable), read `detail` for an explanation, and "
        + "use the keys of `errors` to see which arguments to correct. When `rules` is present, a business rule this company can "
        + "switch refused the call: it names the rule's `key` and the `value` it has (see `rule_list`); when it is absent, no switch lifts the refusal.";

    private static readonly Dictionary<string, XerpTool> Tools = ToolCatalog.All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static readonly ListToolsResult ToolList = new() { Tools = ToolCatalog.All.Select(t => t.ToProtocolTool()).ToList() };

    public static IServiceCollection AddXerpMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = ServerName, Version = ApiVersion };
                options.ServerInstructions = Instructions;
                options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
            })
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(ToolList))
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    private static string ApiVersion =>
        typeof(McpServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params?.Name;
        // Not a tool result: the caller named something that does not exist (spec 003, E6).
        if (name is null || !Tools.TryGetValue(name, out var tool))
            throw new McpProtocolException($"Unknown tool: '{name}'.", McpErrorCode.InvalidParams);

        var services = request.Services ?? throw new InvalidOperationException("The MCP request has no service provider.");
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpServerSetup));
        try
        {
            var resultOptions = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            return await tool.Invoke(services, request.Params!.Arguments, resultOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in MCP tool {Tool}", name);
            return ToolResults.Unexpected(ex);
        }
    }
}
