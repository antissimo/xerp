using System.Globalization;
using Xerp.Application.Common;
using Xerp.Application.Stock;
using Xerp.Application.Warehouses;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>
/// What differs between the kinds of order (ADR-0016): how a request names the partner and the due date,
/// which role the partner needs, how the progress is called and which number series the order is in. The
/// rules themselves are the same for every kind.
/// </summary>
/// <param name="Side">Which side the order is on; it decides the stock document that fulfils it.</param>
/// <param name="Name">How messages call the order: <c>purchase order</c>.</param>
/// <param name="PartnerWord">How messages call the partner: <c>supplier</c>.</param>
/// <param name="PartnerField">The request field of the partner: <c>supplierId</c>.</param>
/// <param name="PartnerRole">The partner's flag the order needs: <c>isSupplier</c>.</param>
/// <param name="DueDateField">The request field of the due date: <c>expectedDate</c>.</param>
/// <param name="FulfilmentField">The list filter by progress: <c>receiptStatus</c>.</param>
/// <param name="LinkField">The field of a stock document that links it to an order of this kind: <c>purchaseOrderId</c>.</param>
public sealed record OrderKind(
    OrderSide Side, string Name, string PartnerWord, string PartnerField, string PartnerRole, string DueDateField,
    string FulfilmentField, string LinkField, DocumentSeries Series)
{
    public static readonly OrderKind Purchase = new(
        OrderSide.Purchase, "purchase order", "supplier", "supplierId", "isSupplier", "expectedDate", "receiptStatus",
        "purchaseOrderId", DocumentSeries.PurchaseOrder);

    /// <summary>Spec 010: the mirror of <see cref="Purchase"/>.</summary>
    public static readonly OrderKind Sales = new(
        OrderSide.Sales, "sales order", "customer", "customerId", "isCustomer", "requestedDate", "deliveryStatus",
        "salesOrderId", DocumentSeries.SalesOrder);

    public static readonly IReadOnlyList<OrderKind> All = [Purchase, Sales];

    public static OrderKind Of(OrderSide side) => side == OrderSide.Sales ? Sales : Purchase;

    /// <summary>The one type of stock document that can carry <see cref="LinkField"/>.</summary>
    public StockDocumentType FulfilledBy => Side.FulfilledBy();
}

/// <summary>
/// Input rules for orders (spec 009, R1-R9, R36). No I/O. Every invalid field and line is reported together;
/// errors about a line are keyed by its position in the request, <c>lines[0].unitPrice</c>.
/// </summary>
public static class OrderValidation
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string OrderDateField = "orderDate";
    private const string WarehouseField = "warehouseId";
    private const string QuantityField = "quantity";
    private const string UnitPriceField = "unitPrice";

    private const string StatusMessage =
        $"status must be \"{OrderStatusNames.Draft}\", \"{OrderStatusNames.Confirmed}\" or \"{OrderStatusNames.Closed}\".";

    private static string LineKey(int index, string field) => StockDocumentValidation.LineKey(index, field);

    /// <param name="dueDateGiven">False when a replace omitted the due date, which it may not (R4).</param>
    /// <param name="referenceGiven">False when a replace omitted the reference.</param>
    /// <param name="noteGiven">False when a replace omitted the note.</param>
    /// <param name="warehouseOptional">
    /// True for a create (spec 011, R8): <c>warehouseId</c> may be omitted or null, and the result then says
    /// <c>WarehouseOmitted</c> with an empty <c>WarehouseId</c> for the operation to resolve. A replace names it (R9).
    /// </param>
    public static Result<OrderValues> Values(
        OrderKind kind, string? orderDate, string? dueDate, string? partnerId, string? warehouseId, string? reference, string? note,
        IReadOnlyList<OrderLineInput?>? lines, bool dueDateGiven = true, bool referenceGiven = true, bool noteGiven = true,
        bool warehouseOptional = false)
    {
        var errors = new ValidationErrors();

        // R3: exactly YYYY-MM-DD and a real calendar date.
        if (!TryDate(orderDate, out var date))
            errors.Add(OrderDateField, string.IsNullOrEmpty(orderDate)
                ? "orderDate is required, as YYYY-MM-DD."
                : "orderDate must be a calendar date written as YYYY-MM-DD, for example 2026-10-09.");
        DateOnly? due = null;
        if (!dueDateGiven)
            errors.Add(kind.DueDateField, $"{kind.DueDateField} is required (it may be null).");
        else if (dueDate is not null)
        {
            if (!TryDate(dueDate, out var parsed))
                errors.Add(kind.DueDateField, $"{kind.DueDateField} must be a calendar date written as YYYY-MM-DD, or null.");
            else if (!errors.Has(OrderDateField) && parsed < date)
                errors.Add(kind.DueDateField, $"{kind.DueDateField} must not be earlier than orderDate.");
            else
                due = parsed;
        }

        var partner = RequiredId(errors, partnerId, kind.PartnerField, "a partner");
        var warehouse = DefaultWarehouseRules.Named(errors, warehouseId, warehouseOptional);

        // R4: as on a stock document (spec 005, R7).
        string? normalizedReference = null;
        if (!referenceGiven)
            errors.Add("reference", "reference is required (it may be null).");
        else if (!OptionalTextRules.TryNormalize(reference, StockDocument.ReferenceMaxLength, out normalizedReference))
            errors.Add("reference", $"reference must be at most {StockDocument.ReferenceMaxLength} characters on a single line, without control characters.");
        string? normalizedNote = null;
        if (!noteGiven)
            errors.Add("note", "note is required (it may be null).");
        else if (!NoteRules.TryNormalize(note, StockDocument.NoteMaxLength, out normalizedNote))
            errors.Add("note", $"note must be at most {StockDocument.NoteMaxLength} characters and contain no control characters other than line breaks and tabs.");

        var lineValues = new List<OrderLineRequest>();
        if (lines is null || lines.Count == 0)
            errors.Add("lines", $"lines is required: an array of 1 to {StockDocument.MaxLines} lines.");
        else if (lines.Count > StockDocument.MaxLines)
            errors.Add("lines", $"An order has at most {StockDocument.MaxLines} lines; split it into several orders.");
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i] is not { } line)
                {
                    errors.Add($"lines[{i}]", "A line must be an object with articleId, quantity and unitPrice.");
                    continue;
                }
                var article = RequiredId(errors, line.ArticleId, LineKey(i, "articleId"), "an article", "articleId");
                Guid? unit = null;
                if (line.UnitId is not null)
                {
                    if (Guid.TryParse(line.UnitId, out var parsed)) unit = parsed;
                    else errors.Add(LineKey(i, StockDocumentValidation.UnitField),
                        "unitId must be the id (UUID) of a unit of measure, not its code; leave it out or pass null for the article's base unit.");
                }
                if (line.Quantity is not { } quantity)
                    errors.Add(LineKey(i, QuantityField), "quantity is required.");
                else if (!QuantityRules.IsValid(quantity))
                    errors.Add(LineKey(i, QuantityField),
                        $"quantity must be greater than 0, at most {Text(QuantityRules.Max)}, with at most {QuantityRules.DecimalPlaces} decimal places.");
                // R8: zero is a price - goods free of charge.
                if (line.UnitPrice is not { } unitPrice)
                    errors.Add(LineKey(i, UnitPriceField), "unitPrice is required: the price of one unit of the line, 0 for goods free of charge.");
                else if (!PriceRules.IsValid(unitPrice))
                    errors.Add(LineKey(i, UnitPriceField),
                        $"unitPrice must be 0 or greater, at most {Text(PriceRules.Max)}, with at most {PriceRules.DecimalPlaces} decimal places.");

                if (errors.Has(LineKey(i, QuantityField)) || errors.Has(LineKey(i, UnitPriceField)))
                    continue;
                // R9: the maximum is compared with the rounded amount, and both factors of it are named.
                if (!OrderAmounts.IsValidLine(line.Quantity!.Value, line.UnitPrice!.Value))
                {
                    var message = $"quantity × unitPrice is {Text(OrderAmounts.LineAmount(line.Quantity.Value, line.UnitPrice.Value))}; "
                        + $"the amount of a line is at most {Text(OrderAmounts.MaxLineAmount)}.";
                    errors.Add(LineKey(i, QuantityField), message);
                    errors.Add(LineKey(i, UnitPriceField), message);
                }
                else
                    lineValues.Add(new OrderLineRequest(article, line.Quantity.Value, line.UnitPrice.Value, unit));
            }
        }

        if (errors.Any)
            return errors.ToError();
        return new OrderValues(date, due, partner, warehouse ?? default, normalizedReference, normalizedNote, lineValues, WarehouseOmitted: warehouse is null);
    }

    public static Result<OrderListQuery> List(
        OrderKind kind, string? status, string? fulfilment, string? partnerId, string? warehouseId, string? search, int? limit, int? offset)
    {
        var errors = new ValidationErrors();
        OrderStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (OrderStatusNames.TryParse(status, out var value)) parsedStatus = value;
            else errors.Add("status", StatusMessage);
        }
        FulfilmentStatus? parsedFulfilment = null;
        if (!string.IsNullOrEmpty(fulfilment))
        {
            if (FulfilmentStatusNames.TryParse(fulfilment, out var value)) parsedFulfilment = value;
            else errors.Add(kind.FulfilmentField,
                $"{kind.FulfilmentField} must be \"{FulfilmentStatusNames.None}\", \"{FulfilmentStatusNames.Partial}\" or \"{FulfilmentStatusNames.Full}\".");
        }
        var partner = OptionalId(errors, partnerId, kind.PartnerField);
        var warehouse = OptionalId(errors, warehouseId, WarehouseField);
        var text = ListRules.Search(errors, search);
        var pageSize = ListRules.Limit(errors, limit);
        var skip = ListRules.Offset(errors, offset);
        if (errors.Any)
            return errors.ToError();
        return new OrderListQuery(parsedStatus, parsedFulfilment, partner, warehouse, text, pageSize, skip);
    }

    private static bool TryDate(string? input, out DateOnly date) =>
        DateOnly.TryParseExact(input, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Text(decimal value) => QuantityRules.Normalize(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>Only the form is checked here; whether the record exists is a later stage (R10).</summary>
    private static Guid RequiredId(ValidationErrors errors, string? input, string key, string what, string? field = null)
    {
        if (Guid.TryParse(input, out var id))
            return id;
        field ??= key;
        errors.Add(key, string.IsNullOrEmpty(input) ? $"{field} is required." : $"{field} must be the id (UUID) of {what}, not its code.");
        return default;
    }

    private static Guid? OptionalId(ValidationErrors errors, string? input, string field)
    {
        if (string.IsNullOrEmpty(input))
            return null;
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add(field, $"{field} must be a UUID.");
        return null;
    }
}
