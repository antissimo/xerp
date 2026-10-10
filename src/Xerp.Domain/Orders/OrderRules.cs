using Xerp.Domain.Inventory;

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

    /// <summary>Every line is fulfilled to its ordered quantity - or above it, where the tenant allows that (spec 012, R23).</summary>
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
    /// yet) or closed (nothing more will come). Never negative: a line fulfilled above its ordered quantity
    /// has nothing outstanding (spec 012, R23).
    /// </summary>
    public static decimal Outstanding(OrderStatus status, decimal? baseQuantity, decimal fulfilled) =>
        status == OrderStatus.Confirmed && baseQuantity is { } ordered ? Math.Max(0m, ordered - fulfilled) : 0m;

    /// <summary>R29, from the ordered and the fulfilled base quantity of every line. A draft has neither and is <see cref="FulfilmentStatus.None"/>.</summary>
    public static FulfilmentStatus Status(IEnumerable<(decimal? BaseQuantity, decimal Fulfilled)> lines)
    {
        var all = lines.ToList();
        if (all.All(l => l.Fulfilled <= 0))
            return FulfilmentStatus.None;
        return all.All(l => l.Fulfilled >= l.BaseQuantity) ? FulfilmentStatus.Full : FulfilmentStatus.Partial;
    }

    /// <summary>
    /// The rules <c>purchase.overReceiptAllowed</c> and <c>sales.overDeliveryAllowed</c> for one order line
    /// (spec 009, R26; spec 012, R22): whether a document that adds <paramref name="document"/> to a line of
    /// which <paramref name="fulfilled"/> of <paramref name="ordered"/> is fulfilled already is refused. When
    /// more than ordered is allowed, nothing is refused and there is no upper limit.
    /// </summary>
    /// <param name="overFulfilmentAllowed">The tenant's value of the rule of the order's kind.</param>
    public static bool Exceeds(decimal ordered, decimal fulfilled, decimal document, bool overFulfilmentAllowed) =>
        !overFulfilmentAllowed && fulfilled + document > ordered;

    /// <summary>
    /// R26: the zero-based positions of every document line that names an order line which
    /// <see cref="Exceeds"/> refuses for the document, summed over all its lines naming it - ascending; empty
    /// when the document is within the order, and always when more than ordered is allowed. The order is given
    /// by what its lines have outstanding, which is the ordered quantity still open (never below zero): an
    /// order line missing from <paramref name="outstanding"/> has nothing outstanding.
    /// </summary>
    public static IReadOnlyList<int> ExceedingLines(
        IReadOnlyList<LineFulfilment> documentLines, IReadOnlyDictionary<int, decimal> outstanding, bool overFulfilmentAllowed)
    {
        var exceeded = documentLines
            .GroupBy(l => l.OrderLineNo)
            .Where(g => Exceeds(outstanding.GetValueOrDefault(g.Key), 0m, g.Sum(l => l.BaseQuantity), overFulfilmentAllowed))
            .Select(g => g.Key)
            .ToHashSet();
        return Enumerable.Range(0, documentLines.Count).Where(i => exceeded.Contains(documentLines[i].OrderLineNo)).ToList();
    }

    /// <summary>
    /// What a fulfilment document takes off the outstanding quantity of each order line it names (spec 012,
    /// R27, R28): its quantity for the line, but no more than is outstanding - the part above it, possible
    /// where more than ordered is allowed, releases nothing.
    /// </summary>
    public static IReadOnlyDictionary<int, decimal> Released(IReadOnlyList<LineFulfilment> documentLines, IReadOnlyDictionary<int, decimal> outstanding) =>
        documentLines
            .GroupBy(l => l.OrderLineNo)
            .ToDictionary(g => g.Key, g => Math.Min(g.Sum(l => l.BaseQuantity), outstanding.GetValueOrDefault(g.Key)));
}

/// <summary>The rules <c>purchase.partnerRequired</c> and <c>sales.partnerRequired</c> (spec 012, R33, R34).</summary>
public static class OrderPartnerRules
{
    /// <summary>
    /// Whether an order may be saved with this partner: with one always - whether that partner may be used is
    /// another check - and without one only when the tenant does not require a partner on this kind of order.
    /// </summary>
    /// <param name="partnerRequired">The tenant's value of the rule of the order's kind.</param>
    public static bool MayBeSavedWith(Guid? partnerId, bool partnerRequired) => partnerId is not null || !partnerRequired;
}

/// <summary>The two sides an order can be on: goods coming in from a supplier, goods going out to a customer.</summary>
public enum OrderSide
{
    Purchase,
    Sales,
}

public static class OrderSides
{
    /// <summary>
    /// The one type of stock document that fulfils an order of this side (spec 009, R18; spec 010, R5): a
    /// purchase order is received with a receipt, a sales order delivered with an issue.
    /// </summary>
    public static StockDocumentType FulfilledBy(this OrderSide side) => side switch
    {
        OrderSide.Purchase => StockDocumentType.Receipt,
        OrderSide.Sales => StockDocumentType.Issue,
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };
}

/// <summary>The order a stock document fulfils. A document has at most one (spec 010, R6).</summary>
public readonly record struct OrderLink(OrderSide Side, Guid OrderId);

/// <summary>An order of any kind as the posting and the reversal of a linked stock document see it.</summary>
public interface IFulfilledOrder
{
    OrderStatus Status { get; }

    /// <summary>Per line number, what can still be fulfilled.</summary>
    IReadOnlyDictionary<int, decimal> Outstanding { get; }

    /// <param name="overFulfilmentAllowed">The tenant's value of the rule that lets a line go above its ordered quantity.</param>
    void Fulfil(IReadOnlyList<LineFulfilment> documentLines, bool overFulfilmentAllowed);

    void TakeBack(IReadOnlyList<LineFulfilment> documentLines);
}

/// <summary>
/// What stock on hand says about promises (ADR-0017; spec 010, R15, R16). Both figures are derived: nothing
/// stores them. Whether they block a movement is the rule <c>sales.reservedStockProtected</c> (spec 012, R27).
/// </summary>
public static class StockAvailability
{
    /// <summary>
    /// R15: the reserved quantity of an (article, warehouse) pair, from the sales order lines with that article
    /// shipping from that warehouse - the sum of what is outstanding on them, which is zero for the lines of
    /// drafts and of closed orders.
    /// </summary>
    public static decimal Reserved(IEnumerable<(OrderStatus Status, decimal? BaseQuantity, decimal Delivered)> lines) =>
        lines.Sum(l => OrderProgress.Outstanding(l.Status, l.BaseQuantity, l.Delivered));

    /// <summary>R16: on hand minus reserved. It may be negative - more is promised than is there. What is incoming is not part of it.</summary>
    public static decimal Available(decimal onHand, decimal reserved) => onHand - reserved;
}
