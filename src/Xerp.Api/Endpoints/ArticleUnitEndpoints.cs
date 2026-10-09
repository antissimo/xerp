using Xerp.Api.Http;
using Xerp.Application.ArticleUnits;

namespace Xerp.Api.Endpoints;

/// <summary>The unit conversions of an article (spec 007, 4.1), a sub-resource of the article. Thin: bind, call, map.</summary>
public static class ArticleUnitEndpoints
{
    public static string Route(Guid articleId) => $"{ArticleEndpoints.Route}/{articleId}/units";

    public static void MapArticleUnitEndpoints(this IEndpointRouteBuilder v1)
    {
        // A malformed id in either place does not match the route and ends as NOT_FOUND like any unknown path.
        var units = v1.MapGroup(ArticleEndpoints.Route + "/{articleId:guid}/units");

        units.MapGet("", async (Guid articleId, HttpRequest request, ArticleUnitOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListArticleUnitsInput(binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(articleId, input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("limit", "offset");

        units.MapGet("/{unitId:guid}", async (Guid articleId, Guid unitId, ArticleUnitOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(articleId, unitId, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        // Creates or replaces (R3): 201 with Location when the conversion was created, 200 when its factor was replaced.
        units.MapPut("/{unitId:guid}", async (Guid articleId, Guid unitId, HttpRequest request, ArticleUnitOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetArticleUnitInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.SetAsync(articleId, unitId, body.Value, ct);
            if (!result.IsSuccess)
                return Problems.From(result.Error);
            return result.Value.Created
                ? Results.Created($"/api/v1{Route(articleId)}/{unitId}", result.Value.ArticleUnit)
                : Results.Ok(result.Value.ArticleUnit);
        });

        units.MapDelete("/{unitId:guid}", async (Guid articleId, Guid unitId, ArticleUnitOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(articleId, unitId, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }
}
