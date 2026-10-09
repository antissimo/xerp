using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Warehouses;

namespace Xerp.Api.Endpoints;

public static class WarehouseEndpoints
{
    public const string Route = "/warehouses";

    public static void MapWarehouseEndpoints(this IEndpointRouteBuilder v1)
    {
        var warehouses = v1.MapGroup(Route);

        warehouses.MapGet("", async (HttpRequest request, WarehouseOperations operations, CancellationToken ct) =>
        {
            var input = BindList(request.Query);
            if (!input.IsSuccess)
                return Problems.From(input.Error);
            var result = await operations.ListAsync(input.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "isActive", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path.
        warehouses.MapGet("/{id:guid}", async (Guid id, WarehouseOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        warehouses.MapGet("/by-code/{code}", async (string code, WarehouseOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        warehouses.MapPost("", async (HttpRequest request, WarehouseOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<WarehouseInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        warehouses.MapPut("/{id:guid}", async (Guid id, HttpRequest request, WarehouseOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<WarehouseInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        warehouses.MapDelete("/{id:guid}", async (Guid id, WarehouseOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }

    private static Result<ListWarehousesInput> BindList(IQueryCollection query)
    {
        var binder = new QueryBinder(query);
        var input = new ListWarehousesInput(binder.Text("search"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"));
        return binder.Error is { } error ? error : input;
    }
}
