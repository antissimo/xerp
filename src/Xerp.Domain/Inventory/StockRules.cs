namespace Xerp.Domain.Inventory;

/// <summary>
/// A line as it was entered (spec 007, R12, R13): an article, a unit of that article - its base unit or one of
/// its alternative units - and a quantity in that unit.
/// </summary>
public readonly record struct StockLineEntry(Guid ArticleId, Guid UnitId, decimal Quantity);

/// <summary>
/// What a line means for stock: an article and a quantity in the article's base unit (spec 007, R19). The
/// ledger, the stock check and every movement are computed from these, never from an entered quantity.
/// </summary>
public readonly record struct StockLineValues(Guid ArticleId, decimal Quantity);

/// <summary>
/// Rule for a quantity on a document line (ADR-0012, decision 7; spec 005, R6): an exact decimal greater than
/// zero, with at most six decimal places, at most 999,999,999.999999. A value outside the rule is rejected,
/// never rounded.
/// </summary>
public static class QuantityRules
{
    public const int DecimalPlaces = 6;
    public const decimal Max = 999_999_999.999999m;

    public static bool IsValid(decimal quantity) =>
        quantity > 0 && quantity <= Max && decimal.Round(quantity, DecimalPlaces) == quantity;

    /// <summary>The same value without trailing zeros (<c>100.000000</c> becomes <c>100</c>), so it is written one way everywhere.</summary>
    public static decimal Normalize(decimal quantity) => quantity / 1.0000000000000000000000000000m;
}

/// <summary>
/// The number a stock document gets when it is posted (ADR-0012, decision 6; spec 005, R17): the prefix of its
/// type and the tenant's counter value, padded with zeros to at least six digits.
/// </summary>
public static class DocumentNumber
{
    public const int MinDigits = 6;

    public static string Prefix(StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => "SR",
        StockDocumentType.Issue => "SI",
        StockDocumentType.Transfer => "ST",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string Format(StockDocumentType type, long counter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(counter, 1);
        return $"{Prefix(type)}-{counter.ToString("D" + MinDigits, System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// No negative stock (ADR-0012, decision 4; spec 005, R15): for every article of an outgoing document, the sum
/// of its line quantities must not exceed the stock on hand of that article in the document's warehouse.
/// </summary>
public static class StockSufficiency
{
    /// <summary>
    /// The zero-based positions of all lines of every short article, ascending; empty when stock covers the
    /// document. An article missing from <paramref name="onHand"/> has zero stock.
    /// </summary>
    public static IReadOnlyList<int> ShortLines(IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, decimal> onHand)
    {
        var shortArticles = lines
            .GroupBy(l => l.ArticleId)
            .Where(g => g.Sum(l => l.Quantity) > onHand.GetValueOrDefault(g.Key))
            .Select(g => g.Key)
            .ToHashSet();
        return Enumerable.Range(0, lines.Count).Where(i => shortArticles.Contains(lines[i].ArticleId)).ToList();
    }
}

/// <summary>A signed quantity of an article in a warehouse: what one ledger entry adds to stock on hand.</summary>
public readonly record struct StockMovement(Guid ArticleId, Guid WarehouseId, decimal Quantity);

/// <summary>The destination of a transfer (spec 006, R1-R3).</summary>
public static class TransferRules
{
    /// <summary>A transfer has a destination other than its source; a receipt or an issue has none.</summary>
    public static bool IsValidDestination(StockDocumentType type, Guid warehouseId, Guid? toWarehouseId) =>
        type == StockDocumentType.Transfer
            ? toWarehouseId is { } destination && destination != warehouseId
            : toWarehouseId is null;
}

/// <summary>
/// The sign rules of the ledger (spec 005, R14; spec 006, R6, R14) and what they do to stock on hand.
/// </summary>
public static class StockMovements
{
    /// <summary>
    /// What posting one line moves: <c>+quantity</c> for a receipt, <c>-quantity</c> for an issue, and for a
    /// transfer <c>-quantity</c> in the source followed by <c>+quantity</c> in the destination - so a
    /// transfer sums to zero per article (conservation, R8).
    /// </summary>
    public static IReadOnlyList<StockMovement> OfLine(StockDocumentType type, Guid warehouseId, Guid? toWarehouseId, StockLineValues line) =>
        type switch
        {
            StockDocumentType.Receipt => [new(line.ArticleId, warehouseId, line.Quantity)],
            StockDocumentType.Issue => [new(line.ArticleId, warehouseId, -line.Quantity)],
            StockDocumentType.Transfer when toWarehouseId is { } destination && destination != warehouseId =>
                [new(line.ArticleId, warehouseId, -line.Quantity), new(line.ArticleId, destination, line.Quantity)],
            StockDocumentType.Transfer => throw new InvalidOperationException("A transfer needs a destination warehouse other than its source."),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    /// <summary>What reversing a movement moves: the same article and warehouse, the opposite quantity (R14).</summary>
    public static StockMovement Opposite(StockMovement movement) => movement with { Quantity = -movement.Quantity };

    /// <summary>
    /// No negative stock (ADR-0012, decision 4): the articles that some (article, warehouse) pair would go
    /// below zero for if all <paramref name="movements"/> were applied together. A pair missing from
    /// <paramref name="onHand"/> has zero stock. Only the net effect per pair counts, never the order.
    /// </summary>
    public static IReadOnlySet<Guid> ShortArticles(
        IEnumerable<StockMovement> movements, IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand) =>
        movements
            .GroupBy(m => (m.ArticleId, m.WarehouseId))
            .Where(g => onHand.GetValueOrDefault(g.Key) + g.Sum(m => m.Quantity) < 0)
            .Select(g => g.Key.ArticleId)
            .ToHashSet();
}
