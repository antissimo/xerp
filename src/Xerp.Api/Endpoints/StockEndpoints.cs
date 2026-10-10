using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Stock;

namespace Xerp.Api.Endpoints;

/// <summary>Stock documents, stock on hand and the stock ledger (spec 005, section 4; spec 006). Thin: bind, call, map.</summary>
public static class StockEndpoints
{
    public const string DocumentsRoute = "/stock-documents";
    public const string OnHandRoute = "/stock-on-hand";
    public const string LedgerRoute = "/stock-ledger-entries";
    public const string BalanceDifferencesRoute = "/stock-balance-differences";
    public const string BalanceRebuildRoute = "/stock-balances/rebuild";

    public static void MapStockEndpoints(this IEndpointRouteBuilder v1)
    {
        var documents = v1.MapGroup(DocumentsRoute);

        documents.MapGet("", async (HttpRequest request, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListStockDocumentsInput(
                binder.Text("type"), binder.Text("status"), binder.Text("warehouseId"), binder.Text("search"),
                binder.Int("limit"), binder.Int("offset"), binder.Text("purchaseOrderId"), binder.Text("salesOrderId"), binder.Text("partnerId"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("type", "status", "warehouseId", "purchaseOrderId", "salesOrderId", "partnerId", "search", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path.
        documents.MapGet("/{id:guid}", async (Guid id, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        documents.MapGet("/by-number/{number}", async (string number, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByNumberAsync(number, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        documents.MapPost("", async (HttpRequest request, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateStockDocumentInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{DocumentsRoute}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        documents.MapPut("/{id:guid}", async (Guid id, HttpRequest request, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplaceStockDocumentInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        documents.MapDelete("/{id:guid}", async (Guid id, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        // No body: everything posting needs is the draft itself.
        documents.MapPost("/{id:guid}/post", async (Guid id, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var result = await operations.PostAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        // Spec 006, 4.2: answers with the reversing document, a new record.
        documents.MapPost("/{id:guid}/reverse", async (Guid id, HttpRequest request, StockDocumentOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadOrEmptyAsync<ReverseStockDocumentInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReverseAsync(id, body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{DocumentsRoute}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        v1.MapGet(OnHandRoute, async (HttpRequest request, StockQueries queries, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListStockOnHandInput(
                binder.Text("articleId"), binder.Text("warehouseId"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await queries.OnHandAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("articleId", "warehouseId", "limit", "offset");

        // The ledger has a read and nothing else: no route creates, changes or deletes an entry (4.3, S3).
        v1.MapGet(LedgerRoute, async (HttpRequest request, StockQueries queries, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListStockLedgerEntriesInput(
                binder.Text("articleId"), binder.Text("warehouseId"), binder.Text("documentId"),
                binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await queries.LedgerAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("articleId", "warehouseId", "documentId", "limit", "offset");

        // Spec 011, 4.5: verify. A read; an empty list is the healthy state.
        v1.MapGet(BalanceDifferencesRoute, async (HttpRequest request, StockBalances balances, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListStockBalanceDifferencesInput(binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await balances.DifferencesAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("limit", "offset");

        // Spec 011, 4.5: rebuild. No body is read: there is nothing a caller can say about a balance (S2).
        v1.MapPost(BalanceRebuildRoute, async (StockBalances balances, CancellationToken ct) =>
        {
            var result = await balances.RebuildAsync(ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }
}
