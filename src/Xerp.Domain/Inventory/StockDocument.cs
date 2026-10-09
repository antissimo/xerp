using Xerp.Domain.Common;

namespace Xerp.Domain.Inventory;

public enum StockDocumentType
{
    /// <summary>Goods come into the warehouse: one positive ledger entry per line.</summary>
    Receipt,

    /// <summary>Goods leave the warehouse: one negative ledger entry per line.</summary>
    Issue,
}

/// <summary>The contract names of <see cref="StockDocumentType"/>, as stored and as sent to clients.</summary>
public static class StockDocumentTypeNames
{
    public const string Receipt = "receipt";
    public const string Issue = "issue";

    public static string ToName(this StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => Receipt,
        StockDocumentType.Issue => Issue,
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
}

/// <summary>The contract names of <see cref="StockDocumentStatus"/>.</summary>
public static class StockDocumentStatusNames
{
    public const string Draft = "draft";
    public const string Posted = "posted";

    public static string ToName(this StockDocumentStatus status) => status switch
    {
        StockDocumentStatus.Draft => Draft,
        StockDocumentStatus.Posted => Posted,
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

    public Guid WarehouseId { get; private set; }
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

    public static StockDocument Create(
        StockDocumentType type, DateOnly documentDate, Guid warehouseId, string? reference, string? note,
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
        document.Replace(documentDate, warehouseId, reference, note, lines, now, actorKeyId);
        return document;
    }

    /// <summary>
    /// Replaces header and lines of a draft; lines are renumbered 1…n in the order given (R4, R10).
    /// Returns the lines that are no longer part of the document, for the caller to delete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is posted (R11).</exception>
    public IReadOnlyList<StockDocumentLine> Replace(
        DateOnly documentDate, Guid warehouseId, string? reference, string? note,
        IReadOnlyList<StockLineValues> lines, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
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
    /// Posts the draft (R12, R14): status, number and posting attribution are set and one ledger entry per
    /// line is returned - positive for a receipt, negative for an issue. Whether stock suffices and which
    /// number is next is decided by the caller, inside the same transaction that saves the result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is already posted (R11).</exception>
    public IReadOnlyList<StockLedgerEntry> Post(string number, DateTime now, Guid actorKeyId)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("A posted document needs a number.", nameof(number));

        Status = StockDocumentStatus.Posted;
        Number = number;
        PostedAt = now;
        PostedBy = actorKeyId;

        var sign = Type == StockDocumentType.Issue ? -1 : 1;
        return Lines
            .Select(l => new StockLedgerEntry(l.ArticleId, WarehouseId, sign * l.Quantity, Id, l.LineNo, DocumentDate, now, actorKeyId))
            .ToList();
    }

    private void EnsureDraft()
    {
        if (!IsDraft)
            throw new InvalidOperationException("A posted stock document cannot be changed.");
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
/// document line that produced it. Created only by <see cref="StockDocument.Post"/>; never changed or deleted.
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

    /// <summary>The contract name of the document type (<c>receipt</c>, <c>issue</c>).</summary>
    public string DocumentType { get; private set; } = "";

    public long LastNumber { get; private set; }

    public static DocumentCounter Start(Guid tenantId, StockDocumentType type) =>
        new() { TenantId = tenantId, DocumentType = type.ToName(), LastNumber = 0 };

    /// <summary>Advances the counter by exactly one and returns the new value.</summary>
    public long Next() => ++LastNumber;
}
