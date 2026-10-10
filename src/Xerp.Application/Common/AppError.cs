namespace Xerp.Application.Common;

/// <summary>Stable, machine-readable error codes (docs/architecture.md section 6). Part of the contract.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string CodeTaken = "CODE_TAKEN";
    public const string InUse = "IN_USE";
    public const string ReferenceNotFound = "REFERENCE_NOT_FOUND";
    public const string ReferenceInactive = "REFERENCE_INACTIVE";
    public const string CannotRevokeSelf = "CANNOT_REVOKE_SELF";
    public const string InvalidState = "INVALID_STATE";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string ArticleNotStocked = "ARTICLE_NOT_STOCKED";
    public const string UnitIsBaseUnit = "UNIT_IS_BASE_UNIT";
    public const string UnitNotOnArticle = "UNIT_NOT_ON_ARTICLE";
    public const string QuantityNotConvertible = "QUANTITY_NOT_CONVERTIBLE";
    public const string CountOutdated = "COUNT_OUTDATED";
    public const string PartnerRoleMissing = "PARTNER_ROLE_MISSING";
    public const string OrderNotOpen = "ORDER_NOT_OPEN";
    public const string OrderMismatch = "ORDER_MISMATCH";
    public const string QuantityExceedsOrder = "QUANTITY_EXCEEDS_ORDER";
    public const string DefaultWarehouse = "DEFAULT_WAREHOUSE";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>
/// An expected failure of an Application operation. Clients (HTTP, MCP, CLI) map it to their own
/// transport; they branch on <see cref="Code"/>, never on <see cref="Detail"/>.
/// </summary>
public sealed record AppError(string Code, string Detail, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static AppError Unauthenticated(string detail = "A valid API key is required.") =>
        new(ErrorCodes.Unauthenticated, detail);

    public static AppError Forbidden(string detail = "This credential is not allowed to perform the operation.") =>
        new(ErrorCodes.Forbidden, detail);

    /// <summary>The request was refused unread because its body is larger than the transport accepts.</summary>
    public static AppError PayloadTooLarge(long maxBytes) =>
        new(ErrorCodes.PayloadTooLarge, $"The request body is larger than the limit of {maxBytes} bytes.");

    public static AppError Internal() => new(ErrorCodes.InternalError, "An unexpected error occurred.");

    public static AppError NotFound(string detail) => new(ErrorCodes.NotFound, detail);

    public static AppError CodeTaken(string detail) => new(ErrorCodes.CodeTaken, detail);

    public static AppError InUse(string detail) => new(ErrorCodes.InUse, detail);

    /// <summary>The record is used, so the named fields cannot change (ADR-0012: <c>IN_USE</c> for frozen fields).</summary>
    public static AppError InUse(string detail, IReadOnlyDictionary<string, string[]> errors) => new(ErrorCodes.InUse, detail, errors);

    /// <summary>The operation is not allowed in the document's current status (ADR-0007).</summary>
    public static AppError InvalidState(string detail) => new(ErrorCodes.InvalidState, detail);

    /// <summary>ADR-0008: a well-formed id in request field <paramref name="field"/> names no record of this tenant.</summary>
    public static AppError ReferenceNotFound(string field, Guid id) =>
        new(ErrorCodes.ReferenceNotFound, $"The record referenced by {field} does not exist.",
            new Dictionary<string, string[]> { [field] = [$"No record with id '{id}' exists."] });

    /// <summary>ADR-0008: the record named by request field <paramref name="field"/> is inactive and would be newly assigned.</summary>
    public static AppError ReferenceInactive(string field, Guid id) =>
        new(ErrorCodes.ReferenceInactive, $"The record referenced by {field} is inactive and cannot be newly assigned.",
            new Dictionary<string, string[]> { [field] = [$"The record with id '{id}' is inactive."] });

    public static AppError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

    public static AppError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });
}
