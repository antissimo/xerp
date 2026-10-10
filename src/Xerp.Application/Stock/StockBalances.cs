using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>A pair whose stored balance is not the sum of its ledger entries (spec 011, 4.5). <c>DifferenceQuantity</c> is stored minus ledger.</summary>
public sealed record StockBalanceDifferenceDto(
    ReferenceSummary Article, ReferenceSummary Warehouse, ReferenceSummary Unit,
    decimal StoredQuantity, decimal LedgerQuantity, decimal DifferenceQuantity);

public sealed record ListStockBalanceDifferencesInput(int? Limit = null, int? Offset = null);

/// <summary>
/// The result of a rebuild (spec 011, R25): <c>Pairs</c> is the number of (article, warehouse) pairs that have
/// ledger entries, <c>Corrected</c> the number of pairs whose stored quantity differed from the ledger before it.
/// </summary>
public sealed record StockBalanceRebuildDto(int Pairs, int Corrected);

/// <summary>
/// The stored stock balance (ADR-0018; spec 011, R19-R28): per warehouse and article, a copy of one number -
/// the sum of the pair's ledger entries. The ledger is the truth; the balance is derived from it.
/// <para>
/// <b>This class is the only code that writes <see cref="IXerpDb.StockBalances"/></b> (spec 011, AC-05), in
/// exactly two places: <see cref="AddAsync"/>, called wherever ledger entries are added - the posting and the
/// reversal of every type of stock document - inside the transaction that holds the tenant's lock and before
/// its one save, so entries and balances commit together or not at all; and <see cref="RebuildAsync"/>.
/// Nothing else writes a balance: no endpoint, no tool, no draft, no order, no change of a master.
/// </para>
/// <see cref="IXerpDb"/> is already filtered to the current tenant: every read and write here is the caller's
/// tenant's alone (T5).
/// </summary>
public sealed class StockBalances(IXerpDb db, ITenantContext context, ILogger<StockBalances> logger)
{
    /// <summary>
    /// Stock on hand of the given articles in the given warehouses, as stored (R23). A pair without a row is
    /// absent and has none. Every decision about stock - sufficiency, the book quantity of a count, whether a
    /// count is still current - reads this, under the tenant's lock.
    /// </summary>
    internal static async Task<Dictionary<(Guid ArticleId, Guid WarehouseId), decimal>> OnHandAsync(
        IXerpDb db, IEnumerable<Guid> articles, IEnumerable<Guid> warehouses, CancellationToken cancellationToken)
    {
        var articleIds = articles.Distinct().ToList();
        var warehouseIds = warehouses.Distinct().ToList();
        var balances = await db.StockBalances.AsNoTracking()
            .Where(b => warehouseIds.Contains(b.WarehouseId) && articleIds.Contains(b.ArticleId))
            .Select(b => new { b.ArticleId, b.WarehouseId, b.Quantity })
            .ToListAsync(cancellationToken);
        return balances.ToDictionary(b => (b.ArticleId, b.WarehouseId), b => b.Quantity);
    }

    /// <summary>
    /// R21 (a): adds the signed quantity of every entry to the balance of its pair. The caller holds the
    /// tenant's lock, has added <paramref name="entries"/> to the ledger in the same unit of work and saves
    /// once afterwards; nothing is saved here. A pair without a row gets one.
    /// </summary>
    internal static async Task AddAsync(IXerpDb db, Guid tenantId, IReadOnlyCollection<StockLedgerEntry> entries, CancellationToken cancellationToken)
    {
        var changes = StockBalanceRules.Changes(entries.Select(e => new StockMovement(e.ArticleId, e.WarehouseId, e.Quantity)));
        if (changes.Count == 0)
            return; // a count without differences writes no entry and changes no balance (E14)
        var stored = await TrackedAsync(db, changes.Keys, cancellationToken);
        foreach (var (pair, change) in changes)
        {
            if (!stored.TryGetValue(pair, out var balance))
            {
                balance = StockBalance.Start(tenantId, pair.WarehouseId, pair.ArticleId);
                db.StockBalances.Add(balance);
            }
            balance.Add(change);
        }
    }

    /// <summary>The balance rows of the given pairs, tracked for a write.</summary>
    private static async Task<Dictionary<(Guid ArticleId, Guid WarehouseId), StockBalance>> TrackedAsync(
        IXerpDb db, IEnumerable<(Guid ArticleId, Guid WarehouseId)> pairs, CancellationToken cancellationToken)
    {
        var wanted = pairs.ToHashSet();
        var articleIds = wanted.Select(p => p.ArticleId).Distinct().ToList();
        var warehouseIds = wanted.Select(p => p.WarehouseId).Distinct().ToList();
        var rows = await db.StockBalances
            .Where(b => warehouseIds.Contains(b.WarehouseId) && articleIds.Contains(b.ArticleId))
            .ToListAsync(cancellationToken);
        return rows.Where(b => wanted.Contains((b.ArticleId, b.WarehouseId))).ToDictionary(b => (b.ArticleId, b.WarehouseId));
    }

    /// <summary>
    /// Verify (R24): the pairs of the tenant whose stored balance differs from the sum of their ledger entries,
    /// ordered by article code, then warehouse code. Balances and ledger are compared inside one statement,
    /// and the count and the page are read from one snapshot, so a posting in progress is seen wholly or not
    /// at all and never shows up as a difference. It takes no lock and changes nothing; an empty list is the
    /// healthy state.
    /// </summary>
    public async Task<Result<PagedResult<StockBalanceDifferenceDto>>> DifferencesAsync(
        ListStockBalanceDifferencesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        var (total, rows) = await db.AtOneMomentAsync(async ct =>
        {
            var differences = Differences();
            var count = await differences.CountAsync(ct);
            var page = count == 0
                ? []
                : await (
                    from d in differences
                    join a in db.Articles.AsNoTracking() on d.ArticleId equals a.Id
                    join u in db.UnitsOfMeasure.AsNoTracking() on a.BaseUnitId equals u.Id
                    join w in db.Warehouses.AsNoTracking() on d.WarehouseId equals w.Id
                    orderby EF.Property<string>(a, DbNames.CodeLower), EF.Property<string>(w, DbNames.CodeLower), a.Id, w.Id
                    select new
                    {
                        d.ArticleId, ArticleCode = a.Code, ArticleName = a.Name,
                        d.WarehouseId, WarehouseCode = w.Code, WarehouseName = w.Name,
                        UnitId = u.Id, UnitCode = u.Code, UnitName = u.Name,
                        d.Stored, d.Ledger,
                    })
                    .Skip(offset)
                    .Take(limit)
                    .ToListAsync(ct);
            return (count, page);
        }, cancellationToken);

        // S5: a difference is evidence of a defect, never of the caller's data.
        foreach (var r in rows)
            LogDifference("found by verify", r.ArticleId, r.WarehouseId, r.Stored, r.Ledger);
        var items = rows.Select(r => new StockBalanceDifferenceDto(
            new ReferenceSummary(r.ArticleId, r.ArticleCode, r.ArticleName),
            new ReferenceSummary(r.WarehouseId, r.WarehouseCode, r.WarehouseName),
            new ReferenceSummary(r.UnitId, r.UnitCode, r.UnitName),
            QuantityRules.Normalize(r.Stored), QuantityRules.Normalize(r.Ledger), QuantityRules.Normalize(r.Stored - r.Ledger))).ToList();
        return new PagedResult<StockBalanceDifferenceDto>(items, total, limit, offset);
    }

    /// <summary>
    /// Rebuild (R25, R21 (b)): makes the stored balances of the tenant equal to the sums of its ledger, in one
    /// transaction under the tenant's lock - so it is ordered against every posting and reversal (R28). The
    /// ledger alone decides the result (S3). The differing pairs are found by the database (the query of
    /// verify) and only those rows are written; a row that should be 0 because its pair has no entries is
    /// removed, since no row means 0. It reads the ledger and writes only balances: no document, entry,
    /// number or audit field changes. Safe to repeat: a second call corrects nothing.
    /// </summary>
    public Task<Result<StockBalanceRebuildDto>> RebuildAsync(CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<StockBalanceRebuildDto>>(async ct =>
        {
            var tenantId = context.TenantId ?? throw new InvalidOperationException("Stock balances can only be rebuilt for a tenant.");
            var pairs = await db.StockLedgerEntries.AsNoTracking()
                .Select(e => new { e.ArticleId, e.WarehouseId })
                .Distinct()
                .CountAsync(ct);
            var differences = await Differences().ToListAsync(ct);
            if (differences.Count > 0)
            {
                var stored = await TrackedAsync(db, differences.Select(d => (d.ArticleId, d.WarehouseId)), ct);
                foreach (var d in differences)
                {
                    LogDifference("corrected by rebuild", d.ArticleId, d.WarehouseId, d.Stored, d.Ledger);
                    if (!stored.TryGetValue((d.ArticleId, d.WarehouseId), out var balance))
                    {
                        // The row is missing: its pair has entries that do not sum to zero.
                        balance = StockBalance.Start(tenantId, d.WarehouseId, d.ArticleId);
                        db.StockBalances.Add(balance);
                        balance.CorrectTo(d.Ledger);
                    }
                    else if (d.Entries == 0)
                        db.StockBalances.Remove(balance); // nothing was ever posted for the pair
                    else
                        balance.CorrectTo(d.Ledger);
                }
                await db.SaveChangesAsync(ct);
            }
            return new StockBalanceRebuildDto(pairs, differences.Count);
        }, cancellationToken);

    /// <summary>One side of the comparison: a stored balance, or one ledger entry.</summary>
    private sealed class PairSide
    {
        public Guid ArticleId { get; init; }
        public Guid WarehouseId { get; init; }
        public decimal Stored { get; init; }
        public decimal Ledger { get; init; }
        public int Entries { get; init; }
    }

    /// <summary>A pair with its stored quantity (0 without a row), the sum of its ledger entries (0 without entries) and their number.</summary>
    private sealed class PairDifference
    {
        public Guid ArticleId { get; init; }
        public Guid WarehouseId { get; init; }
        public decimal Stored { get; init; }
        public decimal Ledger { get; init; }
        public int Entries { get; init; }
    }

    /// <summary>
    /// The pairs whose stored balance is not the sum of their ledger entries (R20, R24) - one statement, so
    /// both sides are of one moment. A pair without a row counts as 0, a pair without entries as 0.
    /// </summary>
    private IQueryable<PairDifference> Differences() =>
        db.StockBalances.AsNoTracking()
            .Select(b => new PairSide { ArticleId = b.ArticleId, WarehouseId = b.WarehouseId, Stored = b.Quantity, Ledger = 0m, Entries = 0 })
            .Concat(db.StockLedgerEntries.AsNoTracking()
                .Select(e => new PairSide { ArticleId = e.ArticleId, WarehouseId = e.WarehouseId, Stored = 0m, Ledger = e.Quantity, Entries = 1 }))
            .GroupBy(x => new { x.ArticleId, x.WarehouseId })
            .Select(g => new PairDifference
            {
                ArticleId = g.Key.ArticleId, WarehouseId = g.Key.WarehouseId,
                Stored = g.Sum(x => x.Stored), Ledger = g.Sum(x => x.Ledger), Entries = g.Sum(x => x.Entries),
            })
            .Where(p => p.Stored != p.Ledger);

    private void LogDifference(string how, Guid articleId, Guid warehouseId, decimal stored, decimal ledger) =>
        logger.LogError(
            "Stock balance difference {How}: tenant {TenantId}, article {ArticleId}, warehouse {WarehouseId}, stored {StoredQuantity}, ledger {LedgerQuantity}.",
            how, context.TenantId, articleId, warehouseId, stored, ledger);
}
