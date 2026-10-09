using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Stock;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 010, AC-03: a sales order runs the amount, progress and status rules of a purchase order - one
/// implementation, two users; the reserved / available rule (R15, R16) without HTTP; and the second link of a
/// stock document (R5, R6, E11) as the Domain and the input rules enforce it.
/// </summary>
public class SalesOrderRulesTests
{
    private static readonly DateOnly Date = new(2026, 10, 9);
    private static readonly DateTime Now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid Pcs = Guid.NewGuid();
    private static readonly Guid Box = Guid.NewGuid();

    private static SalesOrder Confirmed(params (decimal Quantity, decimal Factor, decimal Price)[] lines)
    {
        var order = SalesOrder.Create(
            Date, null, Customer, Warehouse, null, null,
            lines.Select(l => new OrderLineEntry(A, l.Factor == 1 ? Pcs : Box, l.Quantity, l.Price)).ToArray(), Now, Actor);
        order.Confirm("SO-000001", lines.Select(l => l.Factor).ToList(), Now, Actor);
        return order;
    }

    // ---- one implementation, two users

    [Fact]
    public void AC03_Both_kinds_of_order_are_the_same_order_and_the_same_operations()
    {
        Assert.Equal(typeof(Order<>), typeof(SalesOrder).BaseType!.GetGenericTypeDefinition());
        Assert.Equal(typeof(Order<>), typeof(PurchaseOrder).BaseType!.GetGenericTypeDefinition());
        Assert.Equal(typeof(OrderLine), typeof(SalesOrderLine).BaseType);

        var operations = typeof(OrderOperations<,,,>);
        Assert.Equal(operations, typeof(SalesOrderOperations).BaseType!.GetGenericTypeDefinition());
        Assert.Equal(operations, typeof(PurchaseOrderOperations).BaseType!.GetGenericTypeDefinition());
        // A kind adds names, not behaviour: what it declares itself are the three operations whose input it names.
        foreach (var kind in new[] { typeof(SalesOrderOperations), typeof(PurchaseOrderOperations) })
            Assert.Equal(
                ["CreateAsync", "ListAsync", "ReplaceAsync"],
                kind.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                    .Select(m => m.Name).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void R1_R8_A_sales_order_has_the_amounts_the_progress_and_the_lifecycle_of_any_order()
    {
        // 009/R9: 40 × 4.5 = 180; 5 boxes of 12 at 60 = 300.
        var order = Confirmed((40m, 1m, 4.5m), (5m, 12m, 60m));
        Assert.Equal([180m, 300m], order.Lines.Select(l => l.LineAmount));
        Assert.Equal("SO-000001", order.Number);
        Assert.Equal([40m, 60m], order.Lines.Select(l => l.BaseQuantity!.Value));
        Assert.Equal(FulfilmentStatus.None, order.FulfilmentStatus);

        // 009/R25-R27 mirrored: delivered rises with a posted delivery, never above what was ordered.
        order.Fulfil([new LineFulfilment(1, 25m)]);
        Assert.Equal(FulfilmentStatus.Partial, order.FulfilmentStatus);
        Assert.Equal(15m, order.Outstanding[1]);
        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(1, 16m)]));
        Assert.Equal(25m, order.Lines[0].FulfilledBaseQuantity);

        // 009/R15, R16 mirrored: closed, nothing is outstanding; reopened, the rest is again.
        order.Close(Now, Actor);
        Assert.Equal(0m, order.Outstanding[1]);
        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(1, 1m)]));
        // R14: a delivery is reversed whatever the order's status.
        order.TakeBack([new LineFulfilment(1, 25m)]);
        Assert.Equal(OrderStatus.Closed, order.Status);
        order.Reopen();
        Assert.Equal(40m, order.Outstanding[1]);

        order.Fulfil([new LineFulfilment(1, 40m), new LineFulfilment(2, 60m)]);
        Assert.Equal(FulfilmentStatus.Full, order.FulfilmentStatus);
    }

    [Fact]
    public void R1_The_sales_series_is_SO_and_a_sales_order_is_fulfilled_by_an_issue()
    {
        Assert.Equal("SO-000001", DocumentNumber.Format(DocumentSeries.SalesOrder, 1));
        Assert.Equal("salesOrder", DocumentCounter.Start(Guid.NewGuid(), DocumentSeries.SalesOrder).DocumentType);
        Assert.Equal(StockDocumentType.Issue, OrderSide.Sales.FulfilledBy());
        Assert.Equal(StockDocumentType.Receipt, OrderSide.Purchase.FulfilledBy());
        Assert.Equal(("salesOrderId", "customerId", "isCustomer", "requestedDate", "deliveryStatus"),
            (OrderKind.Sales.LinkField, OrderKind.Sales.PartnerField, OrderKind.Sales.PartnerRole, OrderKind.Sales.DueDateField, OrderKind.Sales.FulfilmentField));
        Assert.Same(OrderKind.Sales, OrderKind.Of(OrderSide.Sales));
        Assert.Same(OrderKind.Purchase, OrderKind.Of(OrderSide.Purchase));
    }

    // ---- reserved and available (R15, R16, R18)

    [Fact]
    public void R15_Reserved_is_what_confirmed_orders_still_owe_and_drafts_and_closed_orders_reserve_nothing()
    {
        Assert.Equal(0m, StockAvailability.Reserved([]));
        Assert.Equal(60m, StockAvailability.Reserved([(OrderStatus.Confirmed, 30m, 0m), (OrderStatus.Confirmed, 24m, 0m), (OrderStatus.Confirmed, 6m, 0m)]));
        // What was delivered is no longer reserved.
        Assert.Equal(18m, StockAvailability.Reserved([(OrderStatus.Confirmed, 30m, 12m)]));
        Assert.Equal(0m, StockAvailability.Reserved([(OrderStatus.Confirmed, 30m, 30m)]));
        // A draft has no base quantity yet; a closed order owes nothing, whatever was delivered.
        Assert.Equal(0m, StockAvailability.Reserved([(OrderStatus.Draft, null, 0m), (OrderStatus.Closed, 10m, 4m)]));
        Assert.Equal(30m, StockAvailability.Reserved([(OrderStatus.Draft, null, 0m), (OrderStatus.Closed, 10m, 4m), (OrderStatus.Confirmed, 30m, 0m)]));
    }

    [Theory]
    [InlineData("100", "30", "70")]
    [InlineData("10", "10", "0")]
    [InlineData("0", "10", "-10")] // E2: more is promised than is there
    [InlineData("5", "300", "-295")]
    [InlineData("20", "0", "20")]
    public void R16_Available_is_on_hand_minus_reserved_and_may_be_negative(string onHand, string reserved, string expected)
    {
        Assert.Equal(decimal.Parse(expected), StockAvailability.Available(decimal.Parse(onHand), decimal.Parse(reserved)));
    }

    [Fact]
    public void R18_A_delivery_lowers_stock_and_reservation_together_so_available_does_not_move()
    {
        var order = Confirmed((30m, 1m, 1m));
        decimal Reserved() => StockAvailability.Reserved(order.Lines.Select(l => (order.Status, l.BaseQuantity, l.FulfilledBaseQuantity)));

        var onHand = 100m;
        Assert.Equal(70m, StockAvailability.Available(onHand, Reserved()));
        order.Fulfil([new LineFulfilment(1, 12m)]);
        onHand -= 12m;
        Assert.Equal((88m, 18m, 70m), (onHand, Reserved(), StockAvailability.Available(onHand, Reserved())));
        // Closing frees the rest; reopening reserves it again.
        order.Close(Now, Actor);
        Assert.Equal(0m, Reserved());
        order.Reopen();
        Assert.Equal(18m, Reserved());
    }

    // ---- the link of a delivery (R5, R6)

    private static StockDocument Document(StockDocumentType type, Guid? purchaseOrderId, Guid? salesOrderId) =>
        StockDocument.Create(
            type, Date, Warehouse, type == StockDocumentType.Transfer ? Guid.NewGuid() : null, null, null,
            [new StockLineEntry(A, Pcs, 4m, 1)], Now, Actor, type == StockDocumentType.Count ? [0m] : null, purchaseOrderId, salesOrderId);

    [Fact]
    public void R5_R6_Only_an_issue_carries_the_sales_link_and_a_document_has_at_most_one_link()
    {
        var order = Guid.NewGuid();
        var delivery = Document(StockDocumentType.Issue, null, order);
        Assert.Equal(new OrderLink(OrderSide.Sales, order), delivery.Link);
        Assert.True(delivery.IsLinked);
        Assert.Null(delivery.PurchaseOrderId);
        Assert.All(delivery.Lines, l => Assert.Equal((order, (Guid?)null, 1), (l.SalesOrderId!.Value, l.PurchaseOrderId, l.OrderLineNo!.Value)));

        foreach (var type in new[] { StockDocumentType.Receipt, StockDocumentType.Transfer, StockDocumentType.Count })
            Assert.Throws<ArgumentException>(() => Document(type, null, order));
        // Both links: one of them is on the wrong type, whatever the type is.
        foreach (var type in Enum.GetValues<StockDocumentType>())
            Assert.Throws<ArgumentException>(() => Document(type, Guid.NewGuid(), order));
        // A receipt keeps its own link, and its lines do not get the other one.
        var receipt = Document(StockDocumentType.Receipt, order, null);
        Assert.Equal(new OrderLink(OrderSide.Purchase, order), receipt.Link);
        Assert.All(receipt.Lines, l => Assert.Null(l.SalesOrderId));
    }

    [Fact]
    public void R14_The_reversal_of_a_delivery_carries_the_order_and_the_order_lines()
    {
        var order = Guid.NewGuid();
        var delivery = Document(StockDocumentType.Issue, null, order);
        var entries = delivery.Post("SI-000001", [1m], Now, Actor);
        Assert.Equal([new LineFulfilment(1, 4m)], delivery.Fulfilment);

        var (reversal, reversing) = delivery.Reverse(entries, Date, null, "SI-000002", Now, Actor);
        Assert.Equal(order, reversal.SalesOrderId);
        Assert.Equal([(order, 1)], reversal.Lines.Select(l => (l.SalesOrderId!.Value, l.OrderLineNo!.Value)));
        // R11, R14: the delivery took 4 out; its reversal brings 4 back.
        Assert.Equal([-4m], entries.Select(e => e.Quantity));
        Assert.Equal([4m], reversing.Select(e => e.Quantity));
    }

    // ---- the link in a create request (R5, R6, E11)

    private static string[] LinkErrors(string type, bool purchase, bool sales)
    {
        var input = new CreateStockDocumentInput(
            type, "2026-10-10", Warehouse.ToString(), [new StockLineInput(A.ToString(), 1m, null, 1)],
            ToWarehouseId: type == "transfer" ? Guid.NewGuid().ToString() : null,
            PurchaseOrderId: purchase ? Guid.NewGuid().ToString() : null, SalesOrderId: sales ? Guid.NewGuid().ToString() : null);
        var result = StockDocumentValidation.Create(input);
        if (result.IsSuccess)
            return [];
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error.Code);
        return result.Error.Errors!.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void R5_Only_an_issue_may_send_salesOrderId_and_the_type_decides_about_that_key_only()
    {
        Assert.Empty(LinkErrors("issue", purchase: false, sales: true));
        // A valid orderLineNo is not reported with the link (009/R18 mirrored).
        foreach (var type in new[] { "receipt", "transfer", "count" })
            Assert.Equal(["salesOrderId"], LinkErrors(type, purchase: false, sales: true));

        var malformed = StockDocumentValidation.Create(new CreateStockDocumentInput(
            "issue", "2026-10-10", Warehouse.ToString(), [new StockLineInput(A.ToString(), 1m, null, 1)], SalesOrderId: "SO-000001"));
        Assert.Equal(["salesOrderId"], malformed.Error!.Errors!.Keys);
    }

    [Fact]
    public void E11_With_both_links_errors_has_the_key_of_every_link_the_type_cannot_carry()
    {
        Assert.Equal(["purchaseOrderId"], LinkErrors("issue", purchase: true, sales: true));
        Assert.Equal(["salesOrderId"], LinkErrors("receipt", purchase: true, sales: true));
        Assert.Equal(["purchaseOrderId", "salesOrderId"], LinkErrors("transfer", purchase: true, sales: true));
        Assert.Equal(["purchaseOrderId", "salesOrderId"], LinkErrors("count", purchase: true, sales: true));
    }

    [Fact]
    public void R5_The_validated_link_names_the_kind_of_order()
    {
        var order = Guid.NewGuid();
        var created = StockDocumentValidation.Create(new CreateStockDocumentInput(
            "issue", "2026-10-10", Warehouse.ToString(), [new StockLineInput(A.ToString(), 1m, null, 1)], SalesOrderId: order.ToString()));
        Assert.Equal(new OrderLink(OrderSide.Sales, order), created.Value!.Link);

        var listed = StockDocumentValidation.List(new ListStockDocumentsInput(SalesOrderId: order.ToString()));
        Assert.Equal((order, (Guid?)null), (listed.Value!.SalesOrderId!.Value, listed.Value.PurchaseOrderId));
        Assert.Equal(["salesOrderId"], StockDocumentValidation.List(new ListStockDocumentsInput(SalesOrderId: "x")).Error!.Errors!.Keys);
    }

    // ---- the input rules of the order, under a sales order's names (R1)

    [Fact]
    public void R1_Invalid_input_is_reported_under_the_names_of_a_sales_order()
    {
        var values = OrderValidation.Values(
            OrderKind.Sales, "2026-10-09", "2026-10-08", "CUS", Warehouse.ToString(), null, null, [new OrderLineInput(A.ToString(), 1m, 1m)]);
        Assert.Equal(["customerId", "requestedDate"], values.Error!.Errors!.Keys.Order(StringComparer.Ordinal));

        var list = OrderValidation.List(OrderKind.Sales, null, "all", "x", null, null, null, null);
        Assert.Equal(["customerId", "deliveryStatus"], list.Error!.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    // ---- posting: the order is asked before stock (R7, E3)

    [Fact]
    public void R7_E3_A_delivery_above_the_order_and_above_stock_is_refused_for_the_order()
    {
        var order = Confirmed((5m, 1m, 1m));
        var fulfilment = new[] { new LineFulfilment(1, 20m) };
        var exceeds = OrderLinkChecks.WithinOrder(fulfilment, order.Outstanding);
        Assert.Equal(ErrorCodes.QuantityExceedsOrder, exceeds!.Code);
        Assert.Equal(["lines[0].quantity"], exceeds.Errors!.Keys);
        // Within the order, stock decides alone - what is reserved plays no part (R9).
        Assert.Null(OrderLinkChecks.WithinOrder([new LineFulfilment(1, 5m)], order.Outstanding));
        var short_ = StockLineChecks.Sufficiency([new StockLineValues(A, 5m)], new Dictionary<Guid, decimal> { [A] = 4m });
        Assert.Equal(ErrorCodes.InsufficientStock, short_!.Code);
        Assert.Null(StockLineChecks.Sufficiency([new StockLineValues(A, 5m)], new Dictionary<Guid, decimal> { [A] = 5m }));

        Assert.Equal(["salesOrderId"], OrderLinkChecks.Open(OrderKind.Sales.LinkField, OrderStatus.Closed)!.Errors!.Keys);
    }
}
