using Xerp.Domain.Common;

namespace Xerp.Domain.Inventory;

/// <summary>
/// The stored quantity of one article in one warehouse, in the article's base unit (ADR-0018). It is derived:
/// by definition the sum of the pair's ledger entries, written together with them and never trusted over them.
/// A pair without a row has quantity 0. It has no audit columns - it is a sum, not a record of an act; who
/// moved stock and when is in the ledger.
/// </summary>
public sealed class StockBalance : ITenantOwned
{
    private StockBalance() { }

    public Guid TenantId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ArticleId { get; private set; }

    /// <summary>Never negative (ADR-0012, decision 4); the database checks it too.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>The balance of a pair that had no row: zero.</summary>
    public static StockBalance Start(Guid tenantId, Guid warehouseId, Guid articleId) =>
        new() { TenantId = tenantId, WarehouseId = warehouseId, ArticleId = articleId };

    /// <summary>Adds the signed quantity of ledger entries of this pair (spec 011, R21).</summary>
    /// <exception cref="InvalidOperationException">The balance would go below zero; it is unchanged.</exception>
    public void Add(decimal quantity)
    {
        if (Quantity + quantity < 0)
            throw new InvalidOperationException("A stock balance cannot go below zero.");
        Quantity += quantity;
    }

    /// <summary>Replaces the stored quantity by the sum of the pair's ledger entries (spec 011, R25: rebuild).</summary>
    /// <exception cref="InvalidOperationException">The sum is negative; the balance is unchanged.</exception>
    public void CorrectTo(decimal ledgerQuantity)
    {
        if (ledgerQuantity < 0)
            throw new InvalidOperationException("A stock balance cannot be below zero.");
        Quantity = ledgerQuantity;
    }
}

/// <summary>What ledger entries do to the stored balances (spec 011, R21).</summary>
public static class StockBalanceRules
{
    /// <summary>
    /// The change of every (article, warehouse) pair the movements touch: the sum of their signed quantities.
    /// A pair whose movements cancel is still returned, with 0.
    /// </summary>
    public static IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> Changes(IEnumerable<StockMovement> movements) =>
        movements
            .GroupBy(m => (m.ArticleId, m.WarehouseId))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
}
