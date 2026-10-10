using Xerp.Domain.Orders;

namespace Xerp.Domain.Inventory;

/// <summary>
/// A line as it was entered (spec 007, R12, R13): an article, a unit of that article - its base unit or one of
/// its alternative units - and a quantity in that unit.
/// </summary>
/// <param name="OrderLineNo">On a document linked to an order: the order line this line fulfils (spec 009, R20); otherwise null.</param>
public readonly record struct StockLineEntry(Guid ArticleId, Guid UnitId, decimal Quantity, int? OrderLineNo = null);

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

    /// <summary>
    /// The rule for the line of a document of the given type (spec 008, R4): on a count the quantity is what
    /// was counted and may be zero - none found; on every other type it moves stock and must be greater than zero.
    /// </summary>
    public static bool IsValidOn(StockDocumentType type, decimal quantity) =>
        IsValid(quantity) || (type == StockDocumentType.Count && quantity == 0);

    /// <summary>The same value without trailing zeros (<c>100.000000</c> becomes <c>100</c>), so it is written one way everywhere.</summary>
    public static decimal Normalize(decimal quantity) => quantity / 1.0000000000000000000000000000m;
}

/// <summary>
/// A number series of a tenant (ADR-0012, decision 6; spec 009, R13): the key its counter is kept under and the
/// prefix of its numbers. Every stock document type has one, and so has every kind of order.
/// </summary>
public readonly record struct DocumentSeries(string Key, string Prefix)
{
    public static readonly DocumentSeries PurchaseOrder = new("purchaseOrder", "PO");
    public static readonly DocumentSeries SalesOrder = new("salesOrder", "SO");

    public static DocumentSeries Of(StockDocumentType type) => new(type.ToName(), DocumentNumber.Prefix(type));
}

/// <summary>
/// The number a document gets when it is posted or confirmed (ADR-0012, decision 6; spec 005, R17): the prefix
/// of its series and the tenant's counter value, padded with zeros to at least six digits.
/// </summary>
public static class DocumentNumber
{
    public const int MinDigits = 6;

    public static string Prefix(StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => "SR",
        StockDocumentType.Issue => "SI",
        StockDocumentType.Transfer => "ST",
        StockDocumentType.Count => "SC",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string Format(StockDocumentType type, long counter) => Format(DocumentSeries.Of(type), counter);

    public static string Format(DocumentSeries series, long counter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(counter, 1);
        return $"{series.Prefix}-{counter.ToString("D" + MinDigits, System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

/// <summary>A signed quantity of an article in a warehouse: what one ledger entry adds to stock on hand.</summary>
public readonly record struct StockMovement(Guid ArticleId, Guid WarehouseId, decimal Quantity);

/// <summary>The destination of a transfer (spec 006, R1-R3).</summary>
public static class TransferRules
{
    /// <summary>A transfer has a destination other than its source; a receipt, an issue or a count has none.</summary>
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

    /// <summary>
    /// What posting one count line moves (spec 008, R11, R12): the difference between the counted base quantity
    /// and the book quantity, into the warehouse when more was found and out of it when less - and nothing
    /// when they agree. Applied to stock that equals the book quantity, the result is the counted quantity.
    /// </summary>
    public static IReadOnlyList<StockMovement> OfCountLine(Guid warehouseId, StockLineValues counted, decimal bookQuantity) =>
        CountRules.Difference(counted.Quantity, bookQuantity) is var difference && difference != 0
            ? [new(counted.ArticleId, warehouseId, difference)]
            : [];

    /// <summary>What reversing a movement moves: the same article and warehouse, the opposite quantity (R14).</summary>
    public static StockMovement Opposite(StockMovement movement) => movement with { Quantity = -movement.Quantity };

    /// <summary>
    /// The rule <c>stock.negativeStockAllowed</c> for one (article, warehouse) pair (ADR-0012, decision 4;
    /// spec 012, R17, R18): whether an operation that changes the pair's stock by <paramref name="delta"/> is
    /// refused. It judges the movement, not the state (ADR-0020, decision 7): a pair that is already below
    /// zero may be raised, and may not be lowered.
    /// </summary>
    /// <param name="onHand">The pair's stock before the operation.</param>
    /// <param name="delta">The sum of the entries the operation writes for the pair.</param>
    /// <param name="negativeStockAllowed">The tenant's value of the rule.</param>
    public static bool GoesBelowZero(decimal onHand, decimal delta, bool negativeStockAllowed) =>
        !negativeStockAllowed && delta < 0 && onHand + delta < 0;

    /// <summary>
    /// The rule <c>sales.reservedStockProtected</c> for one (article, warehouse) pair (ADR-0017; spec 012,
    /// R26, R27): whether an operation that changes the pair's stock by <paramref name="delta"/> is refused
    /// because it takes goods that confirmed sales orders reserve - afterwards something is still reserved,
    /// stock no longer covers it, and this operation made that worse.
    /// </summary>
    /// <param name="onHand">The pair's stock before the operation.</param>
    /// <param name="delta">The sum of the entries the operation writes for the pair.</param>
    /// <param name="reservedBefore">The pair's reserved quantity before the operation.</param>
    /// <param name="reservedAfter">The reserved quantity it would have afterwards: a delivery lowers what its own order still awaits.</param>
    /// <param name="reservedStockProtected">The tenant's value of the rule.</param>
    public static bool TakesReserved(decimal onHand, decimal delta, decimal reservedBefore, decimal reservedAfter, bool reservedStockProtected)
    {
        if (!reservedStockProtected || delta >= 0 || reservedAfter <= 0)
            return false;
        var availableBefore = StockAvailability.Available(onHand, reservedBefore);
        var availableAfter = StockAvailability.Available(onHand + delta, reservedAfter);
        return availableAfter < 0 && availableAfter < availableBefore;
    }

    /// <summary>
    /// The pairs <see cref="GoesBelowZero"/> refuses if all <paramref name="movements"/> were applied
    /// together, in the order they first appear. A pair missing from <paramref name="onHand"/> has zero
    /// stock. Only the net effect per pair counts, never the order. This is the one place the comparison is
    /// made: for an issue, the source of a transfer, a delivery and every reversal.
    /// </summary>
    public static IReadOnlyList<(Guid ArticleId, Guid WarehouseId)> ShortPairs(
        IEnumerable<StockMovement> movements, IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand,
        bool negativeStockAllowed) =>
        movements
            .GroupBy(m => (m.ArticleId, m.WarehouseId))
            .Where(g => GoesBelowZero(onHand.GetValueOrDefault(g.Key), g.Sum(m => m.Quantity), negativeStockAllowed))
            .Select(g => g.Key)
            .ToList();

    /// <summary>
    /// The pairs <see cref="TakesReserved"/> refuses if all <paramref name="movements"/> were applied
    /// together, in the order they first appear. A pair missing from a dictionary has zero stock, or nothing
    /// reserved.
    /// </summary>
    public static IReadOnlyList<(Guid ArticleId, Guid WarehouseId)> ReservedPairs(
        IEnumerable<StockMovement> movements, IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> reservedBefore,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> reservedAfter,
        bool reservedStockProtected) =>
        movements
            .GroupBy(m => (m.ArticleId, m.WarehouseId))
            .Where(g => TakesReserved(
                onHand.GetValueOrDefault(g.Key), g.Sum(m => m.Quantity),
                reservedBefore.GetValueOrDefault(g.Key), reservedAfter.GetValueOrDefault(g.Key), reservedStockProtected))
            .Select(g => g.Key)
            .ToList();
}

/// <summary>
/// The rules of a stock count (ADR-0015; spec 008): each article once, the difference to the book quantity,
/// and when a count is still current.
/// </summary>
public static class CountRules
{
    /// <summary>
    /// A book quantity is stock on hand: a quantity in base units. It is below zero where the tenant allows
    /// negative stock (spec 012, R20).
    /// </summary>
    public static bool IsValidBookQuantity(decimal bookQuantity) =>
        decimal.Round(bookQuantity, QuantityRules.DecimalPlaces) == bookQuantity;

    /// <summary>
    /// R3: the zero-based positions of every line whose article is on more than one line, ascending; empty
    /// when each article appears once. The unit plays no part: one article in two units is a repeat.
    /// A null is a line whose article is not known and repeats nothing.
    /// </summary>
    public static IReadOnlyList<int> RepeatedLines(IReadOnlyList<Guid?> articleIds)
    {
        var repeated = articleIds.Where(a => a is not null).GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return Enumerable.Range(0, articleIds.Count).Where(i => repeated.Contains(articleIds[i])).ToList();
    }

    /// <inheritdoc cref="RepeatedLines(IReadOnlyList{Guid?})"/>
    public static IReadOnlyList<int> RepeatedLines(IReadOnlyList<Guid> articleIds) =>
        RepeatedLines(articleIds.Select(a => (Guid?)a).ToList());

    /// <summary>R7: counted minus book, in base units - positive when more was found than the books say.</summary>
    public static decimal Difference(decimal baseQuantity, decimal bookQuantity) => baseQuantity - bookQuantity;

    /// <summary>
    /// R10: the zero-based positions of the lines whose book quantity is no longer the stock on hand of their
    /// article in the count's warehouse, ascending; empty when the count is current. An article missing from
    /// <paramref name="onHand"/> has zero stock. Only the quantity is compared, not how stock got there.
    /// </summary>
    /// <param name="lines">Per line the article and its book quantity.</param>
    public static IReadOnlyList<int> OutdatedLines(IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, decimal> onHand) =>
        Enumerable.Range(0, lines.Count).Where(i => onHand.GetValueOrDefault(lines[i].ArticleId) != lines[i].Quantity).ToList();
}
