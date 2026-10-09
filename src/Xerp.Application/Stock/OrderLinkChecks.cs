using System.Globalization;
using Xerp.Application.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Stock;

/// <summary>What a stock document linked to an order needs to know about that order, read under the tenant's lock.</summary>
/// <param name="LineArticles">The article of every line of the order, by line number.</param>
public sealed record LinkedOrderFacts(Guid Id, OrderStatus Status, Guid WarehouseId, IReadOnlyDictionary<int, Guid> LineArticles);

/// <summary>
/// The checks the link to an order adds to saving and posting a stock document (ADR-0016; spec 009, R21, R24,
/// R26). No I/O: the operations read the order and ask here. The same checks serve every kind of order; the
/// request field that carries the link is what differs.
/// </summary>
public static class OrderLinkChecks
{
    public const string PurchaseOrderField = "purchaseOrderId";

    private const string ArticleField = "articleId";
    private const string QuantityField = "quantity";
    private const string WarehouseField = "warehouseId";

    /// <summary>
    /// R21, steps 1 and 2: null when the order exists and is confirmed; REFERENCE_NOT_FOUND when the tenant has
    /// no such order; ORDER_NOT_OPEN when it is a draft or closed.
    /// </summary>
    public static AppError? Open(string linkField, Guid orderId, LinkedOrderFacts? order) =>
        order is null ? AppError.ReferenceNotFound(linkField, orderId) : Open(linkField, order.Status, saving: true);

    /// <summary>R24: a document is saved and posted only against a confirmed order.</summary>
    public static AppError? Open(string linkField, OrderStatus status, bool saving = false)
    {
        if (status == OrderStatus.Confirmed)
            return null;
        var why = status == OrderStatus.Draft
            ? "The order is still a draft: it orders nothing until it is confirmed."
            : "The order is closed: nothing more is received or delivered against it until it is reopened.";
        return new AppError(ErrorCodes.OrderNotOpen,
            why + (saving ? " Nothing was saved." : " Nothing was posted and the document is still a draft.")
            + (status == OrderStatus.Draft ? " Confirm the order first." : " Reopen the order, or delete the draft document."),
            new Dictionary<string, string[]> { [linkField] = [$"The order is {status.ToName()}, not confirmed."] });
    }

    /// <summary>
    /// R21, step 4: null when the document agrees with its order, otherwise ORDER_MISMATCH with every
    /// applicable key - <c>warehouseId</c> when the document is for another warehouse than the order,
    /// <c>lines[i].articleId</c> when a line's article is not the article of the order line it names.
    /// </summary>
    /// <param name="lines">The lines of the document; every <c>OrderLineNo</c> is a line of the order.</param>
    public static AppError? Agreement(Guid documentWarehouseId, IReadOnlyList<StockLineEntry> lines, LinkedOrderFacts order)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (documentWarehouseId != order.WarehouseId)
            errors[WarehouseField] = [$"The order is for the warehouse with id '{order.WarehouseId}'."];
        for (var i = 0; i < lines.Count; i++)
        {
            var orderLineNo = lines[i].OrderLineNo ?? throw new InvalidOperationException("A line of a linked document names an order line.");
            if (order.LineArticles[orderLineNo] != lines[i].ArticleId)
                errors[StockDocumentValidation.LineKey(i, ArticleField)] =
                    [$"Order line {orderLineNo} is for the article with id '{order.LineArticles[orderLineNo]}'."];
        }
        return errors.Count == 0
            ? null
            : new AppError(ErrorCodes.OrderMismatch,
                "The document disagrees with the order it is linked to: an order is fulfilled in its own warehouse, and each line must name "
                + "the article of the order line it fulfils. Nothing was saved. Read the order and correct `warehouseId`, `articleId` or `orderLineNo`.",
                errors);
    }

    /// <summary>
    /// R26, never more than ordered: null when the document is within the outstanding quantities of the order,
    /// otherwise QUANTITY_EXCEEDS_ORDER with the quantity key of every document line that names an exceeded
    /// order line. Compared in base units.
    /// </summary>
    /// <param name="lines">Per document line, the order line it names and its base quantity as it posts now.</param>
    /// <param name="outstanding">Per order line number, what can still be fulfilled at this moment.</param>
    public static AppError? WithinOrder(IReadOnlyList<LineFulfilment> lines, IReadOnlyDictionary<int, decimal> outstanding)
    {
        var exceeding = OrderProgress.ExceedingLines(lines, outstanding);
        if (exceeding.Count == 0)
            return null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var index in exceeding)
        {
            var orderLineNo = lines[index].OrderLineNo;
            var total = lines.Where(l => l.OrderLineNo == orderLineNo).Sum(l => l.BaseQuantity);
            errors[StockDocumentValidation.LineKey(index, QuantityField)] =
                [$"The document has {Text(total)} for order line {orderLineNo} in total, in base units; {Text(outstanding.GetValueOrDefault(orderLineNo))} is outstanding."];
        }
        return new AppError(ErrorCodes.QuantityExceedsOrder,
            "The document would take an order line above its ordered quantity; nothing was posted and the draft is unchanged. "
            + "Read the order (`outstandingBaseQuantity` of its lines is what can still be fulfilled, in base units), lower the quantities and post again.",
            errors);
    }

    private static string Text(decimal quantity) => QuantityRules.Normalize(quantity).ToString(CultureInfo.InvariantCulture);
}
