using System.Text.Json;
using Xerp.Api.Http;
using Xerp.Application.Common;

namespace Xerp.Api.Mcp;

/// <summary>
/// Binds the arguments of a tool call to the input of an Application operation with the rules of a
/// request body (spec 003, R14): unknown arguments and wrong JSON types are VALIDATION_FAILED under the
/// argument's name; what is required and what a value may be is decided by the operation.
/// </summary>
public static class ToolArguments
{
    private const string Id = "id";

    public static Result<T> Bind<T>(IEnumerable<KeyValuePair<string, JsonElement>>? arguments) where T : class =>
        JsonBody.Read<T>(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, JsonElement>(arguments ?? [])));

    /// <summary>
    /// For tools whose HTTP operation takes the id in the path and the rest in the body: the <c>id</c>
    /// argument is taken out (as text, or null when absent) and the remaining arguments are bound.
    /// </summary>
    public static Result<(string? Id, T Input)> BindWithId<T>(IEnumerable<KeyValuePair<string, JsonElement>>? arguments) where T : class
    {
        var rest = new Dictionary<string, JsonElement>(arguments ?? []);
        string? id = null;
        if (rest.Remove(Id, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String)
                return AppError.Validation(Id, "id must be a string.");
            id = value.GetString();
        }
        var input = Bind<T>(rest);
        return input.IsSuccess ? (id, input.Value) : input.Error;
    }
}
