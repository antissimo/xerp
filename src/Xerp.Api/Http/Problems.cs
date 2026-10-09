using System.Text.Json.Serialization;
using Xerp.Application.Common;

namespace Xerp.Api.Http;

/// <summary>
/// The one place where an <see cref="AppError"/> becomes an HTTP response: <c>application/problem+json</c>
/// with a stable <c>code</c> (docs/architecture.md section 6).
/// </summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    private sealed record Body(
        string Type,
        string Title,
        int Status,
        string Detail,
        string Code,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Errors);

    public static int StatusOf(string code) => code switch
    {
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.CodeTaken or ErrorCodes.InUse or ErrorCodes.InvalidState or ErrorCodes.CannotRevokeSelf
            or ErrorCodes.ReferenceNotFound or ErrorCodes.ReferenceInactive
            or ErrorCodes.InsufficientStock or ErrorCodes.ArticleNotStocked
            or ErrorCodes.UnitIsBaseUnit or ErrorCodes.UnitNotOnArticle or ErrorCodes.QuantityNotConvertible => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IResult From(AppError error) => new ProblemResult(error);

    /// <summary>An endpoint result that writes exactly what <see cref="WriteAsync"/> writes, challenge header included.</summary>
    private sealed class ProblemResult(AppError error) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, error);
    }

    /// <summary>Writes the problem directly; for middleware, where there is no endpoint result.</summary>
    public static Task WriteAsync(HttpContext context, AppError error)
    {
        context.Response.StatusCode = StatusOf(error.Code);
        if (error.Code == ErrorCodes.Unauthenticated)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        return context.Response.WriteAsJsonAsync(ToBody(error), options: null, contentType: ContentType, context.RequestAborted);
    }

    private static Body ToBody(AppError error) =>
        new("about:blank", error.Code, StatusOf(error.Code), error.Detail, error.Code, error.Errors);
}
