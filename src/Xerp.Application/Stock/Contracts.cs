using Xerp.Application.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>A line with the current code and name of its article and of the article's base unit (R23).</summary>
public sealed record StockDocumentLineDto(int LineNo, ReferenceSummary Article, ReferenceSummary Unit, decimal Quantity);

public sealed record StockDocumentDto(
    Guid Id,
    string Type,
    string Status,
    string? Number,
    DateOnly DocumentDate,
    ReferenceSummary Warehouse,
    string? Reference,
    string? Note,
    IReadOnlyList<StockDocumentLineDto> Lines,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTime? PostedAt,
    Guid? PostedBy);

/// <summary>A document as a list shows it: without its lines, with their count.</summary>
public sealed record StockDocumentSummaryDto(
    Guid Id,
    string Type,
    string Status,
    string? Number,
    DateOnly DocumentDate,
    ReferenceSummary Warehouse,
    string? Reference,
    string? Note,
    int LineCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTime? PostedAt,
    Guid? PostedBy);

/// <summary>Stock on hand of one (article, warehouse) pair: the sum of its ledger entries (R19).</summary>
public sealed record StockOnHandDto(ReferenceSummary Article, ReferenceSummary Warehouse, ReferenceSummary Unit, decimal Quantity);

/// <summary>The posted document a ledger entry came from.</summary>
public sealed record LedgerDocumentDto(Guid Id, string Number, string Type);

public sealed record StockLedgerEntryDto(
    Guid Id,
    ReferenceSummary Article,
    ReferenceSummary Warehouse,
    ReferenceSummary Unit,
    decimal Quantity,
    DateOnly DocumentDate,
    DateTime PostedAt,
    Guid PostedBy,
    LedgerDocumentDto Document,
    int LineNo);

/// <summary>A line of a request: the id is the raw string, so a malformed one is reported with the other invalid fields.</summary>
public sealed record StockLineInput(string? ArticleId = null, decimal? Quantity = null);

public sealed record CreateStockDocumentInput(
    string? Type = null, string? DocumentDate = null, string? WarehouseId = null,
    IReadOnlyList<StockLineInput?>? Lines = null, string? Reference = null, string? Note = null);

/// <summary>
/// All five fields must be present (R7); <c>Reference</c> and <c>Note</c> may be null but must have been given.
/// There is no <c>type</c>: it never changes (R1).
/// </summary>
public sealed record ReplaceStockDocumentInput : TrackedInput
{
    private readonly string? _reference;
    private readonly string? _note;

    public string? DocumentDate { get; init; }
    public string? WarehouseId { get; init; }
    public string? Reference { get => _reference; init => _reference = Given(value); }
    public string? Note { get => _note; init => _note = Given(value); }
    public IReadOnlyList<StockLineInput?>? Lines { get; init; }
}

public sealed record ListStockDocumentsInput(
    string? Type = null, string? Status = null, string? WarehouseId = null, string? Search = null,
    int? Limit = null, int? Offset = null);

/// <summary>How a client without a URL path names one document: by <c>id</c> or by <c>number</c>.</summary>
public sealed record StockDocumentAddressInput(string? Id = null, string? Number = null);

public sealed record ListStockOnHandInput(string? ArticleId = null, string? WarehouseId = null, int? Limit = null, int? Offset = null);

public sealed record ListStockLedgerEntriesInput(
    string? ArticleId = null, string? WarehouseId = null, string? DocumentId = null, int? Limit = null, int? Offset = null);

/// <summary>Validated header and lines of a document. The references are well-formed, not yet known to exist.</summary>
public sealed record StockDocumentValues(
    DateOnly DocumentDate, Guid WarehouseId, string? Reference, string? Note, IReadOnlyList<StockLineValues> Lines);

/// <summary>Validated input of a create: the type and the values.</summary>
public sealed record NewStockDocumentValues(StockDocumentType Type, StockDocumentValues Values);

public sealed record StockDocumentListQuery(
    StockDocumentType? Type, StockDocumentStatus? Status, Guid? WarehouseId, string? Search, int Limit, int Offset);

public sealed record StockOnHandQuery(Guid? ArticleId, Guid? WarehouseId, int Limit, int Offset);

public sealed record StockLedgerQuery(Guid? ArticleId, Guid? WarehouseId, Guid? DocumentId, int Limit, int Offset);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record StockDocumentDeleted(bool Deleted = true);
