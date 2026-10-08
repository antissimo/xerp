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

    [GeneratedRegex(@"^\$\.([A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex TopLevelProperty();

    public static async Task<Result<T>> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(request.Body, Options, cancellationToken);
            return value is null
                ? AppError.Validation(WholeBody, "The request body must be a JSON object.")
                : value;
        }
        catch (JsonException ex)
        {
            // The exception text is not passed on: it can echo input and is not a stable contract.
            var match = TopLevelProperty().Match(ex.Path ?? "");
            return match.Success
                ? AppError.Validation(match.Groups[1].Value, "Unknown property, or a value of the wrong JSON type.")
                : AppError.Validation(WholeBody, "The request body must be a JSON object with the documented properties only.");
        }
    }
}
