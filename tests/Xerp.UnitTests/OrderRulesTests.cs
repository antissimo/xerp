using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 009, AC-03: the amount rule (R9, E2), the received / outstanding rule (R25-R27) and the receipt status
/// (R29), without HTTP; and the order lifecycle (R11-R16) and the link of a stock document (R18-R20, R32) as
/// the Domain enforces them.
/// </summary>
public class OrderRulesTests
{
    private static readonly DateOnly Date = new(2026, 10, 9);
    private static readonly DateTime Now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Supplier = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid Pcs = Guid.NewGuid();
    private static readonly Guid Box = Guid.NewGuid();

    private static PurchaseOrder Draft(params OrderLineEntry[] lines) =>
        PurchaseOrder.Create(Date, null, Supplier, Warehouse, null, null, lines, Now, Actor);

    private static PurchaseOrder Confirmed(params (Guid Article, decimal Quantity, decimal Factor)[] lines)
    {
        var order = Draft(lines.Select(l => new OrderLineEntry(l.Article, l.Factor == 1 ? Pcs : Box, l.Quantity, 1m)).ToArray());
        order.Confirm("PO-000001", lines.Select(l => l.Factor).ToList(), Now, Actor);
        return order;
    }

    // ---- amounts (R8, R9, E1, E2)

    [Theory]
    [InlineData("2.5", "0.333333", "0.83")]
    [InlineData("0.5", "0.01", "0.01")]
    [InlineData("0.5", "0.03", "0.02")]
    [InlineData("1", "0.004", "0")]
    [InlineData("1", "0.005", "0.01")]
    [InlineData("3", "0.115", "0.35")]
    [InlineData("5", "30", "150")]
    [InlineData("1", "0", "0")]
    public void R9_E2_Line_amount_is_quantity_times_price_rounded_to_two_places_half_away_from_zero(string quantity, string price, string expected)
    {
        Assert.Equal(decimal.Parse(expected), OrderAmounts.LineAmount(decimal.Parse(quantity), decimal.Parse(price)));
    }

    [Theory]
    [InlineData("99999.99", "100000", true)]
    [InlineData("100000", "100000", false)]
    [InlineData("999999999.999", "10", true)]
    // Below the maximum before rounding, above it after: compared with the rounded amount.
    [InlineData("999999999.9995", "10", false)]
    [InlineData("999999999.9994", "10", true)]
    public void R9_The_maximum_is_compared_with_the_rounded_line_amount(string quantity, string price, bool valid)
    {
        Assert.Equal(valid, OrderAmounts.IsValidLine(decimal.Parse(quantity), decimal.Parse(price)));
    }

    [Fact]
    public void R9_Total_is_the_sum_of_the_rounded_line_amounts()
    {
        // Three lines of 0.5 × 0.01 are 0.01 each: 0.03, not 0.015 rounded.
        var amounts = Enumerable.Repeat(OrderAmounts.LineAmount(0.5m, 0.01m), 3);

        Assert.Equal(0.03m, OrderAmounts.Total(amounts));
    }

    [Fact]
    public void R9_Amounts_do_not_follow_the_factor_or_what_was_received()
    {
        var order = Draft(new OrderLineEntry(A, Box, 5m, 30m));
        Assert.Equal(150m, order.Lines[0].LineAmount);

        order.Confirm("PO-000001", [10m], Now, Actor);
        order.Fulfil([new LineFulfilment(1, 20m)]);

        Assert.Equal(150m, order.Lines[0].LineAmount);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.000001", true)]
    [InlineData("999999999.999999", true)]
    [InlineData("-0.01", false)]
    [InlineData("1.0000001", false)]
    [InlineData("1000000000", false)]
    public void R8_E1_Unit_price_is_zero_or_more_with_six_decimal_places(string price, bool valid)
    {
        Assert.Equal(valid, PriceRules.IsValid(decimal.Parse(price)));
    }

    // ---- received and outstanding (R25-R27)

    [Fact]
    public void R27_Outstanding_is_ordered_minus_received_while_confirmed_and_zero_otherwise()
    {
        Assert.Equal(6m, OrderProgress.Outstanding(OrderStatus.Confirmed, 10m, 4m));
        Assert.Equal(0m, OrderProgress.Outstanding(OrderStatus.Confirmed, 10m, 10m));
        Assert.Equal(0m, OrderProgress.Outstanding(OrderStatus.Closed, 10m, 4m));
        Assert.Equal(0m, OrderProgress.Outstanding(OrderStatus.Draft, null, 0m));
    }

    [Fact]
    public void R25_Received_rises_by_the_base_quantities_of_a_posted_receipt_per_order_line()
    {
        var order = Confirmed((A, 10m, 1m), (B, 5m, 1m), (A, 5m, 12m));

        // Two lines for line 1; line 3 was ordered in boxes (60) and is received in base units.
        order.Fulfil([new LineFulfilment(1, 6m), new LineFulfilment(3, 24m), new LineFulfilment(1, 4m)]);

        Assert.Equal([10m, 0m, 24m], order.Lines.Select(l => l.FulfilledBaseQuantity));
        Assert.Equal(0m, order.Outstanding[1]);
        Assert.Equal(5m, order.Outstanding[2]);
        Assert.Equal(36m, order.Outstanding[3]);
    }

    [Fact]
    public void R26_E10_Exactly_the_outstanding_quantity_is_within_the_order_and_one_millionth_more_is_not()
    {
        var outstanding = new Dictionary<int, decimal> { [1] = 10m };

        Assert.Empty(OrderProgress.ExceedingLines([new LineFulfilment(1, 10m)], outstanding));
        Assert.Equal([0], OrderProgress.ExceedingLines([new LineFulfilment(1, 10.000001m)], outstanding));
    }

    [Fact]
    public void R26_E11_E12_Every_document_line_of_an_exceeded_order_line_is_named_and_no_other()
    {
        var outstanding = new Dictionary<int, decimal> { [1] = 10m, [2] = 5m, [3] = 3m };

        // Each of the two lines for line 1 is within, together they are above (E11).
        Assert.Equal([0, 1], OrderProgress.ExceedingLines([new LineFulfilment(1, 6m), new LineFulfilment(1, 5m)], outstanding));
        Assert.Empty(OrderProgress.ExceedingLines([new LineFulfilment(1, 6m), new LineFulfilment(1, 4m)], outstanding));
        // One line within and one above (E12): only the second.
        Assert.Equal([1], OrderProgress.ExceedingLines(
            [new LineFulfilment(3, 3m), new LineFulfilment(2, 6m), new LineFulfilment(1, 2m)], outstanding));
        // A line the order does not have has nothing outstanding.
        Assert.Equal([0], OrderProgress.ExceedingLines([new LineFulfilment(9, 1m)], outstanding));
    }

    [Fact]
    public void R26_The_order_refuses_a_receipt_above_what_is_outstanding_and_keeps_what_it_had()
    {
        var order = Confirmed((A, 10m, 1m), (B, 5m, 1m));
        order.Fulfil([new LineFulfilment(1, 4m)]);

        // Line 2 is within; nothing of the document counts.
        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(2, 5m), new LineFulfilment(1, 7m)]));

        Assert.Equal([4m, 0m], order.Lines.Select(l => l.FulfilledBaseQuantity));
    }

    [Fact]
    public void R33_A_reversed_receipt_no_longer_counts_and_the_quantity_can_be_received_again()
    {
        var order = Confirmed((A, 10m, 1m));
        order.Fulfil([new LineFulfilment(1, 10m)]);
        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(1, 0.000001m)]));

        order.TakeBack([new LineFulfilment(1, 10m)]);

        Assert.Equal(0m, order.Lines[0].FulfilledBaseQuantity);
        Assert.Equal(10m, order.Outstanding[1]);
        order.Fulfil([new LineFulfilment(1, 10m)]);
    }

    [Fact]
    public void R34_A_receipt_against_a_closed_order_can_be_taken_back_and_the_order_stays_closed()
    {
        var order = Confirmed((A, 10m, 1m));
        order.Fulfil([new LineFulfilment(1, 4m)]);
        order.Close(Now, Actor);

        order.TakeBack([new LineFulfilment(1, 4m)]);

        Assert.Equal(OrderStatus.Closed, order.Status);
        Assert.Equal(0m, order.Lines[0].FulfilledBaseQuantity);
        Assert.Equal(0m, order.Outstanding[1]);
    }

    [Fact]
    public void R27_Received_never_leaves_zero_to_ordered()
    {
        var order = Confirmed((A, 10m, 1m));
        order.Fulfil([new LineFulfilment(1, 4m)]);

        Assert.Throws<InvalidOperationException>(() => order.TakeBack([new LineFulfilment(1, 5m)]));
        Assert.Throws<InvalidOperationException>(() => order.TakeBack([new LineFulfilment(2, 1m)]));
        Assert.Equal(4m, order.Lines[0].FulfilledBaseQuantity);
    }

    [Fact]
    public void R31_Only_a_confirmed_order_is_received_against()
    {
        var draft = Draft(new OrderLineEntry(A, Pcs, 10m, 1m));
        Assert.Throws<InvalidOperationException>(() => draft.Fulfil([new LineFulfilment(1, 1m)]));

        var closed = Confirmed((A, 10m, 1m));
        closed.Close(Now, Actor);
        Assert.Throws<InvalidOperationException>(() => closed.Fulfil([new LineFulfilment(1, 1m)]));

        closed.Reopen();
        closed.Fulfil([new LineFulfilment(1, 1m)]);
    }

    // ---- receipt status (R29)

    [Fact]
    public void R29_Receipt_status_is_none_partial_or_full()
    {
        Assert.Equal(FulfilmentStatus.None, OrderProgress.Status([(10m, 0m), (5m, 0m)]));
        Assert.Equal(FulfilmentStatus.Partial, OrderProgress.Status([(10m, 10m), (5m, 0m)]));
        Assert.Equal(FulfilmentStatus.Partial, OrderProgress.Status([(10m, 4m), (5m, 5m)]));
        Assert.Equal(FulfilmentStatus.Full, OrderProgress.Status([(10m, 10m), (5m, 5m)]));
        // A draft has no base quantity yet.
        Assert.Equal(FulfilmentStatus.None, OrderProgress.Status([(null, 0m)]));
    }

    [Fact]
    public void R29_A_fully_received_order_stays_confirmed_and_closing_keeps_its_receipt_status()
    {
        var order = Confirmed((A, 10m, 1m), (B, 5m, 1m));
        Assert.Equal(FulfilmentStatus.None, order.FulfilmentStatus);

        order.Fulfil([new LineFulfilment(1, 10m)]);
        Assert.Equal(FulfilmentStatus.Partial, order.FulfilmentStatus);

        order.Fulfil([new LineFulfilment(2, 5m)]);
        Assert.Equal(FulfilmentStatus.Full, order.FulfilmentStatus);
        Assert.Equal(OrderStatus.Confirmed, order.Status);

        order.Close(Now, Actor);
        Assert.Equal(FulfilmentStatus.Full, order.FulfilmentStatus);
    }

    // ---- lifecycle (R11-R17)

    [Fact]
    public void R11_A_new_order_is_a_draft_without_number_and_with_lines_numbered_in_the_order_given()
    {
        var order = Draft(new OrderLineEntry(A, Pcs, 10m, 2.5m), new OrderLineEntry(B, Pcs, 1m, 0m));

        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Null(order.Number);
        Assert.Null(order.ConfirmedAt);
        Assert.Null(order.ClosedAt);
        Assert.Equal([1, 2], order.Lines.Select(l => l.LineNo));
        Assert.All(order.Lines, l => Assert.True(l.Factor is null && l.BaseQuantity is null && l.FulfilledBaseQuantity == 0));
        Assert.Equal(FulfilmentStatus.None, order.FulfilmentStatus);
        Assert.Equal(0m, order.Outstanding[1]);
    }

    [Fact]
    public void R5_Replace_renumbers_the_lines_and_returns_those_that_are_gone()
    {
        var order = Draft(new OrderLineEntry(A, Pcs, 10m, 1m), new OrderLineEntry(B, Pcs, 1m, 1m), new OrderLineEntry(A, Pcs, 2m, 1m));
        var later = Now.AddMinutes(5);

        var removed = order.Replace(Date, Date, Supplier, Warehouse, "ref", null, [new OrderLineEntry(B, Pcs, 7m, 1.1m)], later, Actor);

        Assert.Equal(2, removed.Count);
        var line = Assert.Single(order.Lines);
        Assert.Equal((1, B, 7m, 1.1m), (line.LineNo, line.ArticleId, line.Quantity, line.UnitPrice));
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(Date, order.DueDate);
    }

    [Fact]
    public void R3_R5_R7_R8_A_draft_refuses_what_validation_refuses()
    {
        OrderLineEntry[] one = [new(A, Pcs, 1m, 1m)];

        Assert.Throws<ArgumentException>(() => PurchaseOrder.Create(Date, Date.AddDays(-1), Supplier, Warehouse, null, null, one, Now, Actor));
        Assert.Throws<ArgumentException>(() => Draft());
        Assert.Throws<ArgumentException>(() => Draft(Enumerable.Repeat(one[0], 201).ToArray()));
        Assert.Throws<ArgumentException>(() => Draft(new OrderLineEntry(A, Pcs, 0m, 1m)));
        Assert.Throws<ArgumentException>(() => Draft(new OrderLineEntry(A, Pcs, 1m, -0.01m)));
        Assert.Throws<ArgumentException>(() => Draft(new OrderLineEntry(A, Pcs, 100000m, 100000m)));
        Assert.Equal(200, Draft(Enumerable.Repeat(one[0], 200).ToArray()).Lines.Count);
    }

    [Fact]
    public void R12_Confirmation_assigns_the_number_and_freezes_factor_and_base_quantity()
    {
        var order = Draft(new OrderLineEntry(A, Box, 5m, 30m), new OrderLineEntry(B, Pcs, 2.5m, 0.333333m));
        var confirmedAt = Now.AddHours(1);
        var confirmer = Guid.NewGuid();

        order.Confirm("PO-000001", [12m, 1m], confirmedAt, confirmer);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal("PO-000001", order.Number);
        Assert.Equal((confirmedAt, confirmer), (order.ConfirmedAt, order.ConfirmedBy));
        Assert.Equal([(12m, 60m), (1m, 2.5m)], order.Lines.Select(l => (l.Factor!.Value, l.BaseQuantity!.Value)));
        Assert.Equal(60m, order.Outstanding[1]);
        // R17: confirmation is attributed by confirmedBy, not by the audit columns.
        Assert.Equal((Now, Actor), (order.UpdatedAt, order.UpdatedBy));
    }

    [Fact]
    public void R12_A_line_that_does_not_convert_leaves_the_draft_untouched()
    {
        var order = Draft(new OrderLineEntry(A, Pcs, 1m, 1m), new OrderLineEntry(A, Box, 0.000001m, 1m));

        Assert.Throws<InvalidOperationException>(() => order.Confirm("PO-000001", [1m, 0.1m], Now, Actor));

        Assert.True(order.IsDraft);
        Assert.Null(order.Number);
        Assert.All(order.Lines, l => Assert.Null(l.Factor));
    }

    [Fact]
    public void R14_A_confirmed_order_is_not_replaced_or_confirmed_again()
    {
        var order = Confirmed((A, 10m, 1m));

        Assert.Throws<InvalidOperationException>(() =>
            order.Replace(Date, null, Supplier, Warehouse, null, null, [new OrderLineEntry(A, Pcs, 1m, 1m)], Now, Actor));
        Assert.Throws<InvalidOperationException>(() => order.Confirm("PO-000002", [1m], Now, Actor));
        Assert.Equal("PO-000001", order.Number);
        Assert.Equal(10m, order.Lines[0].Quantity);
    }

    [Fact]
    public void R15_R16_Close_and_reopen_change_nothing_but_the_status_and_who_closed()
    {
        var order = Confirmed((A, 10m, 1m));
        order.Fulfil([new LineFulfilment(1, 4m)]);
        var closer = Guid.NewGuid();
        var closedAt = Now.AddDays(1);

        order.Close(closedAt, closer);

        Assert.Equal((OrderStatus.Closed, closedAt, closer), (order.Status, order.ClosedAt, order.ClosedBy));
        Assert.Equal(0m, order.Outstanding[1]);
        Assert.Equal(FulfilmentStatus.Partial, order.FulfilmentStatus);

        order.Reopen();

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Null(order.ClosedAt);
        Assert.Null(order.ClosedBy);
        Assert.Equal("PO-000001", order.Number);
        Assert.Equal(6m, order.Outstanding[1]);
        Assert.Equal((Now, Actor), (order.UpdatedAt, order.UpdatedBy));
    }

    [Fact]
    public void R15_R16_Transitions_from_the_wrong_status_are_refused()
    {
        var draft = Draft(new OrderLineEntry(A, Pcs, 1m, 1m));
        Assert.Throws<InvalidOperationException>(() => draft.Close(Now, Actor));
        Assert.Throws<InvalidOperationException>(() => draft.Reopen());

        var confirmed = Confirmed((A, 10m, 1m));
        Assert.Throws<InvalidOperationException>(() => confirmed.Reopen());

        confirmed.Close(Now, Actor);
        Assert.Throws<InvalidOperationException>(() => confirmed.Close(Now, Actor));
        Assert.Throws<InvalidOperationException>(() => confirmed.Confirm("PO-000002", [1m], Now, Actor));
    }

    // ---- numbering (R13)

    [Fact]
    public void R13_Order_numbers_have_their_own_series_and_prefix()
    {
        Assert.Equal("PO-000001", DocumentNumber.Format(DocumentSeries.PurchaseOrder, 1));
        Assert.Equal("PO-1234567", DocumentNumber.Format(DocumentSeries.PurchaseOrder, 1234567));
        Assert.Equal("purchaseOrder", DocumentCounter.Start(Guid.NewGuid(), DocumentSeries.PurchaseOrder).DocumentType);
        // The stock series are what they were.
        Assert.Equal(new DocumentSeries("receipt", "SR"), DocumentSeries.Of(StockDocumentType.Receipt));
        Assert.Equal("count", DocumentCounter.Start(Guid.NewGuid(), DocumentSeries.Of(StockDocumentType.Count)).DocumentType);
    }

    // ---- the link of a stock document (R18-R20, R28, R32)

    private static StockDocument Receipt(Guid? order, params StockLineEntry[] lines) =>
        StockDocument.Create(StockDocumentType.Receipt, Date, Warehouse, null, null, null, lines, Now, Actor, purchaseOrderId: order);

    [Theory]
    [InlineData(StockDocumentType.Issue)]
    [InlineData(StockDocumentType.Transfer)]
    [InlineData(StockDocumentType.Count)]
    public void R18_Only_a_receipt_carries_the_link(StockDocumentType type)
    {
        Assert.Throws<ArgumentException>(() => StockDocument.Create(
            type, Date, Warehouse, type == StockDocumentType.Transfer ? Guid.NewGuid() : null, null, null,
            [new StockLineEntry(A, Pcs, 1m, 1)], Now, Actor, type == StockDocumentType.Count ? [0m] : null, purchaseOrderId: Guid.NewGuid()));
    }

    [Fact]
    public void R20_Every_line_of_a_linked_document_names_an_order_line_and_no_line_of_an_unlinked_one_does()
    {
        var order = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Receipt(order, new StockLineEntry(A, Pcs, 1m, 1), new StockLineEntry(A, Pcs, 1m)));
        Assert.Throws<ArgumentException>(() => Receipt(order, new StockLineEntry(A, Pcs, 1m, 0)));
        Assert.Throws<ArgumentException>(() => Receipt(null, new StockLineEntry(A, Pcs, 1m, 1)));

        var linked = Receipt(order, new StockLineEntry(A, Pcs, 1m, 2), new StockLineEntry(A, Pcs, 3m, 2));
        Assert.True(linked.IsLinked);
        Assert.All(linked.Lines, l => Assert.Equal((order, 2), (l.PurchaseOrderId!.Value, l.OrderLineNo!.Value)));
        Assert.False(Receipt(null, new StockLineEntry(A, Pcs, 1m)).IsLinked);
    }

    [Fact]
    public void R19_Replace_keeps_the_link_and_holds_the_new_lines_to_it()
    {
        var order = Guid.NewGuid();
        var linked = Receipt(order, new StockLineEntry(A, Pcs, 1m, 1));

        Assert.Throws<ArgumentException>(() => linked.Replace(Date, Warehouse, null, null, null, [new StockLineEntry(A, Pcs, 1m)], Now, Actor));
        linked.Replace(Date, Warehouse, null, null, null, [new StockLineEntry(B, Pcs, 2m, 3), new StockLineEntry(A, Pcs, 1m, 1)], Now, Actor);

        Assert.Equal(order, linked.PurchaseOrderId);
        Assert.Equal([3, 1], linked.Lines.Select(l => l.OrderLineNo!.Value));

        var unlinked = Receipt(null, new StockLineEntry(A, Pcs, 1m));
        Assert.Throws<ArgumentException>(() => unlinked.Replace(Date, Warehouse, null, null, null, [new StockLineEntry(A, Pcs, 1m, 1)], Now, Actor));
    }

    [Fact]
    public void R25_R28_A_posted_linked_receipt_writes_the_ledger_of_any_receipt_and_says_what_it_fulfils_in_base_units()
    {
        var linked = Receipt(Guid.NewGuid(), new StockLineEntry(A, Box, 2m, 1), new StockLineEntry(B, Pcs, 5m, 2));
        Assert.Throws<InvalidOperationException>(() => linked.Fulfilment);

        var entries = linked.Post("SR-000001", [12m, 1m], Now, Actor);

        Assert.Equal([24m, 5m], entries.Select(e => e.Quantity));
        Assert.Equal([new LineFulfilment(1, 24m), new LineFulfilment(2, 5m)], linked.Fulfilment);
        Assert.Empty(Receipt(null, new StockLineEntry(A, Pcs, 1m)).Fulfilment);
    }

    [Fact]
    public void R32_The_reversing_document_carries_the_same_order_and_order_lines()
    {
        var order = Guid.NewGuid();
        var linked = Receipt(order, new StockLineEntry(A, Box, 2m, 1), new StockLineEntry(B, Pcs, 5m, 2));
        var entries = linked.Post("SR-000001", [12m, 1m], Now, Actor);

        var (reversal, _) = linked.Reverse(entries, Date, null, "SR-000002", Now, Actor);

        Assert.Equal(order, reversal.PurchaseOrderId);
        Assert.Equal([(order, 1), (order, 2)], reversal.Lines.Select(l => (l.PurchaseOrderId!.Value, l.OrderLineNo!.Value)));
        Assert.Equal(linked.Fulfilment, reversal.Fulfilment);
    }
}
