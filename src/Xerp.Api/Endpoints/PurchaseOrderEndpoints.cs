using Xerp.Api.Http;
using Xerp.Application.Orders;

namespace Xerp.Api.Endpoints;

/// <summary>Purchase orders (spec 009, section 4.1). Thin: bind, call, map.</summary>
public static class PurchaseOrderEndpoints
{
    public const string Route = "/purchase-orders";

    public static void MapPurchaseOrderEndpoints(this IEndpointRouteBuilder v1)
    {
        var orders = v1.MapGroup(Route);

        orders.MapGet("", async (HttpRequest request, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListPurchaseOrdersInput(
                binder.Text("status"), binder.Text("receiptStatus"), binder.Text("supplierId"), binder.Text("warehouseId"),
                binder.Text("search"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("status", "receiptStatus", "supplierId", "warehouseId", "search", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path.
        orders.MapGet("/{id:guid}", async (Guid id, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapGet("/by-number/{number}", async (string number, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByNumberAsync(number, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("", async (HttpRequest request, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreatePurchaseOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        orders.MapPut("/{id:guid}", async (Guid id, HttpRequest request, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplacePurchaseOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapDelete("/{id:guid}", async (Guid id, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        // No body: everything a transition needs is the order itself.
        orders.MapPost("/{id:guid}/confirm", async (Guid id, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ConfirmAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/close", async (Guid id, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.CloseAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/reopen", async (Guid id, PurchaseOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ReopenAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }
}
