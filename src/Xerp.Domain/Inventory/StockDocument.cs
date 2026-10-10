using Xerp.Domain.Common;
using Xerp.Domain.Orders;

namespace Xerp.Domain.Inventory;

public enum StockDocumentType
{
    /// <summary>Goods come into the warehouse: one positive ledger entry per line.</summary>
    Receipt,

    /// <summary>Goods leave the warehouse: one negative ledger entry per line.</summary>
    Issue,

    /// <summary>
    /// Goods move from the warehouse to the destination warehouse in one posting (ADR-0013): per line one
    /// negative entry in the source and one positive entry in the destination.
    /// </summary>
    Transfer,

    /// <summary>
    /// The counted quantities of articles in the warehouse (ADR-0015): posting writes, per line, the difference
    /// between what was counted and the book quantity the draft was saved against - nothing when they agree.
    /// </summary>
    Count,
}

/// <summary>The contract names of <see cref="StockDocumentType"/>, as stored and as sent to clients.</summary>
public static class StockDocumentTypeNames
{
    public const string Receipt = "receipt";
    public const string Issue = "issue";
    public const string Transfer = "transfer";
    public const string Count = "count";

    public static string ToName(this StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => Receipt,
        StockDocumentType.Issue => Issue,
        StockDocumentType.Transfer => Transfer,
        StockDocumentType.Count => Count,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static StockDocumentType Parse(string name) =>
        TryParse(name, out var type) ? type : throw new ArgumentException($"Unknown stock document type '{name}'.", nameof(name));

    /// <summary>Exact, case-sensitive match (spec 005, R1).</summary>
    public static bool TryParse(string? name, out StockDocumentType type)
    {
        switch (name)
        {
            case Receipt:
                type = StockDocumentType.Receipt;
                return true;
            case Issue:
                type = StockDocumentType.Issue;
                return true;
            case Transfer:
                type = StockDocumentType.Transfer;
                return true;
            case Count:
                type = StockDocumentType.Count;
                return true;
            default:
                type = default;
                return false;
        }
    }
}

public enum StockDocumentStatus
{
    /// <summary>Freely editable and deletable; no effect on stock.</summary>
    Draft,

    /// <summary>Numbered, in the ledger, immutable.</summary>
    Posted,

    /// <summary>
    /// Posted and then cancelled by a reversing document (ADR-0013). Still in the ledger and still immutable;
    /// the reversing document's entries cancel this document's exactly.
    /// </summary>
    Reversed,
}

/// <summary>The contract names of <see cref="StockDocumentStatus"/>.</summary>
public static class StockDocumentStatusNames
{
    public const string Draft = "draft";
    public const string Posted = "posted";
    public const string Reversed = "reversed";

    public static string ToName(this StockDocumentStatus status) => status switch
    {
        StockDocumentStatus.Draft => Draft,
        StockDocumentStatus.Posted => Posted,
        StockDocumentStatus.Reversed => Reversed,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static StockDocumentStatus Parse(string name) =>
        TryParse(name, out var status) ? status : throw new ArgumentException($"Unknown stock document status '{name}'.", nameof(name));

    public static bool TryParse(string? name, out StockDocumentStatus status)
    {
        switch (name)
        {
            case Draft:
                status = StockDocumentStatus.Draft;
                return true;
            case Posted:
                status = StockDocumentStatus.Posted;
                return true;
            case Reversed:
                status = StockDocumentStatus.Reversed;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

/// <summary>
/// A stock document (ADR-0012): a header and 1-200 lines, prepared as a draft and then posted. Posting gives
/// it its number, produces the ledger entries and makes it immutable; this class is the only place that
/// produces <see cref="StockLedgerEntry"/> instances.
/// </summary>
public sealed class StockDocument : ITenantOwned
{
    public const int ReferenceMaxLength = 100;
    public const int NoteMaxLength = 2000;
    public const int NumberMaxLength = 30;
    public const int MaxLines = 200;

    private readonly List<StockDocumentLine> _lines = [];

    private StockDocument() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Chosen at creation; never changes (R1).</summary>
    public StockDocumentType Type { get; private set; }

    public StockDocumentStatus Status { get; private set; }

    /// <summary>Null until posted (R9, R17).</summary>
    public string? Number { get; private set; }

    /// <summary>The business date the user assigns; plays no part in the stock check (ADR-0012, decision 5).</summary>
    public DateOnly DocumentDate { get; private set; }

    /// <summary>The warehouse of a receipt, an issue or a count; the source of a transfer.</summary>
    public Guid WarehouseId { get; private set; }

    /// <summary>The destination of a transfer; null on every other type (spec 006, R2, R3).</summary>
    public Guid? ToWarehouseId { get; private set; }

    /// <summary>On a reversing document: the document it reverses. Null on every other document.</summary>
    public Guid? ReversalOfId { get; private set; }

    /// <summary>On a reversed document: the document that reversed it. Null until then.</summary>
    public Guid? ReversedById { get; private set; }

    /// <summary>
    /// The purchase order a receipt fulfils (ADR-0016; spec 009, R18, R19): set at creation, never changed,
    /// only on a receipt. Null on an unlinked document, which behaves as if orders did not exist.
    /// </summary>
    public Guid? PurchaseOrderId { get; private set; }

    /// <summary>
    /// The sales order an issue delivers (ADR-0017; spec 010, R5, R6): set at creation, never changed, only on
    /// an issue. A document has at most one order link.
    /// </summary>
    public Guid? SalesOrderId { get; private set; }

    /// <summary>
    /// Whom the goods came from or went to (spec 011a): the supplier of a receipt, the customer of an issue.
    /// A header field, optional on an unlinked document; on a document linked to an order it is that order's
    /// partner. Always null on a transfer and a count. Which partner it may be is decided by the caller.
    /// </summary>
    public Guid? PartnerId { get; private set; }

    public string? Reference { get; private set; }
    public string? Note { get; private set; }
    public DateTime? PostedAt { get; private set; }
    public Guid? PostedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    /// <summary>The lines in line order.</summary>
    public IReadOnlyList<StockDocumentLine> Lines
    {
        get
        {
            // Loaded from the database they come in no particular order.
            _lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
            return _lines;
        }
    }

    public bool IsDraft => Status == StockDocumentStatus.Draft;

    /// <summary>A reversing document: posted from the start, never a draft, never reversible (spec 006, R11, R21).</summary>
    public bool IsReversal => ReversalOfId is not null;

    /// <summary>Spec 006, R11: only a posted document that is not itself a reversing document.</summary>
    public bool CanBeReversed => Status == StockDocumentStatus.Posted && !IsReversal;

    /// <summary>Whether the document fulfils an order: then every line names an order line (spec 009, R20).</summary>
    public bool IsLinked => Link is not null;

    /// <summary>Spec 011a, R1: only a receipt and an issue can name a partner.</summary>
    public static bool CanNamePartner(StockDocumentType type) => type is StockDocumentType.Receipt or StockDocumentType.Issue;

    /// <summary>The order the document fulfils, of either kind; null on an unlinked document (spec 010, R6).</summary>
    public OrderLink? Link =>
        PurchaseOrderId is { } purchaseOrderId ? new OrderLink(OrderSide.Purchase, purchaseOrderId)
        : SalesOrderId is { } salesOrderId ? new OrderLink(OrderSide.Sales, salesOrderId)
        : null;

    /// <summary>
    /// What the posted lines of a linked document mean for the order (spec 009, R25): per line, the order line
    /// it names and its base quantity. Empty on an unlinked document.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is linked and not posted.</exception>
    public IReadOnlyList<LineFulfilment> Fulfilment =>
        IsLinked ? Lines.Select(l => new LineFulfilment(l.OrderLineNo!.Value, l.BaseValues.Quantity)).ToList() : [];

    public static StockDocument Create(
        StockDocumentType type, DateOnly documentDate, Guid warehouseId, Guid? toWarehouseId, string? reference, string? note,
        IReadOnlyList<StockLineEntry> lines, DateTime now, Guid actorKeyId, IReadOnlyList<decimal>? bookQuantities = null,
        Guid? purchaseOrderId = null, Guid? salesOrderId = null, Guid? partnerId = null)
    {
        if (purchaseOrderId is not null && type != OrderSide.Purchase.FulfilledBy())
            throw new ArgumentException("Only a receipt can be linked to a purchase order.", nameof(purchaseOrderId));
        if (salesOrderId is not null && type != OrderSide.Sales.FulfilledBy())
            throw new ArgumentException("Only an issue can be linked to a sales order.", nameof(salesOrderId));
        var document = new StockDocument
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Status = StockDocumentStatus.Draft,
            PurchaseOrderId = purchaseOrderId,
            SalesOrderId = salesOrderId,
            CreatedAt = now,
            CreatedBy = actorKeyId,
        };
        document.Replace(documentDate, warehouseId, toWarehouseId, reference, note, lines, now, actorKeyId, bookQuantities, partnerId);
        return document;
    }

    /// <summary>
    /// Replaces header and lines of a draft; lines are renumbered 1…n in the order given (R4, R10).
    /// Returns the lines that are no longer part of the document, for the caller to delete.
    /// <para>
    /// A count (spec 008, R3, R4, R6) names each article at most once, may have counted quantities of zero and
    /// records with every save the book quantity of every line anew: <paramref name="bookQuantities"/>, the
    /// stock on hand of each line's article in <paramref name="warehouseId"/> at this moment, in line order.
    /// The other types take none.
    /// </para>
    /// <para>
    /// The link to an order is not replaced (spec 009, R19, R20): on a linked document every line names an
    /// order line, on an unlinked one none does.
    /// </para>
    /// <para>
    /// <paramref name="partnerId"/> replaces the partner like every other header field (spec 011a, R3): null
    /// removes it. Only a receipt and an issue can have one (R1).
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is posted (R11).</exception>
    public IReadOnlyList<StockDocumentLine> Replace(
        DateOnly documentDate, Guid warehouseId, Guid? toWarehouseId, string? reference, string? note,
        IReadOnlyList<StockLineEntry> lines, DateTime now, Guid actorKeyId, IReadOnlyList<decimal>? bookQuantities = null,
        Guid? partnerId = null)
    {
        EnsureDraft();
        if (partnerId is not null && !CanNamePartner(Type))
            throw new ArgumentException("Only a receipt and an issue can name a partner.", nameof(partnerId));
        if (!TransferRules.IsValidDestination(Type, warehouseId, toWarehouseId))
            throw new ArgumentException(
                "A transfer needs a destination warehouse other than its source; other documents have none.", nameof(toWarehouseId));
        // Validate everything before assigning anything, so a rejected replace leaves the draft untouched.
        var newReference = OptionalTextRules.Normalize(reference, ReferenceMaxLength, nameof(reference));
        if (!NoteRules.TryNormalize(note, NoteMaxLength, out var newNote))
            throw new ArgumentException("Invalid note.", nameof(note));
        if (lines.Count is < 1 or > MaxLines)
            throw new ArgumentException($"A stock document has 1 to {MaxLines} lines.", nameof(lines));
        if (lines.Any(l => !QuantityRules.IsValidOn(Type, l.Quantity)))
            throw new ArgumentException("A line quantity breaks the quantity rule.", nameof(lines));
        if (Type == StockDocumentType.Count)
        {
            if (CountRules.RepeatedLines(lines.Select(l => l.ArticleId).ToList()).Count > 0)
                throw new ArgumentException("A count names each article at most once.", nameof(lines));
            if (bookQuantities is null || bookQuantities.Count != lines.Count || bookQuantities.Any(b => !CountRules.IsValidBookQuantity(b)))
                throw new ArgumentException("A count needs the book quantity of every line: stock on hand, never negative.", nameof(bookQuantities));
        }
        else if (bookQuantities is not null)
            throw new ArgumentException("Only a count records book quantities.", nameof(bookQuantities));
        if (lines.Any(l => IsLinked ? l.OrderLineNo is not >= 1 : l.OrderLineNo is not null))
            throw new ArgumentException("Every line of a linked document names an order line; a line of an unlinked document names none.", nameof(lines));

        DocumentDate = documentDate;
        WarehouseId = warehouseId;
        ToWarehouseId = toWarehouseId;
        PartnerId = partnerId;
        Reference = newReference;
        Note = newNote;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;

        var current = Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < current.Count)
                current[i].Set(lines[i], bookQuantities?[i], Link);
            else
                _lines.Add(new StockDocumentLine(Id, i + 1, lines[i], bookQuantities?[i], Link));
        }
        var removed = _lines.Skip(lines.Count).ToList();
        _lines.RemoveRange(lines.Count, removed.Count);
        return removed;
    }

    /// <summary>
    /// Posts the draft (R12, R14; spec 006, R6; spec 007, R17-R19): status, number and posting attribution are
    /// set, every line is converted once more with the factor given for it and keeps that factor and the
    /// resulting base quantity for good, and the ledger entries are returned - one positive entry per line for
    /// a receipt, one negative for an issue, and for a transfer the outgoing entry in the source followed by
    /// the incoming entry in the destination. Every entry is plus or minus its line's base quantity. A count
    /// (spec 008, R11) writes for every line the difference between its base quantity and its book quantity,
    /// and no entry where they agree - so it may post without entries. Whether stock suffices, whether a count
    /// is still current, which number is next and what the factors are now is decided by the caller, inside
    /// the same transaction that saves the result.
    /// </summary>
    /// <param name="factors">
    /// For every line, in line order, the factor of its unit to the article's base unit at this moment; 1 for
    /// a line in the base unit.
    /// </param>
    /// <exception cref="InvalidOperationException">The document is not a draft (R11), or a line does not convert (R15).</exception>
    public IReadOnlyList<StockLedgerEntry> Post(string number, IReadOnlyList<decimal> factors, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("A posted document needs a number.", nameof(number));
        var lines = Lines;
        if (factors.Count != lines.Count)
            throw new ArgumentException("Posting needs one factor per line.", nameof(factors));
        // Convert everything before assigning anything, so a refused posting leaves the draft untouched.
        var baseQuantities = new decimal[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!UnitConversion.IsValidFactor(factors[i]))
                throw new ArgumentException($"The factor of line {lines[i].LineNo} breaks the factor rule.", nameof(factors));
            if (!UnitConversion.TryToBaseOn(Type, lines[i].Quantity, factors[i], out baseQuantities[i]))
                throw new InvalidOperationException($"Line {lines[i].LineNo} does not convert to a quantity in the base unit.");
        }

        Status = StockDocumentStatus.Posted;
        Number = number;
        PostedAt = now;
        PostedBy = actorKeyId;
        for (var i = 0; i < lines.Count; i++)
            lines[i].Freeze(factors[i], baseQuantities[i]);

        // From the stored base quantity, never from quantity × factor a second time.
        return lines
            .SelectMany(l => (Type == StockDocumentType.Count
                    ? StockMovements.OfCountLine(WarehouseId, l.BaseValues, l.BookQuantity!.Value)
                    : StockMovements.OfLine(Type, WarehouseId, ToWarehouseId, l.BaseValues))
                .Select(m => new StockLedgerEntry(m.ArticleId, m.WarehouseId, m.Quantity, Id, l.LineNo, DocumentDate, now, actorKeyId)))
            .ToList();
    }

    /// <summary>
    /// Reverses this posted document (ADR-0013; spec 006, R11-R14): returns the reversing document - same type,
    /// warehouses, partner (spec 011a, R14), lines and reference, already posted under <paramref name="number"/> - and its ledger
    /// entries, which are <paramref name="entries"/> with the opposite sign. The lines are copies of this
    /// document's posted lines - unit, quantity, factor and base quantity as posted - whatever the article's
    /// conversions are now (spec 007, R20), and on a count with the book quantity it was posted against (spec
    /// 008, R17), and on a linked receipt with the same order and order lines (spec 009, R32). A count that wrote no entries is reversed like any other: the reversing document then has
    /// none either. This document becomes
    /// <see cref="StockDocumentStatus.Reversed"/> and points to the reversing document; nothing else on it
    /// changes. Whether stock allows it and which number is next is decided by the caller, inside the same
    /// transaction that saves the result.
    /// </summary>
    /// <param name="entries">All ledger entries of this document, and no others.</param>
    /// <exception cref="InvalidOperationException">The document cannot be reversed (R11).</exception>
    public (StockDocument Reversal, IReadOnlyList<StockLedgerEntry> Entries) Reverse(
        IReadOnlyCollection<StockLedgerEntry> entries, DateOnly documentDate, string? note, string number, DateTime now, Guid actorKeyId)
    {
        if (!CanBeReversed)
            throw new InvalidOperationException("Only a posted stock document that is not itself a reversal can be reversed.");
        if (documentDate < DocumentDate)
            throw new ArgumentException("A reversal cannot be dated before the document it reverses.", nameof(documentDate));
        if (!NoteRules.TryNormalize(note, NoteMaxLength, out var reversalNote))
            throw new ArgumentException("Invalid note.", nameof(note));
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("A posted document needs a number.", nameof(number));
        // Every posted line of the other types wrote at least one entry; a count wrote one per line with a difference.
        var expectedEntries = Type == StockDocumentType.Count ? Lines.Count(l => l.DifferenceQuantity != 0) : (int?)null;
        if (entries.Any(e => e.DocumentId != Id) || (expectedEntries is { } expected ? entries.Count != expected : entries.Count == 0))
            throw new ArgumentException("A reversal needs exactly the ledger entries of the document it reverses.", nameof(entries));

        var reversal = new StockDocument
        {
            Id = Guid.CreateVersion7(),
            Type = Type,
            Status = StockDocumentStatus.Posted,
            Number = number,
            DocumentDate = documentDate,
            WarehouseId = WarehouseId,
            ToWarehouseId = ToWarehouseId,
            ReversalOfId = Id,
            PurchaseOrderId = PurchaseOrderId,
            SalesOrderId = SalesOrderId,
            PartnerId = PartnerId,
            Reference = Reference,
            Note = reversalNote,
            PostedAt = now,
            PostedBy = actorKeyId,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actorKeyId,
            UpdatedBy = actorKeyId,
        };
        reversal._lines.AddRange(Lines.Select(l => StockDocumentLine.CopyOfPosted(reversal.Id, l)));

        // Derived from the entries, not from the lines, so the pair sums to zero by construction (R15).
        var reversing = entries
            .OrderBy(e => e.LineNo).ThenByDescending(e => e.Quantity)
            .Select(e => new StockLedgerEntry(e.ArticleId, e.WarehouseId, -e.Quantity, reversal.Id, e.LineNo, documentDate, now, actorKeyId))
            .ToList();

        Status = StockDocumentStatus.Reversed;
        ReversedById = reversal.Id;
        return (reversal, reversing);
    }

    private void EnsureDraft()
    {
        if (!IsDraft)
            throw new InvalidOperationException("Only a draft stock document can be changed or posted.");
    }
}

/// <summary>
/// One line of a stock document (ADR-0014, decision 4): what was entered - an article, a unit of that article
/// and a quantity in that unit - and, once the document is posted, the factor used and the resulting quantity
/// in the article's base unit. A draft line has neither: it is converted with the article's current factor
/// whenever it is read and when it is posted.
/// </summary>
public sealed class StockDocumentLine : ITenantOwned
{
    private StockDocumentLine() { }

    internal StockDocumentLine(Guid documentId, int lineNo, StockLineEntry entry, decimal? bookQuantity = null, OrderLink? link = null)
    {
        Id = Guid.CreateVersion7();
        DocumentId = documentId;
        LineNo = lineNo;
        Set(entry, bookQuantity, link);
    }

    /// <summary>A line of a reversing document: the posted line as it is, factor and base quantity included (spec 007, R20).</summary>
    internal static StockDocumentLine CopyOfPosted(Guid documentId, StockDocumentLine posted)
    {
        if (posted.Factor is not { } factor || posted.BaseQuantity is not { } baseQuantity)
            throw new InvalidOperationException("Only a posted line has a factor and a base quantity to copy.");
        var copy = new StockDocumentLine(documentId, posted.LineNo, posted.Entry, posted.BookQuantity, posted.Link);
        copy.Freeze(factor, baseQuantity);
        return copy;
    }

    public Guid Id { get; private set; }

    /// <summary>The tenant of the document.</summary>
    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    /// <summary>1…n, the position on the document.</summary>
    public int LineNo { get; private set; }

    public Guid ArticleId { get; private set; }

    /// <summary>The unit the quantity was entered in: the article's base unit or one of its alternative units.</summary>
    public Guid UnitId { get; private set; }

    /// <summary>As entered, in <see cref="UnitId"/>.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>The factor the line was posted with; null while the document is a draft. Never changes afterwards (R17).</summary>
    public decimal? Factor { get; private set; }

    /// <summary>The quantity in the article's base unit, as posted; null while the document is a draft. Never changes afterwards (R17).</summary>
    public decimal? BaseQuantity { get; private set; }

    /// <summary>
    /// On a count line (spec 008, R6): stock on hand of the article in the document's warehouse when the draft
    /// was last saved, in the article's base unit. Written by every save, never by reading; kept for good once
    /// posted. Null on every other type.
    /// </summary>
    public decimal? BookQuantity { get; private set; }

    /// <summary>
    /// On a posted count line (spec 008, R7): the base quantity minus the book quantity, which is what the
    /// line wrote to the ledger. Null on a draft (its base quantity follows the current factor) and on every
    /// other type.
    /// </summary>
    public decimal? DifferenceQuantity =>
        BaseQuantity is { } counted && BookQuantity is { } book ? CountRules.Difference(counted, book) : null;

    /// <summary>
    /// On a line of a linked document (spec 009, R20): the document's order, repeated here so that the line
    /// can reference its order line by a foreign key. Null on an unlinked document.
    /// </summary>
    public Guid? PurchaseOrderId { get; private set; }

    /// <summary>As <see cref="PurchaseOrderId"/>, on a line of a delivery (spec 010): the document's sales order.</summary>
    public Guid? SalesOrderId { get; private set; }

    /// <summary>The number of the order line this line fulfils - of whichever order the document is linked to; null on an unlinked document.</summary>
    public int? OrderLineNo { get; private set; }

    private OrderLink? Link =>
        PurchaseOrderId is { } purchaseOrderId ? new OrderLink(OrderSide.Purchase, purchaseOrderId)
        : SalesOrderId is { } salesOrderId ? new OrderLink(OrderSide.Sales, salesOrderId)
        : null;

    internal void Set(StockLineEntry entry, decimal? bookQuantity = null, OrderLink? link = null)
    {
        if (link is null != entry.OrderLineNo is null)
            throw new ArgumentException("A line names an order line exactly when its document is linked to an order.", nameof(entry));
        ArticleId = entry.ArticleId;
        UnitId = entry.UnitId;
        Quantity = entry.Quantity;
        BookQuantity = bookQuantity;
        PurchaseOrderId = link is { Side: OrderSide.Purchase } ? link.Value.OrderId : null;
        SalesOrderId = link is { Side: OrderSide.Sales } ? link.Value.OrderId : null;
        OrderLineNo = entry.OrderLineNo;
    }

    internal void Freeze(decimal factor, decimal baseQuantity)
    {
        if (Factor is not null || BaseQuantity is not null)
            throw new InvalidOperationException("A posted line keeps its factor and base quantity.");
        Factor = factor;
        BaseQuantity = baseQuantity;
    }

    /// <summary>What was entered.</summary>
    public StockLineEntry Entry => new(ArticleId, UnitId, Quantity, OrderLineNo);

    /// <summary>What the posted line means for stock: the article and its base quantity (R19).</summary>
    /// <exception cref="InvalidOperationException">The line is not posted.</exception>
    public StockLineValues BaseValues =>
        new(ArticleId, BaseQuantity ?? throw new InvalidOperationException("A draft line has no base quantity yet."));
}

/// <summary>
/// The unit of truth of stock (ADR-0012, decision 2): a signed quantity of an article in a warehouse, with the
/// document line that produced it. Created only by <see cref="StockDocument.Post"/> and
/// <see cref="StockDocument.Reverse"/>; never changed or deleted.
/// Stock on hand of an (article, warehouse) pair is the sum of its entries.
/// </summary>
public sealed class StockLedgerEntry : ITenantOwned
{
    private StockLedgerEntry() { }

    internal StockLedgerEntry(
        Guid articleId, Guid warehouseId, decimal quantity, Guid documentId, int lineNo, DateOnly documentDate,
        DateTime postedAt, Guid postedBy)
    {
        Id = Guid.CreateVersion7();
        ArticleId = articleId;
        WarehouseId = warehouseId;
        Quantity = quantity;
        DocumentId = documentId;
        LineNo = lineNo;
        DocumentDate = documentDate;
        PostedAt = postedAt;
        PostedBy = postedBy;
    }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public Guid ArticleId { get; private set; }
    public Guid WarehouseId { get; private set; }

    /// <summary>Positive: into the warehouse; negative: out of it. In the article's base unit.</summary>
    public decimal Quantity { get; private set; }

    public Guid DocumentId { get; private set; }
    public int LineNo { get; private set; }
    public DateOnly DocumentDate { get; private set; }
    public DateTime PostedAt { get; private set; }
    public Guid PostedBy { get; private set; }
}

/// <summary>
/// The last document number a tenant has used in one number series (ADR-0012, decision 6): one per stock
/// document type and one per kind of order. It is read and advanced only inside the transaction that posts or
/// confirms, so numbers are gapless and in that order.
/// </summary>
public sealed class DocumentCounter : ITenantOwned
{
    private DocumentCounter() { }

    public Guid TenantId { get; private set; }

    /// <summary>The key of the series: <c>receipt</c>, <c>issue</c>, <c>transfer</c>, <c>count</c>, <c>purchaseOrder</c>.</summary>
    public string DocumentType { get; private set; } = "";

    public long LastNumber { get; private set; }

    public static DocumentCounter Start(Guid tenantId, DocumentSeries series) =>
        new() { TenantId = tenantId, DocumentType = series.Key, LastNumber = 0 };

    /// <summary>Advances the counter by exactly one and returns the new value.</summary>
    public long Next() => ++LastNumber;
}
