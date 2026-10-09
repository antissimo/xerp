using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xerp.Application.Common;

namespace Xerp.Api.Mcp;

/// <summary>The behaviour classes of spec 003, 5.3; each fixes the annotations sent with a tool.</summary>
public enum ToolKind
{
    Read,
    Create,
    Update,
    Delete,
}

/// <summary>Runs a tool: binds the arguments, calls one Application operation, maps its result.</summary>
public delegate Task<CallToolResult> ToolInvoker(
    IServiceProvider services,
    IEnumerable<KeyValuePair<string, JsonElement>>? arguments,
    JsonSerializerOptions resultOptions,
    CancellationToken cancellationToken);

/// <summary>
/// One MCP tool = one Application operation (ADR-0005). A tool has no rules of its own: it binds, calls and
/// maps (spec 003, R12).
/// </summary>
public sealed record XerpTool(
    string Name,
    ToolKind Kind,
    string Description,
    JsonElement InputSchema,
    JsonElement OutputSchema,
    Type InputType,
    bool HasSeparateId,
    ToolInvoker Invoke)
{
    /// <summary>A tool whose arguments are exactly the input of the operation.</summary>
    public static XerpTool For<TInput, TOutput>(
        string name, ToolKind kind, string description, JsonElement inputSchema,
        Func<IServiceProvider, TInput, CancellationToken, Task<Result<TOutput>>> operation)
        where TInput : class
        where TOutput : notnull =>
        new(name, kind, description, inputSchema, ToolSchemas.Output(typeof(TOutput)), typeof(TInput), false,
            async (services, arguments, resultOptions, cancellationToken) =>
            {
                var input = ToolArguments.Bind<TInput>(arguments);
                if (!input.IsSuccess)
                    return ToolResults.Error(input.Error);
                var result = await operation(services, input.Value, cancellationToken);
                return result.IsSuccess ? ToolResults.Success(result.Value, resultOptions) : ToolResults.Error(result.Error);
            });

    /// <summary>A tool whose arguments are the <c>id</c> of the record (the path over HTTP) plus the input of the operation (the body).</summary>
    public static XerpTool WithId<TInput, TOutput>(
        string name, ToolKind kind, string description, JsonElement inputSchema,
        Func<IServiceProvider, string?, TInput, CancellationToken, Task<Result<TOutput>>> operation)
        where TInput : class
        where TOutput : notnull =>
        new(name, kind, description, inputSchema, ToolSchemas.Output(typeof(TOutput)), typeof(TInput), true,
            async (services, arguments, resultOptions, cancellationToken) =>
            {
                var bound = ToolArguments.BindWithId<TInput>(arguments);
                if (!bound.IsSuccess)
                    return ToolResults.Error(bound.Error);
                var result = await operation(services, bound.Value.Id, bound.Value.Input, cancellationToken);
                return result.IsSuccess ? ToolResults.Success(result.Value, resultOptions) : ToolResults.Error(result.Error);
            });

    public Tool ToProtocolTool() =>
        new()
        {
            Name = Name,
            Description = Description,
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            // Every value of the table in spec 003, 5.3 is sent explicitly, also where it is the MCP default.
            Annotations = Kind switch
            {
                ToolKind.Read => new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false },
                ToolKind.Create => new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false },
                ToolKind.Update => new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = true, OpenWorldHint = false },
                ToolKind.Delete => new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = false, OpenWorldHint = false },
                _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
            },
        };
}
