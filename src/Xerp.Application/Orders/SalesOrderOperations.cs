using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>
/// The sales order operations of spec 010, section 4.1. One method = one HTTP endpoint = one MCP tool.
/// Everything they do is <see cref="OrderOperations{TOrder,TLine,TDto,TSummary}"/> - the code purchase orders
/// run too; here are only the names a sales order gives to its customer, its requested date and what was
/// delivered. Confirming one is never refused for stock (R4): what it promises shows as the reserved quantity
/// of stock on hand and, by default, blocks nothing (ADR-0017; rule <c>sales.reservedStockProtected</c>, which never judges a confirmation: spec 012, R31).
/// </summary>
public sealed class SalesOrderOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
    : OrderOperations<SalesOrder, SalesOrderLine, SalesOrderDto, SalesOrderSummaryDto>(db, context, clock, rules, OrderKind.Sales)
{
    private readonly IXerpDb _db = db;

    protected override DbSet<SalesOrder> Orders => _db.SalesOrders;

    protected override DbSet<SalesOrderLine> OrderLines => _db.SalesOrderLines;

    public Task<Result<PagedResult<SalesOrderSummaryDto>>> ListAsync(ListSalesOrdersInput input, CancellationToken cancellationToken = default) =>
        ListAsync(
            new OrderListInput(input.Status, input.DeliveryStatus, input.CustomerId, input.WarehouseId, input.Search, input.Limit, input.Offset),
            cancellationToken);

    public Task<Result<SalesOrderDto>> CreateAsync(CreateSalesOrderInput input, CancellationToken cancellationToken = default) =>
        CreateAsync(
            new OrderInput(
                input.OrderDate, input.RequestedDate, input.CustomerId, input.WarehouseId, input.Reference, input.Note, input.Lines,
                WarehouseOptional: true),
            cancellationToken);

    public Task<Result<SalesOrderDto>> ReplaceAsync(Guid id, ReplaceSalesOrderInput input, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, Values(input), cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<SalesOrderDto>> ReplaceAsync(string? id, ReplaceSalesOrderInput input, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, Values(input), cancellationToken);

    private static OrderInput Values(ReplaceSalesOrderInput input) => new(
        input.OrderDate, input.RequestedDate, input.CustomerId, input.WarehouseId, input.Reference, input.Note, input.Lines,
        input.Has(nameof(input.RequestedDate)), input.Has(nameof(input.Reference)), input.Has(nameof(input.Note)),
        PartnerGiven: input.Has(nameof(input.CustomerId)));

    protected override SalesOrder NewOrder(OrderValues values, IReadOnlyList<OrderLineEntry> lines, DateTime now, Guid actorKeyId) =>
        SalesOrder.Create(
            values.OrderDate, values.DueDate, values.PartnerId, values.WarehouseId, values.Reference, values.Note, lines, now, actorKeyId);

    protected override SalesOrderDto ToDto(OrderView o) => new(
        o.Id, o.Status, o.Number, o.OrderDate, o.DueDate, o.Partner, o.Warehouse, o.Reference, o.Note, o.FulfilmentStatus,
        o.Lines.Select(l => new SalesOrderLineDto(
            l.LineNo, l.Article, l.Unit, l.Quantity, l.UnitPrice, l.LineAmount, l.Factor, l.BaseUnit, l.BaseQuantity,
            l.FulfilledBaseQuantity, l.OutstandingBaseQuantity)).ToList(),
        o.TotalAmount, o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy, o.ConfirmedAt, o.ConfirmedBy, o.ClosedAt, o.ClosedBy);

    protected override SalesOrderSummaryDto ToSummary(OrderView o) => new(
        o.Id, o.Status, o.Number, o.OrderDate, o.DueDate, o.Partner, o.Warehouse, o.Reference, o.Note, o.FulfilmentStatus,
        o.LineCount, o.TotalAmount, o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy, o.ConfirmedAt, o.ConfirmedBy, o.ClosedAt, o.ClosedBy);
}
