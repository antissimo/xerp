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
    public const string InvalidState = "INVALID_STATE";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>
/// An expected failure of an Application operation. Clients (HTTP, MCP, CLI) map it to their own
/// transport; they branch on <see cref="Code"/>, never on <see cref="Detail"/>.
/// </summary>
public sealed record AppError(string Code, string Detail, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static AppError NotFound(string detail) => new(ErrorCodes.NotFound, detail);

    public static AppError CodeTaken(string detail) => new(ErrorCodes.CodeTaken, detail);

    public static AppError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

    public static AppError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });
}
