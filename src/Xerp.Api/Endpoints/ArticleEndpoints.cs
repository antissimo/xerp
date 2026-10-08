using Xerp.Api.Http;
using Xerp.Application.Articles;
using Xerp.Application.Common;

namespace Xerp.Api.Endpoints;

public static class ArticleEndpoints
{
    public const string Route = "/articles";

    public static void MapArticleEndpoints(this IEndpointRouteBuilder v1)
    {
        var articles = v1.MapGroup(Route);

        articles.MapGet("", async (HttpRequest request, ArticleOperations operations, CancellationToken ct) =>
        {
            var input = BindList(request.Query);
            if (!input.IsSuccess)
                return Problems.From(input.Error);
            var result = await operations.ListAsync(input.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "type", "baseUnitId", "isActive", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path (E11).
        articles.MapGet("/{id:guid}", async (Guid id, ArticleOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        articles.MapGet("/by-code/{code}", async (string code, ArticleOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        articles.MapPost("", async (HttpRequest request, ArticleOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateArticleInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        articles.MapPut("/{id:guid}", async (Guid id, HttpRequest request, ArticleOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplaceArticleInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        articles.MapDelete("/{id:guid}", async (Guid id, ArticleOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }

    private static Result<ListArticlesInput> BindList(IQueryCollection query)
    {
        var binder = new QueryBinder(query);
        var input = new ListArticlesInput(
            binder.Text("search"), binder.Text("type"), binder.Text("baseUnitId"),
            binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"));
        return binder.Error is { } error ? error : input;
    }
}
