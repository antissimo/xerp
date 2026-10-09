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
    /// under <c>id</c>; a text that is not a UUID is <c>NOT_FOUND</c>.
    /// </summary>
    public static AppError? Id(string? id, string notFoundDetail, out Guid value)
    {
        value = default;
        if (string.IsNullOrEmpty(id))
            return AppError.Validation("id", "id is required.");
        return Guid.TryParse(id, out value) ? null : AppError.NotFound(notFoundDetail);
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
