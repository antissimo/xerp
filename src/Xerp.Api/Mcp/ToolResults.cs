using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Xerp.Application.Common;

namespace Xerp.Api.Mcp;

/// <summary>
/// The one place where the outcome of an Application operation becomes an MCP tool result
/// (spec 003, 5.2; ADR-0009, decisions 5 and 6) - the counterpart of <c>Problems</c> for HTTP.
/// </summary>
public static class ToolResults
{
    private static readonly JsonSerializerOptions ErrorOptions = new(JsonSerializerDefaults.Web);

    private sealed record ErrorBody(
        string Code,
        string Detail,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Errors,
        // Spec 012, section 5: exactly as the HTTP problem carries it.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RuleRefusal>? Rules);

    /// <summary>The object the HTTP endpoint returns: as <c>structuredContent</c> and, serialised, as the one text block.</summary>
    public static CallToolResult Success(object value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.SerializeToElement(value, value.GetType(), options);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
        };
    }

    /// <summary>A tool execution error: <c>{ code, detail, errors?, rules? }</c> in the one text block and no structured content.</summary>
    public static CallToolResult Error(AppError error) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new ErrorBody(error.Code, error.Detail, error.Errors, error.Rules), ErrorOptions) }],
        };

    /// <summary>Nothing of the exception reaches the client (spec 003, R17); the caller logs it.</summary>
    public static CallToolResult Unexpected(Exception _) => Error(AppError.Internal());
}
