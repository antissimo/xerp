using Xerp.Application.Common;
using Xerp.Application.Identity;

namespace Xerp.Api.Http;

/// <summary>
/// Guards everything under <c>/api/v1</c> (spec 001, S1-S4). It works on the path, not on endpoint
/// metadata, so a route added later cannot be left unauthenticated by omission:
/// no valid credential -> 401; admin key outside <c>/api/v1/admin</c> or tenant key inside it -> 403.
/// It also turns "no such route" under <c>/api/v1</c> into a NOT_FOUND problem.
/// </summary>
public sealed class ApiV1Middleware(RequestDelegate next)
{
    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context, CredentialResolver credentials, RequestActor actor)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1", out var rest))
        {
            await next(context);
            return;
        }

        var credential = await credentials.ResolveAsync(BearerToken(context.Request), context.RequestAborted);
        if (credential is null)
        {
            await Problems.WriteAsync(context, AppError.Unauthenticated());
            return;
        }

        var isAdminRoute = rest.StartsWithSegments("/admin");
        if (isAdminRoute != credential is AdminCredential)
        {
            await Problems.WriteAsync(context, AppError.Forbidden());
            return;
        }
        if (credential is TenantCredential tenant)
            actor.Set(tenant.Key.TenantId, tenant.Key.ApiKeyId);

        await next(context);

        // Routing found no endpoint (404) or no endpoint for this method (405) and wrote no body.
        var response = context.Response;
        if (!response.HasStarted && response.ContentType is null &&
            response.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed)
        {
            await Problems.WriteAsync(context, AppError.NotFound("No such resource or operation."));
        }
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value)
            return null;
        return value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? value[BearerPrefix.Length..].Trim() : null;
    }
}
