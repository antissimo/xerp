namespace Xerp.Application.Common;

/// <summary>How a client that has no URL path (an MCP tool) names one record: by <c>id</c> or by <c>code</c>.</summary>
public sealed record RecordAddressInput(string? Id = null, string? Code = null);

/// <summary>The only argument of an operation that addresses a record by <c>id</c>.</summary>
public sealed record RecordIdInput(string? Id = null);

/// <summary>
/// Rules for addressing a record with values that arrive as text (spec 003, R13-R15). They mirror what
/// routing does for HTTP: an id that is not a UUID names no record, exactly like a malformed path segment.
/// </summary>
public static class RecordAddress
{
    /// <summary>
    /// Null when <paramref name="id"/> is a UUID. Otherwise the error: a missing id is a validation error
    /// under the argument's name; a text that is not a UUID is <c>NOT_FOUND</c>.
    /// </summary>
    /// <param name="name">The name of the addressing argument: <c>id</c>, or <c>articleId</c> / <c>unitId</c> where a record is addressed by two ids (spec 007, section 5).</param>
    public static AppError? Id(string? id, string notFoundDetail, out Guid value, string name = "id")
    {
        value = default;
        if (string.IsNullOrEmpty(id))
            return AppError.Validation(name, $"{name} is required.");
        return Guid.TryParse(id, out value) ? null : AppError.NotFound(notFoundDetail);
    }

    /// <summary>
    /// As <see cref="Id"/> for a record addressed by two ids: every missing one is reported, each under its own
    /// name; only then does a text that is not a UUID make the record <c>NOT_FOUND</c>.
    /// </summary>
    public static AppError? Ids(
        string? first, string firstName, out Guid firstValue, string? second, string secondName, out Guid secondValue, string notFoundDetail)
    {
        firstValue = default;
        secondValue = default;
        var missing = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(first))
            missing[firstName] = [$"{firstName} is required."];
        if (string.IsNullOrEmpty(second))
            missing[secondName] = [$"{secondName} is required."];
        if (missing.Count > 0)
            return AppError.Validation(missing);
        return Guid.TryParse(first, out firstValue) && Guid.TryParse(second, out secondValue) ? null : AppError.NotFound(notFoundDetail);
    }

    /// <summary>
    /// Exactly one of <c>id</c> and <c>code</c> must be present (R13); both or neither is a validation
    /// error under both names. On success exactly one of the out values is set.
    /// </summary>
    public static AppError? IdOrCode(RecordAddressInput input, string notFoundDetail, out Guid? id, out string? code)
    {
        id = null;
        code = null;
        if (input.Id is null == input.Code is null)
        {
            const string message = "Give exactly one of id and code.";
            return AppError.Validation(new Dictionary<string, string[]> { ["id"] = [message], ["code"] = [message] });
        }
        if (input.Code is not null)
        {
            code = input.Code;
            return null;
        }
        if (!Guid.TryParse(input.Id, out var parsed))
            return AppError.NotFound(notFoundDetail);
        id = parsed;
        return null;
    }
}
