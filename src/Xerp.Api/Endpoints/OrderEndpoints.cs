using Xerp.Api.Http;
using Xerp.Application.Orders;
using Xerp.Domain.Orders;

namespace Xerp.Api.Endpoints;

/// <summary>
/// The routes every kind of order has under the same shape (spec 009, section 4.1; spec 010, section 4.1):
/// get, get by number, delete, confirm, close, reopen. Thin: bind, call, map.
/// </summary>
public static class OrderEndpoints
{
    public static void MapShared<TOperations, TOrder, TLine, TDto, TSummary>(RouteGroupBuilder orders)
        where TOperations : OrderOperations<TOrder, TLine, TDto, TSummary>
        where TOrder : Order<TLine>
        where TLine : OrderLine
        where TDto : notnull
        where TSummary : notnull
    {
        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path.
        orders.MapGet("/{id:guid}", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapGet("/by-number/{number}", async (string number, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByNumberAsync(number, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapDelete("/{id:guid}", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        // No body: everything a transition needs is the order itself.
        orders.MapPost("/{id:guid}/confirm", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ConfirmAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/close", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.CloseAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/reopen", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ReopenAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }
}
