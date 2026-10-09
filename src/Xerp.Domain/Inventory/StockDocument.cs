using Xerp.Domain.Common;

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
}

/// <summary>The contract names of <see cref="StockDocumentType"/>, as stored and as sent to clients.</summary>
public static class StockDocumentTypeNames
{
    public const string Receipt = "receipt";
    public const string Issue = "issue";
    public const string Transfer = "transfer";

    public static string ToName(this StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => Receipt,
        StockDocumentType.Issue => Issue,
        StockDocumentType.Transfer => Transfer,
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

    /// <summary>The warehouse of a receipt or issue; the source of a transfer.</summary>
    public Guid WarehouseId { get; private set; }

    /// <summary>The destination of a transfer; null on every other type (spec 006, R2, R3).</summary>
    public Guid? ToWarehouseId { get; private set; }

    /// <summary>On a reversing document: the document it reverses. Null on every other document.</summary>
    public Guid? ReversalOfId { get; private set; }

    /// <summary>On a reversed document: the document that reversed it. Null until then.</summary>
    public Guid? ReversedById { get; private set; }

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

    public static StockDocument Create(
        StockDocumentType type, DateOnly documentDate, Guid warehouseId, Guid? toWarehouseId, string? reference, string? note,
        IReadOnlyList<StockLineValues> lines, DateTime now, Guid actorKeyId)
    {
        var document = new StockDocument
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Status = StockDocumentStatus.Draft,
            CreatedAt = now,
            CreatedBy = actorKeyId,
        };
        document.Replace(documentDate, warehouseId, toWarehouseId, reference, note, lines, now, actorKeyId);
        return document;
    }

    /// <summary>
    /// Replaces header and lines of a draft; lines are renumbered 1…n in the order given (R4, R10).
    /// Returns the lines that are no longer part of the document, for the caller to delete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is posted (R11).</exception>
    public IReadOnlyList<StockDocumentLine> Replace(
        DateOnly documentDate, Guid warehouseId, Guid? toWarehouseId, string? reference, string? note,
        IReadOnlyList<StockLineValues> lines, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
        if (!TransferRules.IsValidDestination(Type, warehouseId, toWarehouseId))
            throw new ArgumentException(
                "A transfer needs a destination warehouse other than its source; other documents have none.", nameof(toWarehouseId));
        // Validate everything before assigning anything, so a rejected replace leaves the draft untouched.
        var newReference = OptionalTextRules.Normalize(reference, ReferenceMaxLength, nameof(reference));
        if (!NoteRules.TryNormalize(note, NoteMaxLength, out var newNote))
            throw new ArgumentException("Invalid note.", nameof(note));
        if (lines.Count is < 1 or > MaxLines)
            throw new ArgumentException($"A stock document has 1 to {MaxLines} lines.", nameof(lines));
        if (lines.Any(l => !QuantityRules.IsValid(l.Quantity)))
            throw new ArgumentException("A line quantity breaks the quantity rule.", nameof(lines));

        DocumentDate = documentDate;
        WarehouseId = warehouseId;
        ToWarehouseId = toWarehouseId;
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
                _lines.Add(new StockDocumentLine(Id, i + 1, lines[i]));
        }
        var removed = _lines.Skip(lines.Count).ToList();
        _lines.RemoveRange(lines.Count, removed.Count);
        return removed;
    }

    /// <summary>
    /// Posts the draft (R12, R14; spec 006, R6): status, number and posting attribution are set and the ledger
    /// entries are returned - one positive entry per line for a receipt, one negative for an issue, and for a
    /// transfer the outgoing entry in the source followed by the incoming entry in the destination. Whether
    /// stock suffices and which number is next is decided by the caller, inside the same transaction that
    /// saves the result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not a draft (R11).</exception>
    public IReadOnlyList<StockLedgerEntry> Post(string number, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("A posted document needs a number.", nameof(number));

        Status = StockDocumentStatus.Posted;
        Number = number;
        PostedAt = now;
        PostedBy = actorKeyId;

        return Lines
            .SelectMany(l => StockMovements.OfLine(Type, WarehouseId, ToWarehouseId, l.Values)
                .Select(m => new StockLedgerEntry(m.ArticleId, m.WarehouseId, m.Quantity, Id, l.LineNo, DocumentDate, now, actorKeyId)))
            .ToList();
    }

    /// <summary>
    /// Reverses this posted document (ADR-0013; spec 006, R11-R14): returns the reversing document - same type,
    /// warehouses, lines and reference, already posted under <paramref name="number"/> - and its ledger
    /// entries, which are <paramref name="entries"/> with the opposite sign. This document becomes
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
        if (entries.Count == 0 || entries.Any(e => e.DocumentId != Id))
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
            Reference = Reference,
            Note = reversalNote,
            PostedAt = now,
            PostedBy = actorKeyId,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actorKeyId,
            UpdatedBy = actorKeyId,
        };
        reversal._lines.AddRange(Lines.Select(l => new StockDocumentLine(reversal.Id, l.LineNo, l.Values)));

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

/// <summary>One line of a stock document: an article and a quantity in the article's base unit.</summary>
public sealed class StockDocumentLine : ITenantOwned
{
    private StockDocumentLine() { }

    internal StockDocumentLine(Guid documentId, int lineNo, StockLineValues values)
    {
        Id = Guid.CreateVersion7();
        DocumentId = documentId;
        LineNo = lineNo;
        Set(values);
    }

    public Guid Id { get; private set; }

    /// <summary>The tenant of the document.</summary>
    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    /// <summary>1…n, the position on the document.</summary>
    public int LineNo { get; private set; }

    public Guid ArticleId { get; private set; }
    public decimal Quantity { get; private set; }

    internal void Set(StockLineValues values)
    {
        ArticleId = values.ArticleId;
        Quantity = values.Quantity;
    }

    public StockLineValues Values => new(ArticleId, Quantity);
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
/// The last document number a tenant has used for one document type (ADR-0012, decision 6). It is read and
/// advanced only inside the posting transaction, so numbers are gapless and in posting order.
/// </summary>
public sealed class DocumentCounter : ITenantOwned
{
    private DocumentCounter() { }

    public Guid TenantId { get; private set; }

    /// <summary>The contract name of the document type (<c>receipt</c>, <c>issue</c>, <c>transfer</c>).</summary>
    public string DocumentType { get; private set; } = "";

    public long LastNumber { get; private set; }

    public static DocumentCounter Start(Guid tenantId, StockDocumentType type) =>
        new() { TenantId = tenantId, DocumentType = type.ToName(), LastNumber = 0 };

    /// <summary>Advances the counter by exactly one and returns the new value.</summary>
    public long Next() => ++LastNumber;
}
