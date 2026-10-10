using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Stock;

/// <summary>
/// Stock on hand, the stock list of a warehouse and the stock ledger (spec 005, 4.2 and 4.3; spec 011, 4.3).
/// Read-only: nothing here, and nothing anywhere else, changes a ledger entry, and nothing here writes a balance. <see cref="IXerpDb"/> is already filtered to the current tenant, so only the
/// caller's entries are listed and summed (T4).
/// </summary>
public sealed class StockQueries(IXerpDb db)
{
    /// <summary>
    /// One item per (article, warehouse) pair whose stock, incoming or reserved quantity is not zero (R19,
    /// R20; spec 009, R35; spec 010, R15-R19). The stock is the stored balance of the pair (spec 011, R23),
    /// which equals the sum of its ledger entries. The incoming quantity is what the lines of confirmed
    /// purchase orders for the warehouse still expect; the reserved quantity what the lines of confirmed sales
    /// orders shipping from it still owe: ordered minus fulfilled, in base units. The three are put together
    /// in one query, so total and paging count every pair once. Nothing here blocks a movement: this is a read
    /// (whether reserved stock may be taken is decided at posting, by the rule <c>sales.reservedStockProtected</c>).
    /// </summary>
    public async Task<Result<PagedResult<StockOnHandDto>>> OnHandAsync(ListStockOnHandInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.OnHand(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var pairs = Pairs(query.ArticleId, query.WarehouseId).Where(p => p.Quantity != 0 || p.Incoming != 0 || p.Reserved != 0);

        var total = await pairs.CountAsync(cancellationToken);
        var rows = await (
            from p in pairs
            join a in db.Articles.AsNoTracking() on p.ArticleId equals a.Id
            join u in db.UnitsOfMeasure.AsNoTracking() on a.BaseUnitId equals u.Id
            join w in db.Warehouses.AsNoTracking() on p.WarehouseId equals w.Id
            orderby EF.Property<string>(a, DbNames.CodeLower), EF.Property<string>(w, DbNames.CodeLower), a.Id, w.Id
            select new StockRow
            {
                ArticleId = p.ArticleId, ArticleCode = a.Code, ArticleName = a.Name,
                WarehouseId = p.WarehouseId, WarehouseCode = w.Code, WarehouseName = w.Name,
                UnitId = u.Id, UnitCode = u.Code, UnitName = u.Name,
                Quantity = p.Quantity, Incoming = p.Incoming, Reserved = p.Reserved,
            })
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        return new PagedResult<StockOnHandDto>(rows.Select(ToDto).ToList(), total, query.Limit, query.Offset);
    }

    /// <summary>
    /// The stock list of one warehouse (spec 011, R13-R17): one item per stock article of the tenant - active
    /// or not, moved in this warehouse or not, zero included - ordered by article code. The four quantities
    /// are those of the pair as stock on hand has them, from the same source (<see cref="Pairs"/>), and 0
    /// where the pair has nothing; the list starts from the articles instead of from the pairs. One statement
    /// gives the page, one the total. A read: it writes no balance rows.
    /// </summary>
    public async Task<Result<PagedResult<StockOnHandDto>>> WarehouseStockAsync(
        Guid warehouseId, ListWarehouseStockInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        // R16: the warehouse is the addressed record; an inactive one has a stock list like any other.
        var warehouse = await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == warehouseId)
            .Select(w => new ReferenceSummary(w.Id, w.Code, w.Name))
            .SingleOrDefaultAsync(cancellationToken);
        if (warehouse is null)
            return AppError.NotFound(WarehouseNotFoundDetail);

        // R13, R15: every stock article; search and isActive are those of the article list (spec 002, R18).
        var articles = db.Articles.AsNoTracking().Where(a => a.Type == ArticleType.Stock);
        if (input.IsActive is { } isActive)
            articles = articles.Where(a => a.IsActive == isActive);
        if (search is not null)
        {
            var pattern = LikePattern.Contains(search);
            articles = articles.Where(a =>
                EF.Functions.Like(EF.Property<string>(a, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(a.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }

        var pairs = Pairs(articleId: null, warehouseId);
        var listed =
            from a in articles
            join p in pairs on a.Id equals p.ArticleId into found
            from p in found.DefaultIfEmpty()
            select new
            {
                Article = a,
                Quantity = (decimal?)p!.Quantity ?? 0m, Incoming = (decimal?)p!.Incoming ?? 0m, Reserved = (decimal?)p!.Reserved ?? 0m,
            };
        if (input.HasStock is { } hasStock)
            // Spec 012, R20: below zero - possible where the tenant allows negative stock - is no stock either.
            listed = hasStock ? listed.Where(x => x.Quantity > 0) : listed.Where(x => x.Quantity <= 0);

        var total = await listed.CountAsync(cancellationToken);
        var rows = await (
            from x in listed
            join u in db.UnitsOfMeasure.AsNoTracking() on x.Article.BaseUnitId equals u.Id
            orderby EF.Property<string>(x.Article, DbNames.CodeLower), x.Article.Id
            select new StockRow
            {
                ArticleId = x.Article.Id, ArticleCode = x.Article.Code, ArticleName = x.Article.Name,
                WarehouseId = warehouse.Id, WarehouseCode = warehouse.Code, WarehouseName = warehouse.Name,
                UnitId = u.Id, UnitCode = u.Code, UnitName = u.Name,
                Quantity = x.Quantity, Incoming = x.Incoming, Reserved = x.Reserved,
            })
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return new PagedResult<StockOnHandDto>(rows.Select(ToDto).ToList(), total, limit, offset);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PagedResult<StockOnHandDto>>> WarehouseStockAsync(ListWarehouseStockInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.Id, WarehouseNotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PagedResult<StockOnHandDto>>>(error)
            : WarehouseStockAsync(parsed, input, cancellationToken);

    private const string WarehouseNotFoundDetail = "Warehouse not found.";

    /// <summary>
    /// What stock on hand and the stock list show of a pair, from one source: the stored balance, and what
    /// confirmed purchase and sales orders have outstanding. One row per (article, warehouse) pair that has a
    /// balance row or an outstanding order line; a quantity the pair does not have is 0.
    /// </summary>
    private IQueryable<PairQuantities> Pairs(Guid? articleId, Guid? warehouseId)
    {
        var balances = db.StockBalances.AsNoTracking();
        if (articleId is { } article)
            balances = balances.Where(b => b.ArticleId == article);
        if (warehouseId is { } warehouse)
            balances = balances.Where(b => b.WarehouseId == warehouse);

        var incoming = Outstanding<PurchaseOrder, PurchaseOrderLine>(db.PurchaseOrders, db.PurchaseOrderLines, articleId, warehouseId);
        var reserved = Outstanding<SalesOrder, SalesOrderLine>(db.SalesOrders, db.SalesOrderLines, articleId, warehouseId);

        return balances
            .Select(b => new PairQuantities { ArticleId = b.ArticleId, WarehouseId = b.WarehouseId, Quantity = b.Quantity, Incoming = 0m, Reserved = 0m })
            .Concat(incoming.Select(x => new PairQuantities { ArticleId = x.ArticleId, WarehouseId = x.WarehouseId, Quantity = 0m, Incoming = x.Quantity, Reserved = 0m }))
            .Concat(reserved.Select(x => new PairQuantities { ArticleId = x.ArticleId, WarehouseId = x.WarehouseId, Quantity = 0m, Incoming = 0m, Reserved = x.Quantity }))
            .GroupBy(x => new { x.ArticleId, x.WarehouseId })
            .Select(g => new PairQuantities
            {
                ArticleId = g.Key.ArticleId, WarehouseId = g.Key.WarehouseId,
                Quantity = g.Sum(x => x.Quantity), Incoming = g.Sum(x => x.Incoming), Reserved = g.Sum(x => x.Reserved),
            });
    }

    /// <summary>A pair with what is shown of its masters, as the database returns it.</summary>
    private sealed class StockRow
    {
        public Guid ArticleId { get; init; }
        public string ArticleCode { get; init; } = "";
        public string ArticleName { get; init; } = "";
        public Guid WarehouseId { get; init; }
        public string WarehouseCode { get; init; } = "";
        public string WarehouseName { get; init; } = "";
        public Guid UnitId { get; init; }
        public string UnitCode { get; init; } = "";
        public string UnitName { get; init; } = "";
        public decimal Quantity { get; init; }
        public decimal Incoming { get; init; }
        public decimal Reserved { get; init; }
    }

    /// <summary>The one representation of a pair: stock on hand and the stock list return the same item (spec 011, R14).</summary>
    private static StockOnHandDto ToDto(StockRow r) => new(
        new ReferenceSummary(r.ArticleId, r.ArticleCode, r.ArticleName),
        new ReferenceSummary(r.WarehouseId, r.WarehouseCode, r.WarehouseName),
        new ReferenceSummary(r.UnitId, r.UnitCode, r.UnitName),
        QuantityRules.Normalize(r.Quantity), QuantityRules.Normalize(r.Incoming), QuantityRules.Normalize(r.Reserved),
        QuantityRules.Normalize(StockAvailability.Available(r.Quantity, r.Reserved)));

    /// <summary>What the lines of the confirmed orders of one kind have outstanding, for the article and the warehouse asked for.</summary>
    private static IQueryable<OrderReads.OutstandingLine> Outstanding<TOrder, TLine>(
        IQueryable<TOrder> orders, IQueryable<TLine> lines, Guid? articleId, Guid? warehouseId)
        where TOrder : Order<TLine> where TLine : OrderLine
    {
        var outstanding = OrderReads.Outstanding<TOrder, TLine>(orders, lines);
        if (articleId is { } article)
            outstanding = outstanding.Where(l => l.ArticleId == article);
        if (warehouseId is { } warehouse)
            outstanding = outstanding.Where(l => l.WarehouseId == warehouse);
        return outstanding;
    }

    /// <summary>The quantities of a pair - and one row of the union they are summed from: a stored balance, or what a confirmed order line still expects or owes.</summary>
    private sealed class PairQuantities
    {
        public Guid ArticleId { get; init; }
        public Guid WarehouseId { get; init; }
        public decimal Quantity { get; init; }
        public decimal Incoming { get; init; }
        public decimal Reserved { get; init; }
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
