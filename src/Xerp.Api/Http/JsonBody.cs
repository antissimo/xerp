using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xerp.Application.Common;

namespace Xerp.Api.Http;

/// <summary>
/// Strict request-body binding: unknown and duplicate properties, wrong types, malformed JSON and a
/// missing body are all <c>400 VALIDATION_FAILED</c> (spec 001, R10, E4), so a client's typo is never ignored.
/// Missing or null fields are left null; whether they are required is an Application rule.
/// </summary>
public static partial class JsonBody
{
    private const string WholeBody = "body";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

    // A property of the body, or an element of an array property and a property of that element:
    // "$.code", "$.lines[1]", "$.lines[1].quantity" (ADR-0012, decision 12).
    [GeneratedRegex(@"^\$\.([A-Za-z_][A-Za-z0-9_]*(?:\[\d+\](?:\.[A-Za-z_][A-Za-z0-9_]*)?)?)$")]
    private static partial Regex TopLevelProperty();

    public static async Task<Result<T>> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return Bound(await JsonSerializer.DeserializeAsync<T>(request.Body, Options, cancellationToken));
        }
        catch (JsonException ex)
        {
            return Rejected(ex);
        }
    }

    /// <summary>
    /// As <see cref="ReadAsync{T}"/>, for an operation whose body has nothing that the transport can require:
    /// a request without a body is the empty object, and Application says which fields are missing.
    /// </summary>
    public static async Task<Result<T>> ReadOrEmptyAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.Length == 0 ? Read<T>("{}"u8) : Read<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>The same strict binding for a JSON object that is already in memory (MCP tool arguments).</summary>
    public static Result<T> Read<T>(ReadOnlySpan<byte> utf8Json) where T : class
    {
        try
        {
            return Bound(JsonSerializer.Deserialize<T>(utf8Json, Options));
        }
        catch (JsonException ex)
        {
            return Rejected(ex);
        }
    }

    private static Result<T> Bound<T>(T? value) where T : class =>
        value is null ? AppError.Validation(WholeBody, "The request body must be a JSON object.") : value;

    private static AppError Rejected(JsonException ex)
    {
        // The exception text is not passed on: it can echo input and is not a stable contract.
        var match = TopLevelProperty().Match(ex.Path ?? "");
        return match.Success
            ? AppError.Validation(match.Groups[1].Value, "Unknown property, or a value of the wrong JSON type.")
            : AppError.Validation(WholeBody, "The request body must be a JSON object with the documented properties only.");
    }
}
