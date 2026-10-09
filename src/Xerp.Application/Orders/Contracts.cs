using Xerp.Application.Common;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>
/// A line of a purchase order with the current code and name of its article and units (spec 009, 4.1).
/// <c>Unit</c>, <c>Quantity</c> and <c>UnitPrice</c> are what was entered; <c>LineAmount</c> follows from the last
/// two alone. <c>Factor</c>, <c>BaseUnit</c> and <c>BaseQuantity</c> say what was ordered in the article's base
/// unit - with the article's current factor on a draft, as confirmed afterwards. <c>ReceivedBaseQuantity</c> is
/// what posted receipts brought in for this line and <c>OutstandingBaseQuantity</c> what can still be received,
/// both in base units.
/// </summary>
public sealed record PurchaseOrderLineDto(
    int LineNo, ReferenceSummary Article, ReferenceSummary Unit, decimal Quantity,
    decimal UnitPrice, decimal LineAmount,
    decimal Factor, ReferenceSummary BaseUnit, decimal BaseQuantity,
    decimal ReceivedBaseQuantity, decimal OutstandingBaseQuantity);

/// <summary>A purchase order (spec 009, 4.1). Every property is always present.</summary>
public sealed record PurchaseOrderDto(
    Guid Id,
    string Status,
    string? Number,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    ReferenceSummary Supplier,
    ReferenceSummary Warehouse,
    string? Reference,
    string? Note,
    string ReceiptStatus,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    decimal TotalAmount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTime? ConfirmedAt,
    Guid? ConfirmedBy,
    DateTime? ClosedAt,
    Guid? ClosedBy);

/// <summary>A purchase order as a list shows it: without its lines, with their count.</summary>
public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string Status,
    string? Number,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    ReferenceSummary Supplier,
    ReferenceSummary Warehouse,
    string? Reference,
    string? Note,
    string ReceiptStatus,
    int LineCount,
    decimal TotalAmount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTime? ConfirmedAt,
    Guid? ConfirmedBy,
    DateTime? ClosedAt,
    Guid? ClosedBy);

/// <summary>
/// A line of an order request: the ids are the raw strings, so a malformed one is reported with the other
/// invalid fields. <c>UnitId</c> omitted or null means the article's base unit; <c>UnitPrice</c> is the price of
/// one unit of the line.
/// </summary>
public sealed record OrderLineInput(string? ArticleId = null, decimal? Quantity = null, decimal? UnitPrice = null, string? UnitId = null);

public sealed record CreatePurchaseOrderInput(
    string? OrderDate = null, string? SupplierId = null, string? WarehouseId = null,
    IReadOnlyList<OrderLineInput?>? Lines = null, string? ExpectedDate = null, string? Reference = null, string? Note = null);

/// <summary>
/// All seven fields must be present (spec 009, R4); <c>ExpectedDate</c>, <c>Reference</c> and <c>Note</c> may be
/// null but must have been given.
/// </summary>
public sealed record ReplacePurchaseOrderInput : TrackedInput
{
    private readonly string? _expectedDate;
    private readonly string? _reference;
    private readonly string? _note;

    public string? OrderDate { get; init; }
    public string? ExpectedDate { get => _expectedDate; init => _expectedDate = Given(value); }
    public string? SupplierId { get; init; }
    public string? WarehouseId { get; init; }
    public string? Reference { get => _reference; init => _reference = Given(value); }
    public string? Note { get => _note; init => _note = Given(value); }
    public IReadOnlyList<OrderLineInput?>? Lines { get; init; }
}

public sealed record ListPurchaseOrdersInput(
    string? Status = null, string? ReceiptStatus = null, string? SupplierId = null, string? WarehouseId = null,
    string? Search = null, int? Limit = null, int? Offset = null);

/// <summary>How a client without a URL path names one order: by <c>id</c> or by <c>number</c>.</summary>
public sealed record OrderAddressInput(string? Id = null, string? Number = null);

/// <summary>A validated line of an order request: well-formed, not yet known to exist. <c>UnitId</c> null is the article's base unit.</summary>
public readonly record struct OrderLineRequest(Guid ArticleId, decimal Quantity, decimal UnitPrice, Guid? UnitId);

/// <summary>Validated header and lines of an order of any kind. The references are well-formed, not yet known to exist.</summary>
public sealed record OrderValues(
    DateOnly OrderDate, DateOnly? DueDate, Guid PartnerId, Guid WarehouseId, string? Reference, string? Note,
    IReadOnlyList<OrderLineRequest> Lines);

public sealed record OrderListQuery(
    OrderStatus? Status, FulfilmentStatus? Fulfilment, Guid? PartnerId, Guid? WarehouseId, string? Search, int Limit, int Offset);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record PurchaseOrderDeleted(bool Deleted = true);
