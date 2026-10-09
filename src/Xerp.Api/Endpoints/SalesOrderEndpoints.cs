using Xerp.Api.Http;
using Xerp.Application.Orders;

namespace Xerp.Api.Endpoints;

/// <summary>Sales orders (spec 010, section 4.1). Thin: bind, call, map. What needs no sales-order name is in <see cref="OrderEndpoints"/>.</summary>
public static class SalesOrderEndpoints
{
    public const string Route = "/sales-orders";

    public static void MapSalesOrderEndpoints(this IEndpointRouteBuilder v1)
    {
        var orders = v1.MapGroup(Route);

        orders.MapGet("", async (HttpRequest request, SalesOrderOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListSalesOrdersInput(
                binder.Text("status"), binder.Text("deliveryStatus"), binder.Text("customerId"), binder.Text("warehouseId"),
                binder.Text("search"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("status", "deliveryStatus", "customerId", "warehouseId", "search", "limit", "offset");

        orders.MapPost("", async (HttpRequest request, SalesOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateSalesOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        orders.MapPut("/{id:guid}", async (Guid id, HttpRequest request, SalesOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplaceSalesOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        OrderEndpoints.MapShared<SalesOrderOperations, Xerp.Domain.Orders.SalesOrder, Xerp.Domain.Orders.SalesOrderLine, SalesOrderDto, SalesOrderSummaryDto>(orders);
    }
}
