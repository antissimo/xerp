using Xerp.Api.Http;
using Xerp.Application.ApiKeys;
using Xerp.Application.Common;

namespace Xerp.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public const string Route = "/api-keys";

    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder v1)
    {
        var keys = v1.MapGroup(Route);

        keys.MapGet("", async (HttpRequest request, ApiKeyOperations operations, CancellationToken ct) =>
        {
            var input = BindList(request.Query);
            if (!input.IsSuccess)
                return Problems.From(input.Error);
            var result = await operations.ListAsync(input.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "actorType", "isActive", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path (E2).
        keys.MapGet("/{id:guid}", async (Guid id, ApiKeyOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        keys.MapPost("", async (HttpContext http, ApiKeyOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateApiKeyInput>(http.Request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            if (!result.IsSuccess)
                return Problems.From(result.Error);
            // The body carries a secret (spec 003, S3).
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value);
        });

        keys.MapPost("/{id:guid}/revoke", async (Guid id, ApiKeyOperations operations, CancellationToken ct) =>
        {
            var result = await operations.RevokeAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }

    private static Result<ListApiKeysInput> BindList(IQueryCollection query)
    {
        var binder = new QueryBinder(query);
        var input = new ListApiKeysInput(
            binder.Text("search"), binder.Text("actorType"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"));
        return binder.Error is { } error ? error : input;
    }
}
