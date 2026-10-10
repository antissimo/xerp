using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Application.Stock;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>
/// What stock documents and the masters read of orders, whatever their kind. This is the one place that
/// turns a kind into its tables; each read is written once, for every kind. <see cref="IXerpDb"/> is already
/// filtered to the current tenant.
/// </summary>
internal static class OrderReads
{
    /// <summary>The order of the current tenant a link names, as a linked document sees it at saving; null when there is none of that kind.</summary>
    public static Task<LinkedOrderFacts?> FactsAsync(IXerpDb db, OrderLink link, CancellationToken cancellationToken) => link.Side switch
    {
        OrderSide.Sales => FactsAsync<SalesOrder, SalesOrderLine>(db.SalesOrders, db.SalesOrderLines, link.OrderId, cancellationToken),
        _ => FactsAsync<PurchaseOrder, PurchaseOrderLine>(db.PurchaseOrders, db.PurchaseOrderLines, link.OrderId, cancellationToken),
    };

    /// <summary>The order a posting or a reversal changes the progress of, tracked and with its lines. Called under the tenant's lock.</summary>
    public static async Task<IFulfilledOrder> ForFulfilmentAsync(IXerpDb db, OrderLink link, CancellationToken cancellationToken) => link.Side switch
    {
        OrderSide.Sales => await db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == link.OrderId, cancellationToken),
        _ => await db.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == link.OrderId, cancellationToken),
    };

    /// <summary>Id and number of the linked orders. A document is linked to a confirmed order only, and an order never loses its number.</summary>
    public static async Task<Dictionary<Guid, OrderLinkDto>> NumbersAsync(IXerpDb db, IReadOnlyCollection<OrderLink> links, CancellationToken cancellationToken)
    {
        var numbers = new Dictionary<Guid, OrderLinkDto>();
        foreach (var side in links.Select(l => l.Side).Distinct())
        {
            var ids = links.Where(l => l.Side == side).Select(l => l.OrderId).ToList();
            var found = side == OrderSide.Sales
                ? await NumbersAsync<SalesOrder, SalesOrderLine>(db.SalesOrders, ids, cancellationToken)
                : await NumbersAsync<PurchaseOrder, PurchaseOrderLine>(db.PurchaseOrders, ids, cancellationToken);
            foreach (var order in found)
                numbers[order.Id] = order;
        }
        return numbers;
    }

    /// <summary>Whether an order of any kind names the partner (spec 009, R37; spec 010, R21).</summary>
    public static async Task<bool> AnyForPartnerAsync(IXerpDb db, Guid partnerId, CancellationToken cancellationToken) =>
        await db.PurchaseOrders.AnyAsync(o => o.PartnerId == partnerId, cancellationToken)
        || await db.SalesOrders.AnyAsync(o => o.PartnerId == partnerId, cancellationToken);

    public static async Task<bool> AnyForWarehouseAsync(IXerpDb db, Guid warehouseId, CancellationToken cancellationToken) =>
        await db.PurchaseOrders.AnyAsync(o => o.WarehouseId == warehouseId, cancellationToken)
        || await db.SalesOrders.AnyAsync(o => o.WarehouseId == warehouseId, cancellationToken);

    public static async Task<bool> AnyLineForArticleAsync(IXerpDb db, Guid articleId, CancellationToken cancellationToken) =>
        await db.PurchaseOrderLines.AnyAsync(l => l.ArticleId == articleId, cancellationToken)
        || await db.SalesOrderLines.AnyAsync(l => l.ArticleId == articleId, cancellationToken);

    public static async Task<bool> AnyLineInUnitAsync(IXerpDb db, Guid unitId, CancellationToken cancellationToken) =>
        await db.PurchaseOrderLines.AnyAsync(l => l.UnitId == unitId, cancellationToken)
        || await db.SalesOrderLines.AnyAsync(l => l.UnitId == unitId, cancellationToken);

    /// <summary>The draft orders, of any kind, with a line for the article in the unit: a draft follows the current factor (spec 009, R38).</summary>
    public static async Task<int> DraftsUsingConversionAsync(IXerpDb db, Guid articleId, Guid unitId, CancellationToken cancellationToken) =>
        await DraftsUsingAsync<PurchaseOrder, PurchaseOrderLine>(db.PurchaseOrders, db.PurchaseOrderLines, articleId, unitId, cancellationToken)
        + await DraftsUsingAsync<SalesOrder, SalesOrderLine>(db.SalesOrders, db.SalesOrderLines, articleId, unitId, cancellationToken);

    private static async Task<LinkedOrderFacts?> FactsAsync<TOrder, TLine>(
        IQueryable<TOrder> orders, IQueryable<TLine> lines, Guid orderId, CancellationToken cancellationToken)
        where TOrder : Order<TLine> where TLine : OrderLine
    {
        var order = await orders.AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.Status, o.WarehouseId, o.PartnerId })
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null)
            return null;
        var lineArticles = await lines.AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .ToDictionaryAsync(l => l.LineNo, l => l.ArticleId, cancellationToken);
        return new LinkedOrderFacts(orderId, order.Status, order.WarehouseId, lineArticles, order.PartnerId);
    }

    private static Task<List<OrderLinkDto>> NumbersAsync<TOrder, TLine>(IQueryable<TOrder> orders, List<Guid> ids, CancellationToken cancellationToken)
        where TOrder : Order<TLine> where TLine : OrderLine =>
        orders.AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => new OrderLinkDto(o.Id, o.Number!)).ToListAsync(cancellationToken);

    private static Task<int> DraftsUsingAsync<TOrder, TLine>(
        IQueryable<TOrder> orders, IQueryable<TLine> lines, Guid articleId, Guid unitId, CancellationToken cancellationToken)
        where TOrder : Order<TLine> where TLine : OrderLine =>
        lines.Where(l => l.ArticleId == articleId && l.UnitId == unitId)
            .Join(orders.Where(o => o.Status == OrderStatus.Draft), l => l.OrderId, o => o.Id, (l, o) => o.Id)
            .Distinct()
            .CountAsync(cancellationToken);
}
