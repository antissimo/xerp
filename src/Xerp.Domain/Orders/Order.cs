using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Domain.Orders;

public enum OrderStatus
{
    /// <summary>Freely editable and deletable; orders nothing and cannot be fulfilled.</summary>
    Draft,

    /// <summary>Numbered and immutable; can be fulfilled, and closed.</summary>
    Confirmed,

    /// <summary>Nothing more will be fulfilled; can be reopened.</summary>
    Closed,
}

/// <summary>The contract names of <see cref="OrderStatus"/>, as stored and as sent to clients.</summary>
public static class OrderStatusNames
{
    public const string Draft = "draft";
    public const string Confirmed = "confirmed";
    public const string Closed = "closed";

    public static string ToName(this OrderStatus status) => status switch
    {
        OrderStatus.Draft => Draft,
        OrderStatus.Confirmed => Confirmed,
        OrderStatus.Closed => Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static OrderStatus Parse(string name) =>
        TryParse(name, out var status) ? status : throw new ArgumentException($"Unknown order status '{name}'.", nameof(name));

    public static bool TryParse(string? name, out OrderStatus status)
    {
        switch (name)
        {
            case Draft:
                status = OrderStatus.Draft;
                return true;
            case Confirmed:
                status = OrderStatus.Confirmed;
                return true;
            case Closed:
                status = OrderStatus.Closed;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

/// <summary>An order line as it was entered: an article, a unit of that article, a quantity in that unit and the price of one such unit.</summary>
public readonly record struct OrderLineEntry(Guid ArticleId, Guid UnitId, decimal Quantity, decimal UnitPrice);

/// <summary>
/// An order (ADR-0016): a commitment towards a partner for goods in one warehouse, with 1-200 priced lines.
/// It is prepared as a draft, then confirmed - which gives it its number, freezes factor and base quantity of
/// every line and makes header and lines immutable - and can then be closed and reopened. An order never
/// writes a ledger entry; it is fulfilled by stock documents linked to it, and only the fulfilled quantity of
/// its lines follows them. What is common to every kind of order is here; a kind adds its name.
/// </summary>
public abstract class Order<TLine> : ITenantOwned, IFulfilledOrder where TLine : OrderLine
{
    public const int ReferenceMaxLength = StockDocument.ReferenceMaxLength;
    public const int NoteMaxLength = StockDocument.NoteMaxLength;
    public const int NumberMaxLength = StockDocument.NumberMaxLength;
    public const int MaxLines = StockDocument.MaxLines;

    private readonly List<TLine> _lines = [];

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>Null until confirmed (R11, R13).</summary>
    public string? Number { get; private set; }

    public DateOnly OrderDate { get; private set; }

    /// <summary>
    /// When fulfilment is due - the expected date of a purchase order, the date the customer requested on a
    /// sales order; never earlier than <see cref="OrderDate"/>.
    /// </summary>
    public DateOnly? DueDate { get; private set; }

    /// <summary>The other party: the supplier of a purchase order, the customer of a sales order.</summary>
    public Guid PartnerId { get; private set; }

    /// <summary>The warehouse the order is fulfilled in (ADR-0016, decision 8).</summary>
    public Guid WarehouseId { get; private set; }

    public string? Reference { get; private set; }
    public string? Note { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public Guid? ConfirmedBy { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    /// <summary>The lines in line order.</summary>
    public IReadOnlyList<TLine> Lines
    {
        get
        {
            // Loaded from the database they come in no particular order.
            _lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
            return _lines;
        }
    }

    public bool IsDraft => Status == OrderStatus.Draft;

    /// <summary>R29.</summary>
    public FulfilmentStatus FulfilmentStatus => OrderProgress.Status(_lines.Select(l => (l.BaseQuantity, l.FulfilledBaseQuantity)));

    /// <summary>R27: per line number, what can still be fulfilled - zero for every line unless the order is confirmed.</summary>
    public IReadOnlyDictionary<int, decimal> Outstanding =>
        _lines.ToDictionary(l => l.LineNo, l => OrderProgress.Outstanding(Status, l.BaseQuantity, l.FulfilledBaseQuantity));

    protected abstract TLine NewLine(int lineNo, OrderLineEntry entry);

    /// <summary>Makes this new instance a draft (R11).</summary>
    protected void Start(
        DateOnly orderDate, DateOnly? dueDate, Guid partnerId, Guid warehouseId, string? reference, string? note,
        IReadOnlyList<OrderLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        Id = Guid.CreateVersion7();
        Status = OrderStatus.Draft;
        CreatedAt = now;
        CreatedBy = actorKeyId;
        Replace(orderDate, dueDate, partnerId, warehouseId, reference, note, lines, now, actorKeyId);
    }

    /// <summary>
    /// Replaces header and lines of a draft; lines are renumbered 1…n in the order given (R5).
    /// Returns the lines that are no longer part of the order, for the caller to delete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The order is not a draft (R14).</exception>
    public IReadOnlyList<TLine> Replace(
        DateOnly orderDate, DateOnly? dueDate, Guid partnerId, Guid warehouseId, string? reference, string? note,
        IReadOnlyList<OrderLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
        // Validate everything before assigning anything, so a rejected replace leaves the draft untouched.
        if (dueDate is { } due && due < orderDate)
            throw new ArgumentException("The due date of an order is not earlier than its order date.", nameof(dueDate));
        var newReference = OptionalTextRules.Normalize(reference, ReferenceMaxLength, nameof(reference));
        if (!NoteRules.TryNormalize(note, NoteMaxLength, out var newNote))
            throw new ArgumentException("Invalid note.", nameof(note));
        if (lines.Count is < 1 or > MaxLines)
            throw new ArgumentException($"An order has 1 to {MaxLines} lines.", nameof(lines));
        if (lines.Any(l => !QuantityRules.IsValid(l.Quantity)))
            throw new ArgumentException("A line quantity breaks the quantity rule.", nameof(lines));
        if (lines.Any(l => !PriceRules.IsValid(l.UnitPrice) || !OrderAmounts.IsValidLine(l.Quantity, l.UnitPrice)))
            throw new ArgumentException("A line's unit price or amount breaks the price rule.", nameof(lines));

        OrderDate = orderDate;
        DueDate = dueDate;
        PartnerId = partnerId;
        WarehouseId = warehouseId;
        Reference = newReference;
        Note = newNote;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;

        var current = Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < current.Count)
                current[i].Set(lines[i]);
            else
                _lines.Add(NewLine(i + 1, lines[i]));
        }
        var removed = _lines.Skip(lines.Count).ToList();
        _lines.RemoveRange(lines.Count, removed.Count);
        return removed;
    }

    /// <summary>
    /// Confirms the draft (R12): number and attribution are set and every line is converted with the factor
    /// given for it and keeps that factor and the resulting base quantity for good. <c>UpdatedAt</c> does not
    /// move (R17). Whether the masters allow it, which number is next and what the factors are now is decided
    /// by the caller, inside the same transaction that saves the result.
    /// </summary>
    /// <param name="factors">For every line, in line order, the factor of its unit to the article's base unit at this moment.</param>
    /// <exception cref="InvalidOperationException">The order is not a draft, or a line does not convert.</exception>
    public void Confirm(string number, IReadOnlyList<decimal> factors, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("A confirmed order needs a number.", nameof(number));
        var lines = Lines;
        if (factors.Count != lines.Count)
            throw new ArgumentException("Confirmation needs one factor per line.", nameof(factors));
        var baseQuantities = new decimal[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!UnitConversion.IsValidFactor(factors[i]))
                throw new ArgumentException($"The factor of line {lines[i].LineNo} breaks the factor rule.", nameof(factors));
            if (!UnitConversion.TryToBase(lines[i].Quantity, factors[i], out baseQuantities[i]))
                throw new InvalidOperationException($"Line {lines[i].LineNo} does not convert to a quantity in the base unit.");
        }

        Status = OrderStatus.Confirmed;
        Number = number;
        ConfirmedAt = now;
        ConfirmedBy = actorKeyId;
        for (var i = 0; i < lines.Count; i++)
            lines[i].Freeze(factors[i], baseQuantities[i]);
    }

    /// <summary>R15: <c>confirmed -> closed</c>, whatever was fulfilled.</summary>
    /// <exception cref="InvalidOperationException">The order is not confirmed.</exception>
    public void Close(DateTime now, Guid actorKeyId)
    {
        if (Status != OrderStatus.Confirmed)
            throw new InvalidOperationException("Only a confirmed order can be closed.");
        Status = OrderStatus.Closed;
        ClosedAt = now;
        ClosedBy = actorKeyId;
    }

    /// <summary>R16: <c>closed -> confirmed</c>; number, lines and fulfilled quantities are untouched.</summary>
    /// <exception cref="InvalidOperationException">The order is not closed.</exception>
    public void Reopen()
    {
        if (Status != OrderStatus.Closed)
            throw new InvalidOperationException("Only a closed order can be reopened.");
        Status = OrderStatus.Confirmed;
        ClosedAt = null;
        ClosedBy = null;
    }

    /// <summary>
    /// Counts a posted fulfilment document (R25, R26): the fulfilled quantity of every order line it names
    /// rises by the document's base quantities for that line. All of it or nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The order is not confirmed, a line is not a line of the order, or a line would go above its ordered quantity.</exception>
    public void Fulfil(IReadOnlyList<LineFulfilment> documentLines)
    {
        if (Status != OrderStatus.Confirmed)
            throw new InvalidOperationException("Only a confirmed order can be fulfilled.");
        if (documentLines.Any(l => l.BaseQuantity <= 0))
            throw new ArgumentException("A fulfilment line has a base quantity greater than zero.", nameof(documentLines));
        if (OrderProgress.ExceedingLines(documentLines, Outstanding).Count > 0)
            throw new InvalidOperationException("The document would take an order line above its ordered quantity, or names a line the order does not have.");
        foreach (var group in documentLines.GroupBy(l => l.OrderLineNo))
            Line(group.Key).Move(group.Sum(l => l.BaseQuantity));
    }

    /// <summary>
    /// Stops counting a fulfilment document that was reversed (R33): the fulfilled quantity of its order lines
    /// drops by its base quantities. Allowed whatever the order's status - also when closed (R34) - and it does
    /// not change that status.
    /// </summary>
    /// <exception cref="InvalidOperationException">The order is a draft, or the document was never counted in full.</exception>
    public void TakeBack(IReadOnlyList<LineFulfilment> documentLines)
    {
        if (IsDraft)
            throw new InvalidOperationException("A draft order has nothing fulfilled.");
        var perLine = documentLines.GroupBy(l => l.OrderLineNo).ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQuantity));
        if (perLine.Any(p => _lines.SingleOrDefault(l => l.LineNo == p.Key) is not { } line || line.FulfilledBaseQuantity < p.Value))
            throw new InvalidOperationException("The document takes back more than was fulfilled, or names a line the order does not have.");
        foreach (var (lineNo, quantity) in perLine)
            Line(lineNo).Move(-quantity);
    }

    private TLine Line(int lineNo) => _lines.Single(l => l.LineNo == lineNo);

    private void EnsureDraft()
    {
        if (!IsDraft)
            throw new InvalidOperationException("Only a draft order can be changed or confirmed.");
    }
}

/// <summary>
/// One line of an order. What was entered - article, unit, quantity, unit price - is frozen at confirmation
/// together with the factor and the base quantity it was confirmed with. The one column that moves afterwards
/// is progress: <see cref="FulfilledBaseQuantity"/>, written only by the posting and the reversal of a linked
/// stock document.
/// </summary>
public abstract class OrderLine : ITenantOwned
{
    protected OrderLine() { }

    protected OrderLine(Guid orderId, int lineNo, OrderLineEntry entry)
    {
        Id = Guid.CreateVersion7();
        OrderId = orderId;
        LineNo = lineNo;
        Set(entry);
    }

    public Guid Id { get; private set; }

    /// <summary>The tenant of the order.</summary>
    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>1…n, the position on the order; what a fulfilment line names.</summary>
    public int LineNo { get; private set; }

    public Guid ArticleId { get; private set; }

    /// <summary>The unit the quantity and the price are in: the article's base unit or one of its alternative units.</summary>
    public Guid UnitId { get; private set; }

    /// <summary>As entered, in <see cref="UnitId"/>.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>The price of one <see cref="UnitId"/>, in the tenant's currency, without tax.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>The factor the line was confirmed with; null while the order is a draft. Frozen.</summary>
    public decimal? Factor { get; private set; }

    /// <summary>The ordered quantity in the article's base unit, as confirmed; null while the order is a draft. Frozen.</summary>
    public decimal? BaseQuantity { get; private set; }

    /// <summary>
    /// Progress (R25): the sum of the base quantities of the stock document lines that name this line on
    /// posted, not reversed, documents - received, for a purchase order; delivered, for a sales order. Always between zero and
    /// <see cref="BaseQuantity"/> (R27).
    /// </summary>
    public decimal FulfilledBaseQuantity { get; private set; }

    /// <summary>R9.</summary>
    public decimal LineAmount => OrderAmounts.LineAmount(Quantity, UnitPrice);

    /// <summary>What was entered.</summary>
    public OrderLineEntry Entry => new(ArticleId, UnitId, Quantity, UnitPrice);

    internal void Set(OrderLineEntry entry)
    {
        ArticleId = entry.ArticleId;
        UnitId = entry.UnitId;
        Quantity = entry.Quantity;
        UnitPrice = entry.UnitPrice;
    }

    internal void Freeze(decimal factor, decimal baseQuantity)
    {
        if (Factor is not null || BaseQuantity is not null)
            throw new InvalidOperationException("A confirmed line keeps its factor and base quantity.");
        Factor = factor;
        BaseQuantity = baseQuantity;
    }

    internal void Move(decimal delta)
    {
        var fulfilled = FulfilledBaseQuantity + delta;
        if (BaseQuantity is not { } ordered || fulfilled < 0 || fulfilled > ordered)
            throw new InvalidOperationException("The fulfilled quantity of an order line stays between zero and its ordered quantity.");
        FulfilledBaseQuantity = fulfilled;
    }
}

/// <summary>
/// A purchase order (spec 009): goods ordered from a supplier (<see cref="Order{TLine}.PartnerId"/>) into a
/// warehouse, fulfilled by receipts linked to it; its due date is the expected date.
/// </summary>
public sealed class PurchaseOrder : Order<PurchaseOrderLine>
{
    private PurchaseOrder() { }

    public static PurchaseOrder Create(
        DateOnly orderDate, DateOnly? expectedDate, Guid supplierId, Guid warehouseId, string? reference, string? note,
        IReadOnlyList<OrderLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        var order = new PurchaseOrder();
        order.Start(orderDate, expectedDate, supplierId, warehouseId, reference, note, lines, now, actorKeyId);
        return order;
    }

    protected override PurchaseOrderLine NewLine(int lineNo, OrderLineEntry entry) => new(Id, lineNo, entry);
}

/// <summary>A line of a purchase order; its fulfilled quantity is what was received.</summary>
public sealed class PurchaseOrderLine : OrderLine
{
    private PurchaseOrderLine() { }

    internal PurchaseOrderLine(Guid orderId, int lineNo, OrderLineEntry entry) : base(orderId, lineNo, entry) { }
}

/// <summary>
/// A sales order (spec 010; ADR-0017): goods a customer (<see cref="Order{TLine}.PartnerId"/>) ordered, shipped
/// from a warehouse and fulfilled by issues linked to it; its due date is the date the customer requested.
/// Everything it does is what <see cref="Order{TLine}"/> does.
/// </summary>
public sealed class SalesOrder : Order<SalesOrderLine>
{
    private SalesOrder() { }

    public static SalesOrder Create(
        DateOnly orderDate, DateOnly? requestedDate, Guid customerId, Guid warehouseId, string? reference, string? note,
        IReadOnlyList<OrderLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        var order = new SalesOrder();
        order.Start(orderDate, requestedDate, customerId, warehouseId, reference, note, lines, now, actorKeyId);
        return order;
    }

    protected override SalesOrderLine NewLine(int lineNo, OrderLineEntry entry) => new(Id, lineNo, entry);
}

/// <summary>A line of a sales order; its fulfilled quantity is what was delivered.</summary>
public sealed class SalesOrderLine : OrderLine
{
    private SalesOrderLine() { }

    internal SalesOrderLine(Guid orderId, int lineNo, OrderLineEntry entry) : base(orderId, lineNo, entry) { }
}
