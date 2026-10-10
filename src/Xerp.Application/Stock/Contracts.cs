using Xerp.Application.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Stock;

/// <summary>
/// A line with the current code and name of its article and units (R23; spec 007, 4.3). <c>Unit</c> and
/// <c>Quantity</c> are what was entered; <c>Factor</c>, <c>BaseUnit</c> and <c>BaseQuantity</c> say what that is in
/// the article's base unit - with the article's current factor on a draft, as posted on a posted document.
/// <c>BookQuantity</c> and <c>DifferenceQuantity</c> are null unless the document is a count (spec 008, section 4):
/// the stock the count was saved against and the base quantity minus it, both in the base unit.
/// </summary>
/// <param name="OrderLineNo">The number of the order line the line fulfils; null unless the document is linked to an order (spec 009, 4.2).</param>
public sealed record StockDocumentLineDto(
    int LineNo, ReferenceSummary Article, ReferenceSummary Unit, decimal Quantity,
    decimal Factor, ReferenceSummary BaseUnit, decimal BaseQuantity, decimal? BookQuantity, decimal? DifferenceQuantity,
    int? OrderLineNo);

/// <summary>Another stock document, as a document links to it: the reversed original or the reversing document.</summary>
public sealed record StockDocumentLinkDto(Guid Id, string Number);

/// <summary>The order a stock document fulfils, as the document shows it (spec 009, 4.2).</summary>
public sealed record OrderLinkDto(Guid Id, string Number);

/// <summary>
/// <c>ToWarehouse</c> is null unless the document is a transfer; <c>ReversalOf</c> is set on a reversing
/// document and <c>ReversedBy</c> on a reversed one (spec 006, 4.1); <c>PurchaseOrder</c> is set on a receipt
/// linked to a purchase order (spec 009, 4.2) and <c>SalesOrder</c> on an issue linked to a sales order (spec
/// 010, 4.2). <c>Partner</c> is the supplier of a receipt or the customer of an issue, with its current code
/// and name (spec 011a, section 4): the order's partner on a linked document, null where none was named and
/// on every transfer and count. All six are always present.
/// </summary>
public sealed record StockDocumentDto(
    Guid Id,
    string Type,
    string Status,
    string? Number,
    DateOnly DocumentDate,
    ReferenceSummary Warehouse,
    ReferenceSummary? ToWarehouse,
    StockDocumentLinkDto? ReversalOf,
    StockDocumentLinkDto? ReversedBy,
    OrderLinkDto? PurchaseOrder,
    OrderLinkDto? SalesOrder,
    ReferenceSummary? Partner,
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
    ReferenceSummary? ToWarehouse,
    StockDocumentLinkDto? ReversalOf,
    StockDocumentLinkDto? ReversedBy,
    OrderLinkDto? PurchaseOrder,
    OrderLinkDto? SalesOrder,
    ReferenceSummary? Partner,
    string? Reference,
    string? Note,
    int LineCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTime? PostedAt,
    Guid? PostedBy);

/// <summary>
/// Stock on hand of one (article, warehouse) pair: <c>Quantity</c> is its stored balance, which is the sum of its ledger entries (R19; spec 011, R23);
/// <c>IncomingQuantity</c> is what confirmed purchase orders still expect into the warehouse, in base units
/// (spec 009, R35); <c>ReservedQuantity</c> what confirmed sales orders shipping from it still owe, and
/// <c>AvailableQuantity</c> is <c>Quantity</c> minus that, possibly negative (spec 010, R15, R16). An order
/// never changes <c>Quantity</c>, and by default a reservation blocks nothing (rule <c>sales.reservedStockProtected</c>).
/// </summary>
public sealed record StockOnHandDto(
    ReferenceSummary Article, ReferenceSummary Warehouse, ReferenceSummary Unit, decimal Quantity, decimal IncomingQuantity,
    decimal ReservedQuantity, decimal AvailableQuantity);

/// <summary>The posted document a ledger entry came from; <c>IsReversal</c> when that is a reversing document.</summary>
public sealed record LedgerDocumentDto(Guid Id, string Number, string Type, bool IsReversal);

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

/// <summary>
/// A line of a request: the ids are the raw strings, so a malformed one is reported with the other invalid
/// fields. <c>UnitId</c> omitted or null means the article's base unit (spec 007, R12).
/// </summary>
/// <param name="OrderLineNo">On a document linked to an order: the number of the order line the line fulfils (spec 009, R20).</param>
public sealed record StockLineInput(string? ArticleId = null, decimal? Quantity = null, string? UnitId = null, int? OrderLineNo = null);

/// <summary>A validated line of a request: well-formed, not yet known to exist. <c>UnitId</c> null is the article's base unit.</summary>
public readonly record struct StockLineRequest(Guid ArticleId, decimal Quantity, Guid? UnitId = null, int? OrderLineNo = null);

/// <param name="WarehouseId">Omitted or null on a receipt, an issue or a count: the tenant's default warehouse, or the linked order's warehouse (spec 011, R8, R10).</param>
/// <param name="PurchaseOrderId">The purchase order a receipt fulfils; omitted or null for an unlinked document (spec 009, R18).</param>
/// <param name="SalesOrderId">The sales order an issue delivers; omitted or null for an unlinked document (spec 010, R5, R6).</param>
/// <param name="PartnerId">The supplier of a receipt or the customer of an issue; omitted and null are equal (spec 011a, R3).</param>
public sealed record CreateStockDocumentInput(
    string? Type = null, string? DocumentDate = null, string? WarehouseId = null,
    IReadOnlyList<StockLineInput?>? Lines = null, string? Reference = null, string? Note = null, string? ToWarehouseId = null,
    string? PurchaseOrderId = null, string? SalesOrderId = null, string? PartnerId = null);

/// <summary>
/// All five fields must be present (R7); <c>Reference</c> and <c>Note</c> may be null but must have been given.
/// There is no <c>type</c>: it never changes (R1). <c>ToWarehouseId</c> is needed for a transfer and must be
/// absent or null for the other types (spec 006, 4.1), which only the stored document can tell. There is no
/// <c>purchaseOrderId</c> or <c>salesOrderId</c> either: the link is set at creation and never changes (spec 009, R19).
/// <c>PartnerId</c> must be present, possibly null, for a receipt and an issue - a partner is never dropped by
/// omission - and absent or null for the other types (spec 011a, R1, R3), which again only the stored document can tell.
/// </summary>
public sealed record ReplaceStockDocumentInput : TrackedInput
{
    private readonly string? _reference;
    private readonly string? _note;
    private readonly string? _partnerId;

    public string? DocumentDate { get; init; }
    public string? WarehouseId { get; init; }
    public string? ToWarehouseId { get; init; }
    public string? Reference { get => _reference; init => _reference = Given(value); }
    public string? Note { get => _note; init => _note = Given(value); }
    public IReadOnlyList<StockLineInput?>? Lines { get; init; }
    public string? PartnerId { get => _partnerId; init => _partnerId = Given(value); }
}

/// <summary>The body of a reversal (spec 006, 4.2): the reversal's own date and note.</summary>
public sealed record ReverseStockDocumentInput(string? DocumentDate = null, string? Note = null);

/// <summary>Validated input of a reversal; whether the date is early enough only the original can tell.</summary>
public sealed record StockReversalValues(DateOnly DocumentDate, string? Note);

public sealed record ListStockDocumentsInput(
    string? Type = null, string? Status = null, string? WarehouseId = null, string? Search = null,
    int? Limit = null, int? Offset = null, string? PurchaseOrderId = null, string? SalesOrderId = null, string? PartnerId = null);

/// <summary>How a client without a URL path names one document: by <c>id</c> or by <c>number</c>.</summary>
public sealed record StockDocumentAddressInput(string? Id = null, string? Number = null);

public sealed record ListStockOnHandInput(string? ArticleId = null, string? WarehouseId = null, int? Limit = null, int? Offset = null);

/// <summary>
/// The stock list of one warehouse (spec 011, 4.3). <c>Id</c> is the warehouse, for clients without a URL path;
/// <c>Search</c> and <c>IsActive</c> are the article's; <c>HasStock</c> keeps the items with or without stock.
/// </summary>
public sealed record ListWarehouseStockInput(
    string? Id = null, string? Search = null, bool? IsActive = null, bool? HasStock = null, int? Limit = null, int? Offset = null);

public sealed record ListStockLedgerEntriesInput(
    string? ArticleId = null, string? WarehouseId = null, string? DocumentId = null, int? Limit = null, int? Offset = null);

/// <summary>Validated header and lines of a document. The references are well-formed, not yet known to exist.</summary>
/// <param name="PartnerId">The partner the request names; null when it names none (spec 011a, R3).</param>
/// <param name="PartnerGiven">False when a replace left <c>partnerId</c> out, which a receipt and an issue may not.</param>
public sealed record StockDocumentValues(
    DateOnly DocumentDate, Guid WarehouseId, Guid? ToWarehouseId, string? Reference, string? Note, IReadOnlyList<StockLineRequest> Lines,
    Guid? PartnerId = null, bool PartnerGiven = true);

/// <summary>Validated input of a create: the type, the values and the order the document is linked to, if any.</summary>
/// <param name="WarehouseOmitted">
/// Spec 011, R8: the request named no warehouse (omitted or null). <c>Values.WarehouseId</c> is then empty and
/// the operation resolves it - the linked order's warehouse, otherwise the tenant's default - before anything else.
/// </param>
public sealed record NewStockDocumentValues(
    StockDocumentType Type, StockDocumentValues Values, OrderLink? Link = null, bool WarehouseOmitted = false);

public sealed record StockDocumentListQuery(
    StockDocumentType? Type, StockDocumentStatus? Status, Guid? WarehouseId, string? Search, int Limit, int Offset, Guid? PurchaseOrderId = null, Guid? SalesOrderId = null,
    Guid? PartnerId = null);

public sealed record StockOnHandQuery(Guid? ArticleId, Guid? WarehouseId, int Limit, int Offset);

public sealed record StockLedgerQuery(Guid? ArticleId, Guid? WarehouseId, Guid? DocumentId, int Limit, int Offset);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record StockDocumentDeleted(bool Deleted = true);
