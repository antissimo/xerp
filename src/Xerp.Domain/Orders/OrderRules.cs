namespace Xerp.Domain.Orders;

/// <summary>
/// Rule for a unit price on an order line (spec 009, R8): an exact decimal of zero or more - goods may be free
/// of charge - with at most six decimal places, at most 999,999,999.999999. Rejected outside the rule, never rounded.
/// </summary>
public static class PriceRules
{
    public const int DecimalPlaces = 6;
    public const decimal Max = 999_999_999.999999m;

    public static bool IsValid(decimal unitPrice) =>
        unitPrice >= 0 && unitPrice <= Max && decimal.Round(unitPrice, DecimalPlaces) == unitPrice;
}

/// <summary>
/// The amounts of an order (ADR-0016, decision 11; spec 009, R9). They depend on nothing but a line's own
/// quantity and unit price: never on a factor, never on what was fulfilled.
/// </summary>
public static class OrderAmounts
{
    public const int DecimalPlaces = 2;
    public const decimal MaxLineAmount = 9_999_999_999.99m;

    /// <summary>Quantity × unit price, rounded to two decimal places, half away from zero.</summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice) =>
        decimal.Round(quantity * unitPrice, DecimalPlaces, MidpointRounding.AwayFromZero);

    /// <summary>The maximum is compared with the rounded amount: <c>999999999.9995 × 10</c> rounds to 10000000000.00 and is refused.</summary>
    public static bool IsValidLine(decimal quantity, decimal unitPrice) => LineAmount(quantity, unitPrice) <= MaxLineAmount;

    /// <summary>The sum of the rounded line amounts; it has no limit of its own.</summary>
    public static decimal Total(IEnumerable<decimal> lineAmounts) => lineAmounts.Sum();
}

/// <summary>How much of an order has been fulfilled (spec 009, R29): received, for a purchase order.</summary>
public enum FulfilmentStatus
{
    /// <summary>No line has a fulfilled quantity above zero.</summary>
    None,

    /// <summary>Something was fulfilled, but not every line in full.</summary>
    Partial,

    /// <summary>Every line is fulfilled to exactly its ordered quantity.</summary>
    Full,
}

public static class FulfilmentStatusNames
{
    public const string None = "none";
    public const string Partial = "partial";
    public const string Full = "full";

    public static string ToName(this FulfilmentStatus status) => status switch
    {
        FulfilmentStatus.None => None,
        FulfilmentStatus.Partial => Partial,
        FulfilmentStatus.Full => Full,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool TryParse(string? name, out FulfilmentStatus status)
    {
        switch (name)
        {
            case None:
                status = FulfilmentStatus.None;
                return true;
            case Partial:
                status = FulfilmentStatus.Partial;
                return true;
            case Full:
                status = FulfilmentStatus.Full;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

/// <summary>What one line of a fulfilment document adds to an order line, in base units.</summary>
public readonly record struct LineFulfilment(int OrderLineNo, decimal BaseQuantity);

/// <summary>
/// Progress of an order line, in base units whatever the units of the order line and of the fulfilment lines
/// (ADR-0016, decision 5; spec 009, R25-R29).
/// </summary>
public static class OrderProgress
{
    /// <summary>
    /// R27: ordered minus fulfilled while the order is confirmed; zero while it is a draft (nothing is ordered
    /// yet) or closed (nothing more will come).
    /// </summary>
    public static decimal Outstanding(OrderStatus status, decimal? baseQuantity, decimal fulfilled) =>
        status == OrderStatus.Confirmed && baseQuantity is { } ordered ? ordered - fulfilled : 0m;

    /// <summary>R29, from the ordered and the fulfilled base quantity of every line. A draft has neither and is <see cref="FulfilmentStatus.None"/>.</summary>
    public static FulfilmentStatus Status(IEnumerable<(decimal? BaseQuantity, decimal Fulfilled)> lines)
    {
        var all = lines.ToList();
        if (all.All(l => l.Fulfilled <= 0))
            return FulfilmentStatus.None;
        return all.All(l => l.BaseQuantity == l.Fulfilled) ? FulfilmentStatus.Full : FulfilmentStatus.Partial;
    }

    /// <summary>
    /// R26, never more than ordered: the zero-based positions of every document line that names an order line
    /// which the document, summed over all its lines naming it, would take above its outstanding quantity -
    /// ascending; empty when the document is within the order. An order line missing from
    /// <paramref name="outstanding"/> has nothing outstanding.
    /// </summary>
    public static IReadOnlyList<int> ExceedingLines(IReadOnlyList<LineFulfilment> documentLines, IReadOnlyDictionary<int, decimal> outstanding)
    {
        var exceeded = documentLines
            .GroupBy(l => l.OrderLineNo)
            .Where(g => g.Sum(l => l.BaseQuantity) > outstanding.GetValueOrDefault(g.Key))
            .Select(g => g.Key)
            .ToHashSet();
        return Enumerable.Range(0, documentLines.Count).Where(i => exceeded.Contains(documentLines[i].OrderLineNo)).ToList();
    }
}
