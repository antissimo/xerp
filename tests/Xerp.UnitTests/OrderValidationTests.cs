using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Stock;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;
using Xerp.Domain.Rules;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 009: the input rules of an order (R3, R4, R8, R9), the form of the link on a stock document (R18-R20)
/// and the decisions the link adds to saving and posting (R21, R24, R26) - without HTTP and without a database.
/// </summary>
public class OrderValidationTests
{
    private static readonly string Supplier = Guid.CreateVersion7().ToString();
    private static readonly string Warehouse = Guid.CreateVersion7().ToString();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();

    private static OrderLineInput Line(decimal? quantity = 1m, decimal? unitPrice = 1m) => new(A.ToString(), quantity, unitPrice);

    private static Result<OrderValues> Values(string? expectedDate = null, params OrderLineInput[] lines) =>
        OrderValidation.Values(OrderKind.Purchase, true, "2026-10-09", expectedDate, Supplier, Warehouse, null, null, lines.Length == 0 ? [Line()] : lines);

    private static void AssertKeys<T>(Result<T> result, string code, params string[] keys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        AssertKeys(result.Error, code, keys);
    }

    private static void AssertKeys(AppError? error, string code, params string[] keys)
    {
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(keys.Order(StringComparer.Ordinal), error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void R3_The_expected_date_is_optional_and_not_earlier_than_the_order_date()
    {
        Assert.Null(Values().Value!.DueDate);
        Assert.Equal(new DateOnly(2026, 10, 9), Values("2026-10-09").Value!.DueDate);
        AssertKeys(Values("2026-10-08"), ErrorCodes.ValidationFailed, "expectedDate");
        AssertKeys(Values("2026-02-30"), ErrorCodes.ValidationFailed, "expectedDate");
        AssertKeys(
            OrderValidation.Values(OrderKind.Purchase, true, "2026-02-30", "2026-10-08", "abc", null, null, null, [Line()]),
            ErrorCodes.ValidationFailed, "orderDate", "supplierId", "warehouseId");
    }

    [Fact]
    public void R4_A_replace_must_give_expected_date_reference_and_note_though_each_may_be_null()
    {
        var omitted = OrderValidation.Values(
            OrderKind.Purchase, true, "2026-10-09", null, Supplier, Warehouse, null, null, [Line()],
            dueDateGiven: false, referenceGiven: false, noteGiven: false);
        AssertKeys(omitted, ErrorCodes.ValidationFailed, "expectedDate", "reference", "note");
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.000001", true)]
    [InlineData("999999999.999999", true)]
    [InlineData("-0.01", false)]
    [InlineData("1.0000001", false)]
    [InlineData("1000000000", false)]
    public void R8_E1_The_unit_price_is_zero_or_more_with_at_most_six_decimals(string price, bool accepted)
    {
        var result = Values(null, Line(1m, decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)));
        if (accepted)
            Assert.True(result.IsSuccess);
        else
            AssertKeys(result, ErrorCodes.ValidationFailed, "lines[0].unitPrice");
    }

    [Fact]
    public void R8_A_missing_unit_price_and_a_missing_quantity_are_reported_together_on_their_line()
    {
        AssertKeys(Values(null, Line(), Line(null, null)), ErrorCodes.ValidationFailed, "lines[1].quantity", "lines[1].unitPrice");
        AssertKeys(Values(null, Line(0m)), ErrorCodes.ValidationFailed, "lines[0].quantity");
    }

    [Fact]
    public void R9_A_line_amount_above_the_maximum_names_quantity_and_unit_price()
    {
        AssertKeys(Values(null, Line(100000m, 100000m)), ErrorCodes.ValidationFailed, "lines[0].quantity", "lines[0].unitPrice");
        Assert.True(Values(null, Line(99999.99m, 100000m)).IsSuccess);
        // The maximum is compared with the rounded amount: 99999999999.995 rounds up and is refused,
        // 9999999999.994 rounds down to the maximum and is accepted.
        AssertKeys(Values(null, Line(10m, 999999999.9995m)), ErrorCodes.ValidationFailed, "lines[0].quantity", "lines[0].unitPrice");
        Assert.True(Values(null, Line(10m, 999999999.9994m)).IsSuccess);
    }

    [Fact]
    public void R36_The_list_filters_take_the_names_of_the_two_statuses_only()
    {
        var query = OrderValidation.List(OrderKind.Purchase, "confirmed", "partial", null, null, null, null, null).Value!;
        Assert.Equal((OrderStatus.Confirmed, FulfilmentStatus.Partial), (query.Status, query.Fulfilment));
        AssertKeys(
            OrderValidation.List(OrderKind.Purchase, "posted", "all", "x", "y", null, 0, -1),
            ErrorCodes.ValidationFailed, "status", "receiptStatus", "supplierId", "warehouseId", "limit", "offset");
    }

    // ---- the link on a stock document

    private static CreateStockDocumentInput Document(string type, string? purchaseOrderId, int? orderLineNo) =>
        new(type, "2026-10-10", Warehouse, [new StockLineInput(A.ToString(), 1m, null, orderLineNo)],
            ToWarehouseId: type == "transfer" ? Guid.CreateVersion7().ToString() : null, PurchaseOrderId: purchaseOrderId);

    [Fact]
    public void R18_Only_a_receipt_carries_the_link_and_the_type_decides_about_that_key_only()
    {
        var order = Guid.CreateVersion7();
        Assert.Equal(new OrderLink(OrderSide.Purchase, order), StockDocumentValidation.Create(Document("receipt", order.ToString(), 1)).Value!.Link);
        Assert.Null(StockDocumentValidation.Create(Document("receipt", null, null)).Value!.Link);
        // A document that sends the link is judged as linked: its valid orderLineNo is not reported with it.
        foreach (var type in new[] { "issue", "transfer", "count" })
            AssertKeys(StockDocumentValidation.Create(Document(type, order.ToString(), 1)), ErrorCodes.ValidationFailed, "purchaseOrderId");
        AssertKeys(StockDocumentValidation.Create(Document("receipt", "abc", 1)), ErrorCodes.ValidationFailed, "purchaseOrderId");
    }

    [Fact]
    public void R20_A_linked_line_names_an_order_line_and_an_unlinked_line_names_none()
    {
        var order = Guid.CreateVersion7().ToString();
        AssertKeys(StockDocumentValidation.Create(Document("receipt", order, null)), ErrorCodes.ValidationFailed, "lines[0].orderLineNo");
        AssertKeys(StockDocumentValidation.Create(Document("receipt", order, 0)), ErrorCodes.ValidationFailed, "lines[0].orderLineNo");
        AssertKeys(StockDocumentValidation.Create(Document("receipt", null, 1)), ErrorCodes.ValidationFailed, "lines[0].orderLineNo");

        // A replace has no link in its body: the stored document decides.
        var values = new StockDocumentValues(new DateOnly(2026, 10, 10), Guid.CreateVersion7(), null, null, null,
            [new StockLineRequest(A, 1m, null, 1), new StockLineRequest(A, 1m)]);
        AssertKeys(StockDocumentValidation.OfType(StockDocumentType.Receipt, values, linked: true), ErrorCodes.ValidationFailed, "lines[1].orderLineNo");
        AssertKeys(StockDocumentValidation.OfType(StockDocumentType.Receipt, values, linked: false), ErrorCodes.ValidationFailed, "lines[0].orderLineNo");
    }

    // ---- what the link adds to saving and posting

    private static readonly LinkedOrderFacts Order =
        new(Guid.CreateVersion7(), OrderStatus.Confirmed, Guid.Parse(Warehouse), new Dictionary<int, Guid> { [1] = A, [2] = B }, Guid.CreateVersion7());

    [Fact]
    public void R21_A_document_is_saved_only_against_an_existing_confirmed_order()
    {
        var field = OrderKind.Purchase.LinkField;
        Assert.Null(OrderLinkChecks.Open(field, Order.Id, Order));
        AssertKeys(OrderLinkChecks.Open(field, Order.Id, null), ErrorCodes.ReferenceNotFound, field);
        AssertKeys(OrderLinkChecks.Open(field, Order.Id, Order with { Status = OrderStatus.Draft }), ErrorCodes.OrderNotOpen, field);
        AssertKeys(OrderLinkChecks.Open(field, Order.Id, Order with { Status = OrderStatus.Closed }), ErrorCodes.OrderNotOpen, field);
        AssertKeys(OrderLinkChecks.Open(field, OrderStatus.Closed), ErrorCodes.OrderNotOpen, field);
    }

    [Fact]
    public void R21_An_order_line_the_order_does_not_have_is_an_unknown_reference_reported_before_any_mismatch()
    {
        var facts = new StockLineFacts(
            new Dictionary<Guid, ArticleFacts> { [A] = new(true, ArticleType.Stock, Pcs), [B] = new(true, ArticleType.Stock, Pcs) },
            new Dictionary<Guid, bool>(), new Dictionary<(Guid, Guid), decimal>());
        var result = StockLineChecks.References(
            [new(B, 1m, null, 1), new(A, 1m, null, 99)], facts, new HashSet<Guid>(), new HashSet<Guid>(), StockDocumentType.Receipt, [1, 2]);
        AssertKeys(result, ErrorCodes.ReferenceNotFound, "lines[1].orderLineNo");
    }

    [Fact]
    public void R21_Disagreement_with_the_order_names_every_applicable_key()
    {
        var warehouse = Guid.Parse(Warehouse);
        Assert.Null(OrderLinkChecks.Agreement(warehouse, [new(A, Pcs, 1m, 1), new(B, Pcs, 1m, 2), new(A, Pcs, 2m, 1)], Order));
        AssertKeys(OrderLinkChecks.Agreement(Guid.CreateVersion7(), [new(A, Pcs, 1m, 1)], Order), ErrorCodes.OrderMismatch, "warehouseId");
        AssertKeys(OrderLinkChecks.Agreement(warehouse, [new(A, Pcs, 1m, 1), new(A, Pcs, 1m, 2)], Order), ErrorCodes.OrderMismatch, "lines[1].articleId");
        AssertKeys(OrderLinkChecks.Agreement(Guid.CreateVersion7(), [new(B, Pcs, 1m, 1)], Order), ErrorCodes.OrderMismatch, "warehouseId", "lines[0].articleId");
    }

    [Fact]
    public void R26_E11_E12_Every_document_line_naming_an_exceeded_order_line_is_reported_and_no_other()
    {
        var outstanding = new Dictionary<int, decimal> { [1] = 10m, [2] = 5m };
        Assert.Null(OrderLinkChecks.WithinOrder([new(1, 6m), new(1, 4m), new(2, 5m)], outstanding, RuleRegistry.OverReceiptAllowed, overFulfilmentAllowed: false));
        AssertKeys(OrderLinkChecks.WithinOrder([new(1, 6m), new(1, 5m)], outstanding, RuleRegistry.OverReceiptAllowed, overFulfilmentAllowed: false), ErrorCodes.QuantityExceedsOrder, "lines[0].quantity", "lines[1].quantity");
        AssertKeys(OrderLinkChecks.WithinOrder([new(1, 3m), new(2, 5.000001m), new(1, 2m)], outstanding, RuleRegistry.OverReceiptAllowed, overFulfilmentAllowed: false), ErrorCodes.QuantityExceedsOrder, "lines[1].quantity");
    }

    [Fact]
    public void R12_Confirmation_reports_the_inactive_header_masters_together_and_alone_then_the_lines()
    {
        var articles = new Dictionary<Guid, ArticleFacts> { [A] = new(false, ArticleType.Stock, Pcs), [B] = new(true, ArticleType.Stock, Pcs) };
        IReadOnlyList<StockLineEntry> lines = [new(B, Pcs, 1m), new(A, Pcs, 1m)];
        var supplier = Guid.Parse(Supplier);
        AssertKeys(
            StockLineChecks.ActiveMasters([("supplierId", supplier), ("warehouseId", Guid.Parse(Warehouse))], lines, articles, "x"),
            ErrorCodes.ReferenceInactive, "supplierId", "warehouseId");
        AssertKeys(StockLineChecks.ActiveMasters([], lines, articles, "x"), ErrorCodes.ReferenceInactive, "lines[1].articleId");
        Assert.Null(StockLineChecks.ActiveMasters([], [new(B, Pcs, 1m)], articles, "x"));
    }
}
