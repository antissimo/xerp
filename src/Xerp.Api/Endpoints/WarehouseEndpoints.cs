using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Stock;
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
        }).AcceptsQuery("search", "isActive", "isDefault", "limit", "offset");

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

        // Spec 011, 4.1. No body: the warehouse to make the default is the addressed one.
        warehouses.MapPost("/{id:guid}/set-default", async (Guid id, WarehouseOperations operations, CancellationToken ct) =>
        {
            var result = await operations.SetDefaultAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        // Spec 011, 4.3: the stock list of one warehouse - every stock article, zero included.
        warehouses.MapGet("/{id:guid}/stock", async (Guid id, HttpRequest request, StockQueries queries, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListWarehouseStockInput(
                null, binder.Text("search"), binder.Bool("isActive"), binder.Bool("hasStock"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await queries.WarehouseStockAsync(id, input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "isActive", "hasStock", "limit", "offset");
    }

    private static Result<ListWarehousesInput> BindList(IQueryCollection query)
    {
        var binder = new QueryBinder(query);
        var input = new ListWarehousesInput(
            binder.Text("search"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"), binder.Bool("isDefault"));
        return binder.Error is { } error ? error : input;
    }
}
