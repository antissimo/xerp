using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>
/// The purchase order operations of spec 009, section 4.1. One method = one HTTP endpoint = one MCP tool.
/// Everything they do is <see cref="OrderOperations{TOrder,TLine,TDto,TSummary}"/>; here are only the names a
/// purchase order gives to its supplier, its expected date and what was received.
/// </summary>
public sealed class PurchaseOrderOperations(IXerpDb db, ITenantContext context, IClock clock)
    : OrderOperations<PurchaseOrder, PurchaseOrderLine, PurchaseOrderDto, PurchaseOrderSummaryDto>(db, context, clock, OrderKind.Purchase)
{
    private readonly IXerpDb _db = db;

    protected override DbSet<PurchaseOrder> Orders => _db.PurchaseOrders;

    protected override DbSet<PurchaseOrderLine> OrderLines => _db.PurchaseOrderLines;

    public Task<Result<PagedResult<PurchaseOrderSummaryDto>>> ListAsync(ListPurchaseOrdersInput input, CancellationToken cancellationToken = default) =>
        ListAsync(
            new OrderListInput(input.Status, input.ReceiptStatus, input.SupplierId, input.WarehouseId, input.Search, input.Limit, input.Offset),
            cancellationToken);

    public Task<Result<PurchaseOrderDto>> CreateAsync(CreatePurchaseOrderInput input, CancellationToken cancellationToken = default) =>
        CreateAsync(
            new OrderInput(input.OrderDate, input.ExpectedDate, input.SupplierId, input.WarehouseId, input.Reference, input.Note, input.Lines),
            cancellationToken);

    public Task<Result<PurchaseOrderDto>> ReplaceAsync(Guid id, ReplacePurchaseOrderInput input, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, Values(input), cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PurchaseOrderDto>> ReplaceAsync(string? id, ReplacePurchaseOrderInput input, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, Values(input), cancellationToken);

    private static OrderInput Values(ReplacePurchaseOrderInput input) => new(
        input.OrderDate, input.ExpectedDate, input.SupplierId, input.WarehouseId, input.Reference, input.Note, input.Lines,
        input.Has(nameof(input.ExpectedDate)), input.Has(nameof(input.Reference)), input.Has(nameof(input.Note)));

    protected override PurchaseOrder NewOrder(OrderValues values, IReadOnlyList<OrderLineEntry> lines, DateTime now, Guid actorKeyId) =>
        PurchaseOrder.Create(
            values.OrderDate, values.DueDate, values.PartnerId, values.WarehouseId, values.Reference, values.Note, lines, now, actorKeyId);

    protected override PurchaseOrderDto ToDto(OrderView o) => new(
        o.Id, o.Status, o.Number, o.OrderDate, o.DueDate, o.Partner, o.Warehouse, o.Reference, o.Note, o.FulfilmentStatus,
        o.Lines.Select(l => new PurchaseOrderLineDto(
            l.LineNo, l.Article, l.Unit, l.Quantity, l.UnitPrice, l.LineAmount, l.Factor, l.BaseUnit, l.BaseQuantity,
            l.FulfilledBaseQuantity, l.OutstandingBaseQuantity)).ToList(),
        o.TotalAmount, o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy, o.ConfirmedAt, o.ConfirmedBy, o.ClosedAt, o.ClosedBy);

    protected override PurchaseOrderSummaryDto ToSummary(OrderView o) => new(
        o.Id, o.Status, o.Number, o.OrderDate, o.DueDate, o.Partner, o.Warehouse, o.Reference, o.Note, o.FulfilmentStatus,
        o.LineCount, o.TotalAmount, o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy, o.ConfirmedAt, o.ConfirmedBy, o.ClosedAt, o.ClosedBy);
}
