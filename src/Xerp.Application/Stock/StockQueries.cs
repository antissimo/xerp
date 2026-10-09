using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Stock;

/// <summary>
/// Stock on hand and the stock ledger (spec 005, 4.2 and 4.3). Read-only: nothing here, and nothing anywhere
/// else, changes a ledger entry. <see cref="IXerpDb"/> is already filtered to the current tenant, so only the
/// caller's entries are listed and summed (T4).
/// </summary>
public sealed class StockQueries(IXerpDb db)
{
    /// <summary>
    /// One item per (article, warehouse) pair whose ledger sum, incoming or reserved quantity is not zero (R19,
    /// R20; spec 009, R35; spec 010, R15-R19). The incoming quantity is what the lines of confirmed purchase
    /// orders for the warehouse still expect; the reserved quantity what the lines of confirmed sales orders
    /// shipping from it still owe: ordered minus fulfilled, in base units. The three sums are made in one
    /// query, so total and paging count every pair once. Nothing here is stored, and nothing here blocks a
    /// movement (ADR-0017).
    /// </summary>
    public async Task<Result<PagedResult<StockOnHandDto>>> OnHandAsync(ListStockOnHandInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.OnHand(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var entries = db.StockLedgerEntries.AsNoTracking();
        if (query.ArticleId is { } articleId)
            entries = entries.Where(e => e.ArticleId == articleId);
        if (query.WarehouseId is { } warehouseId)
            entries = entries.Where(e => e.WarehouseId == warehouseId);

        var incoming = Outstanding<PurchaseOrder, PurchaseOrderLine>(db.PurchaseOrders, db.PurchaseOrderLines, query);
        var reserved = Outstanding<SalesOrder, SalesOrderLine>(db.SalesOrders, db.SalesOrderLines, query);

        var pairs = entries
            .Select(e => new PairQuantities { ArticleId = e.ArticleId, WarehouseId = e.WarehouseId, Quantity = e.Quantity, Incoming = 0m, Reserved = 0m })
            .Concat(incoming.Select(x => new PairQuantities { ArticleId = x.ArticleId, WarehouseId = x.WarehouseId, Quantity = 0m, Incoming = x.Quantity, Reserved = 0m }))
            .Concat(reserved.Select(x => new PairQuantities { ArticleId = x.ArticleId, WarehouseId = x.WarehouseId, Quantity = 0m, Incoming = 0m, Reserved = x.Quantity }))
            .GroupBy(x => new { x.ArticleId, x.WarehouseId })
            .Select(g => new
            {
                g.Key.ArticleId, g.Key.WarehouseId,
                Quantity = g.Sum(x => x.Quantity), Incoming = g.Sum(x => x.Incoming), Reserved = g.Sum(x => x.Reserved),
            })
            .Where(p => p.Quantity != 0 || p.Incoming != 0 || p.Reserved != 0);

        var total = await pairs.CountAsync(cancellationToken);
        var rows = await (
            from p in pairs
            join a in db.Articles.AsNoTracking() on p.ArticleId equals a.Id
            join u in db.UnitsOfMeasure.AsNoTracking() on a.BaseUnitId equals u.Id
            join w in db.Warehouses.AsNoTracking() on p.WarehouseId equals w.Id
            orderby EF.Property<string>(a, DbNames.CodeLower), EF.Property<string>(w, DbNames.CodeLower), a.Id, w.Id
            select new
            {
                p.ArticleId, ArticleCode = a.Code, ArticleName = a.Name,
                p.WarehouseId, WarehouseCode = w.Code, WarehouseName = w.Name,
                UnitId = u.Id, UnitCode = u.Code, UnitName = u.Name,
                p.Quantity,
                p.Incoming,
                p.Reserved,
            })
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new StockOnHandDto(
            new ReferenceSummary(r.ArticleId, r.ArticleCode, r.ArticleName),
            new ReferenceSummary(r.WarehouseId, r.WarehouseCode, r.WarehouseName),
            new ReferenceSummary(r.UnitId, r.UnitCode, r.UnitName),
            QuantityRules.Normalize(r.Quantity), QuantityRules.Normalize(r.Incoming), QuantityRules.Normalize(r.Reserved),
            QuantityRules.Normalize(StockAvailability.Available(r.Quantity, r.Reserved)))).ToList();
        return new PagedResult<StockOnHandDto>(items, total, query.Limit, query.Offset);
    }

    /// <summary>One row of the union the pairs are grouped from: a ledger entry, or what a confirmed order line still expects or owes.</summary>
    private sealed class PairQuantities
    {
        public Guid ArticleId { get; init; }
        public Guid WarehouseId { get; init; }
        public decimal Quantity { get; init; }
        public decimal Incoming { get; init; }
        public decimal Reserved { get; init; }
    }

    /// <summary>What one line of a confirmed order has outstanding, with its article and the order's warehouse.</summary>
    private sealed class OutstandingLine
    {
        public Guid ArticleId { get; init; }
        public Guid WarehouseId { get; init; }
        public decimal Quantity { get; init; }
    }

    /// <summary>
    /// What the lines of the confirmed orders of one kind have outstanding - the rule of
    /// <see cref="OrderProgress.Outstanding"/>, asked of the database. The same query gives the incoming
    /// quantity from purchase orders and the reserved quantity from sales orders.
    /// </summary>
    private static IQueryable<OutstandingLine> Outstanding<TOrder, TLine>(IQueryable<TOrder> orders, IQueryable<TLine> lines, StockOnHandQuery query)
        where TOrder : Order<TLine> where TLine : OrderLine
    {
        var confirmed = orders.AsNoTracking().Where(o => o.Status == OrderStatus.Confirmed);
        if (query.WarehouseId is { } warehouseId)
            confirmed = confirmed.Where(o => o.WarehouseId == warehouseId);
        var ordered = lines.AsNoTracking();
        if (query.ArticleId is { } articleId)
            ordered = ordered.Where(l => l.ArticleId == articleId);
        return
            from l in ordered
            join o in confirmed on l.OrderId equals o.Id
            select new OutstandingLine { ArticleId = l.ArticleId, WarehouseId = o.WarehouseId, Quantity = l.BaseQuantity!.Value - l.FulfilledBaseQuantity };
    }

    /// <summary>
    /// The ledger, oldest first: by posting time, then document number, then line (4.3); of the two entries
    /// of a transfer line the outgoing one comes first (spec 006, R6).
    /// </summary>
    public async Task<Result<PagedResult<StockLedgerEntryDto>>> LedgerAsync(ListStockLedgerEntriesInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.Ledger(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var entries = db.StockLedgerEntries.AsNoTracking();
        if (query.ArticleId is { } articleId)
            entries = entries.Where(e => e.ArticleId == articleId);
        if (query.WarehouseId is { } warehouseId)
            entries = entries.Where(e => e.WarehouseId == warehouseId);
        if (query.DocumentId is { } documentId)
            entries = entries.Where(e => e.DocumentId == documentId);

        var total = await entries.CountAsync(cancellationToken);
        var rows = await (
            from e in entries
            join d in db.StockDocuments.AsNoTracking() on e.DocumentId equals d.Id
            join a in db.Articles.AsNoTracking() on e.ArticleId equals a.Id
            join u in db.UnitsOfMeasure.AsNoTracking() on a.BaseUnitId equals u.Id
            join w in db.Warehouses.AsNoTracking() on e.WarehouseId equals w.Id
            orderby e.PostedAt, d.Number, e.LineNo, e.Quantity, e.Id
            select new
            {
                Entry = e,
                DocumentNumber = d.Number, DocumentType = d.Type, IsReversal = d.ReversalOfId != null,
                ArticleCode = a.Code, ArticleName = a.Name,
                WarehouseCode = w.Code, WarehouseName = w.Name,
                UnitId = u.Id, UnitCode = u.Code, UnitName = u.Name,
            })
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new StockLedgerEntryDto(
            r.Entry.Id,
            new ReferenceSummary(r.Entry.ArticleId, r.ArticleCode, r.ArticleName),
            new ReferenceSummary(r.Entry.WarehouseId, r.WarehouseCode, r.WarehouseName),
            new ReferenceSummary(r.UnitId, r.UnitCode, r.UnitName),
            QuantityRules.Normalize(r.Entry.Quantity),
            r.Entry.DocumentDate, r.Entry.PostedAt, r.Entry.PostedBy,
            new LedgerDocumentDto(r.Entry.DocumentId, r.DocumentNumber!, r.DocumentType.ToName(), r.IsReversal),
            r.Entry.LineNo)).ToList();
        return new PagedResult<StockLedgerEntryDto>(items, total, query.Limit, query.Offset);
    }
}
