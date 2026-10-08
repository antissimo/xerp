using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.UnitsOfMeasure;

namespace Xerp.Api.Endpoints;

public static class UnitOfMeasureEndpoints
{
    public const string Route = "/units-of-measure";

    public static void MapUnitOfMeasureEndpoints(this IEndpointRouteBuilder v1)
    {
        var units = v1.MapGroup(Route);

        units.MapGet("", async (HttpRequest request, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var input = BindList(request.Query);
            if (!input.IsSuccess)
                return Problems.From(input.Error);
            var result = await operations.ListAsync(input.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "isActive", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path (E6).
        units.MapGet("/{id:guid}", async (Guid id, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        units.MapGet("/by-code/{code}", async (string code, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        units.MapPost("", async (HttpRequest request, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateUnitOfMeasureInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        units.MapPut("/{id:guid}", async (Guid id, HttpRequest request, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplaceUnitOfMeasureInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        units.MapDelete("/{id:guid}", async (Guid id, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }

    private static Result<ListUnitsOfMeasureInput> BindList(IQueryCollection query)
    {
        var binder = new QueryBinder(query);
        var input = new ListUnitsOfMeasureInput(binder.Text("search"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"));
        return binder.Error is { } error ? error : input;
    }
}
