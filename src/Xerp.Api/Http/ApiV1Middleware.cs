using Xerp.Application.Common;
using Xerp.Application.Identity;

namespace Xerp.Api.Http;

/// <summary>
/// Guards everything under <c>/api/v1</c> (spec 001, S1-S4) and <c>/mcp</c> (spec 003, S1-S2). It works on
/// the path, not on endpoint metadata, so a route added later cannot be left unauthenticated by omission:
/// no valid credential -> 401; admin key outside <c>/api/v1/admin</c> or tenant key inside it -> 403.
/// Both transports therefore establish tenant and actor in exactly the same way, once per HTTP request.
/// It also turns "no such route" under <c>/api/v1</c> into a NOT_FOUND problem.
/// </summary>
public sealed class ApiV1Middleware(RequestDelegate next)
{
    private const string BearerPrefix = "Bearer ";

    public const string McpPath = "/mcp";

    public async Task InvokeAsync(HttpContext context, CredentialResolver credentials, RequestActor actor, IConfiguration configuration)
    {
        var isMcp = context.Request.Path.StartsWithSegments(McpPath);
        if (!context.Request.Path.StartsWithSegments("/api/v1", out var rest) && !isMcp)
        {
            await next(context);
            return;
        }

        // Spec 003, S2: a browser page of an origin that is not on the allow-list may not talk to /mcp,
        // whatever credential it carries. Clients that are not browsers send no Origin.
        if (isMcp && context.Request.Headers.Origin is { Count: > 0 } origins && !IsAllowed(origins, configuration))
        {
            await Problems.WriteAsync(context, AppError.Forbidden("This origin is not allowed to use the MCP endpoint."));
            return;
        }

        var credential = await credentials.ResolveAsync(BearerToken(context.Request), context.RequestAborted);
        if (credential is null)
        {
            await Problems.WriteAsync(context, AppError.Unauthenticated());
            return;
        }

        var isAdminRoute = !isMcp && rest.StartsWithSegments("/admin");
        if (isAdminRoute != credential is AdminCredential)
        {
            await Problems.WriteAsync(context, AppError.Forbidden());
            return;
        }
        if (credential is TenantCredential tenant)
            actor.Set(tenant.Key.TenantId, tenant.Key.ApiKeyId);

        await next(context);

        // Routing found no endpoint (404) or no endpoint for this method (405) and wrote no body.
        // /mcp answers by the rules of its own transport (405 for GET and DELETE).
        var response = context.Response;
        if (!isMcp && !response.HasStarted && response.ContentType is null &&
            response.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed)
        {
            await Problems.WriteAsync(context, AppError.NotFound("No such resource or operation."));
        }
    }

    private static bool IsAllowed(Microsoft.Extensions.Primitives.StringValues origins, IConfiguration configuration)
    {
        var allowed = configuration.GetSection("Xerp:Mcp:AllowedOrigins").Get<string[]>() ?? [];
        return origins.Count == 1 && allowed.Contains(origins[0], StringComparer.OrdinalIgnoreCase);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value)
            return null;
        return value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? value[BearerPrefix.Length..].Trim() : null;
    }
}
