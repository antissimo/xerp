using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-70 to AC-78 and AC-120: <c>sales.reservedStockProtected</c> (R26–R31, E6–E8). With <c>true</c>
/// an unlinked issue, a transfer out and a reversal are refused with <c>STOCK_RESERVED</c> when they take goods
/// that confirmed sales orders still await — and nothing else is: not a delivery within its order, not a
/// receipt, not a count, not the confirmation of an order.
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleReservedStockTests(XerpFixture app)
{
    private static readonly OrderApi SO = OrderApi.Sales;

    private async Task<OrderSetup> ProtectedAsync()
    {
        var s = await Orders.SetupAsync(app);
        await Rules.SetAsync(s.Http, Rules.Protected, true);
        return s;
    }

    /// <summary>AC-70: protected, Receive 10 of A into W1 (document D), SO [A, 8]: Available(A, W1) == 2.</summary>
    private async Task<(OrderSetup S, JsonElement Receipt, JsonElement Order)> TenWithEightReservedAsync()
    {
        var s = await ProtectedAsync();
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var order = await SO.OrderedAsync(s, (s.A, 8, null, 1m));
        Assert.Equal(2m, await Rules.AvailableAsync(s.Http, s.A, s.W1));
        return (s, receipt, order);
    }

    // ---- AC-70, AC-71 ----

    [Fact]
    public async Task AC70_AC71_An_unlinked_issue_cannot_take_reserved_stock_and_the_delivery_for_the_order_posts()
    {
        var (s, _, order) = await TenWithEightReservedAsync();
        var http = s.Http;
        var ledger = await Stock.LedgerAsync(http);

        var (three, refused) = await Rules.TryIssueAsync(http, s.W1, s.A, 3);

        using (refused)
            await Rules.ReservedAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, three);
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(http), "A refused posting wrote to the ledger");
        Assert.Equal((10m, 2m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));

        var two = await Stock.IssueAsync(http, s.W1, s.A, 2);

        // No number was consumed by the refusal.
        Assert.Equal("SI-000001", two.Number());
        Assert.Equal((8m, 0m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));
        // The smallest further issue is refused too: available would fall below zero.
        var (more, alsoRefused) = await Rules.TryIssueAsync(http, s.W1, s.A, 0.000001m);
        using (alsoRefused)
            await Rules.ReservedAsync(alsoRefused, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, more);

        // AC-71: the delivery against the order is never refused by this rule (R28).
        var delivery = await SO.FulfilAsync(http, order, 1, 8);

        Assert.Equal(("posted", "SI-000002"), (delivery.Str("status"), delivery.Number()));
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));
        Assert.Equal("full", SO.ProgressOf(await SO.GetAsync(http, order.Id())));
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC70_R27_Only_the_lines_of_the_reserved_article_are_named_and_other_articles_are_free()
    {
        var (s, _, _) = await TenWithEightReservedAsync();
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.B, 10);
        var mixed = await Stock.CreateAsync(http, "issue", s.W1, (s.B, 4), (s.A, 3));

        using (var refused = await Stock.SendPostAsync(http, mixed.Id()))
            await Rules.ReservedAsync(refused, "lines[1].quantity");

        // Atomic: B did not move either.
        Assert.Equal(10m, await Stock.QuantityAsync(http, s.B, s.W1));
        // Nothing reserves B: all of it may be issued.
        await Stock.IssueAsync(http, s.W1, s.B, 10);
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.B, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-72 ----

    [Fact]
    public async Task AC72_A_transfer_out_cannot_take_reserved_stock_and_the_other_warehouse_plays_no_part()
    {
        var (s, _, _) = await TenWithEightReservedAsync();
        var http = s.Http;
        // Stock in W2 and an order for W2 do not help or harm W1.
        await Stock.ReceiveAsync(http, s.W2, s.A, 100);
        await SO.OrderedAsync(s, s.W2, (s.A, 50, null, 1m));

        var three = await Stock.CreateTransferAsync(http, s.W1, s.W2, (s.A, 3));
        using (var refused = await Stock.SendPostAsync(http, three.Id()))
            await Rules.ReservedAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, three.Id());
        Assert.Equal((10m, 100m), (await Stock.QuantityAsync(http, s.A, s.W1), await Stock.QuantityAsync(http, s.A, s.W2)));

        var two = await Stock.TransferAsync(http, s.W1, s.W2, s.A, 2);

        Assert.Equal("posted", two.Str("status"));
        Assert.Equal((8m, 102m), (await Stock.QuantityAsync(http, s.A, s.W1), await Stock.QuantityAsync(http, s.A, s.W2)));
        Assert.Equal((0m, 52m), (await Rules.AvailableAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W2)));
        // Into W1 a transfer is free; out of W2 down to what its order awaits, too.
        await Stock.TransferAsync(http, s.W2, s.W1, s.A, 52);
        Assert.Equal((52m, 0m), (await Rules.AvailableAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W2)));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-73 ----

    [Fact]
    public async Task AC73_A_reversal_that_takes_reserved_stock_is_refused_until_the_order_is_closed()
    {
        var (s, receipt, order) = await TenWithEightReservedAsync();
        var http = s.Http;
        var before = await Stock.GetAsync(http, receipt.Id());
        var documents = (await Stock.DocumentsAsync(http)).Total();

        using (var refused = await Stock.SendReverseAsync(http, receipt.Id()))
            await Rules.ReservedAsync(refused, "lines[0].quantity");

        await Stock.AssertUnchangedAsync(http, before);
        Assert.Equal("posted", before.Str("status"));
        Assert.Equal(documents, (await Stock.DocumentsAsync(http)).Total());
        Assert.Equal(10m, await Stock.QuantityAsync(http, s.A, s.W1));

        await SO.CloseAsync(http, order.Id());

        var reversing = await Stock.ReverseAsync(http, receipt.Id());

        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC73_R28_Reversing_an_issue_or_a_delivery_is_never_refused_by_this_rule()
    {
        var (s, _, order) = await TenWithEightReservedAsync();
        var http = s.Http;
        var issue = await Stock.IssueAsync(http, s.W1, s.A, 2);
        var delivery = await SO.FulfilAsync(http, order, 1, 8);

        // Reversing the delivery raises stock and the reservation alike; reversing the issue raises stock.
        await Stock.ReverseAsync(http, delivery.Id(), Stock.NextDay);
        await Stock.ReverseAsync(http, issue.Id());

        Assert.Equal((10m, 2m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));
        Assert.Equal(8m, await SO.OnHandAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-74 ----

    [Fact]
    public async Task AC74_Between_orders_the_first_delivery_gets_the_goods_and_the_second_is_refused_for_stock()
    {
        var s = await ProtectedAsync();
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        var so1 = await SO.OrderedAsync(s, (s.A, 10, null, 1m));
        var so2 = await SO.OrderedAsync(s, (s.A, 10, null, 1m));
        Assert.Equal(-10m, await Rules.AvailableAsync(http, s.A, s.W1));

        var second = await SO.FulfilAsync(http, so2, 1, 10);

        Assert.Equal("posted", second.Str("status"));
        Assert.Equal((0m, -10m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));

        var (draft, response) = await SO.TryFulfilAsync(http, so1, 1, 10);

        // R30: stock answers first, alone — INSUFFICIENT_STOCK naming the stock rule, not STOCK_RESERVED.
        using (response)
            await Rules.InsufficientAsync(response, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, draft);
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-75 ----

    [Fact]
    public async Task AC75_With_availability_already_negative_a_receipt_and_a_count_post_and_an_issue_is_refused()
    {
        var s = await ProtectedAsync();
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 5);
        await SO.OrderedAsync(s, (s.A, 10, null, 1m));
        Assert.Equal(-5m, await Rules.AvailableAsync(http, s.A, s.W1));

        await Stock.ReceiveAsync(http, s.W1, s.A, 1);

        Assert.Equal((6m, -4m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));

        var (issue, refused) = await Rules.TryIssueAsync(http, s.W1, s.A, 1);
        using (refused)
            await Rules.ReservedAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, issue);

        // R29: a count states what is there — it is never judged by this rule.
        var count = await Counts.PostedAsync(http, s.W1, (s.A, 3, null));

        Assert.Equal("posted", count.Str("status"));
        Assert.Equal((3m, -7m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-76 ----

    [Fact]
    public async Task AC76_After_reset_the_refused_issue_posts()
    {
        var (s, _, _) = await TenWithEightReservedAsync();
        var http = s.Http;
        var (three, refused) = await Rules.TryIssueAsync(http, s.W1, s.A, 3);
        using (refused)
            await Rules.ReservedAsync(refused);

        Rules.AssertAtDefault(await Rules.ResetAsync(http, Rules.Protected), Rules.Protected);

        var posted = await Stock.PostDocumentAsync(http, three);

        Assert.Equal(("posted", "SI-000001"), (posted.Str("status"), posted.Number()));
        Assert.Equal((7m, -1m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC76_R26_Set_explicitly_to_false_reserved_stock_blocks_nothing()
    {
        var s = await Orders.SetupAsync(app);
        await Rules.SetAsync(s.Http, Rules.Protected, false);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.B, 10);
        await SO.OrderedAsync(s, (s.B, 8, null, 1m));

        await Stock.IssueAsync(s.Http, s.W1, s.B, 3);
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.B, 7);

        Assert.Equal((0m, -8m), (await Stock.QuantityAsync(s.Http, s.B, s.W1), await Rules.AvailableAsync(s.Http, s.B, s.W1)));
        // With nothing left, reversing the receipt is refused for stock — by the other rule.
        using (var reversal = await Stock.SendReverseAsync(s.Http, receipt.Id()))
            await Rules.InsufficientAsync(reversal, "lines[0].quantity");
        await Rules.AssertBalancedAsync(s.Http);
    }

    // ---- AC-77 ----

    [Fact]
    public async Task AC77_The_rule_is_independent_of_negative_stock()
    {
        var s = await ProtectedAsync();
        var http = s.Http;
        await Rules.SetAsync(http, Rules.NegativeStock, true);
        var order = await SO.OrderedAsync(s, (s.A, 5, null, 1m));

        var (issue, refused) = await Rules.TryIssueAsync(http, s.W1, s.A, 1);

        using (refused)
            await Rules.ReservedAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, issue);
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));

        await SO.CloseAsync(http, order.Id());

        var posted = await Stock.PostDocumentAsync(http, issue);

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal(-1m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-78 ----

    [Fact]
    public async Task AC78_Confirming_closing_and_reopening_a_sales_order_are_unaffected()
    {
        var s = await ProtectedAsync();
        var http = s.Http;

        var order = await SO.OrderedAsync(s, (s.A, 5, null, 1m));

        Assert.Equal(("confirmed", "SO-000001"), (order.Str("status"), order.Number()));
        Assert.Equal(-5m, await Rules.AvailableAsync(http, s.A, s.W1));
        Assert.Equal("closed", (await SO.CloseAsync(http, order.Id())).Str("status"));
        Assert.Equal("confirmed", (await SO.ReopenAsync(http, order.Id())).Str("status"));
        Assert.Equal(-5m, await Rules.AvailableAsync(http, s.A, s.W1));
        // R31: a second order beyond stock confirms too.
        await SO.OrderedAsync(s, (s.A, 7, null, 1m));
        Assert.Equal(-12m, await Rules.AvailableAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- R28, third bullet ----

    [Fact]
    public async Task R28_The_part_of_a_delivery_above_its_order_is_judged_against_what_other_orders_await()
    {
        var s = await ProtectedAsync();
        var http = s.Http;
        await Rules.SetAsync(http, Rules.OverDelivery, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 20);
        var so1 = await SO.OrderedAsync(s, (s.A, 10, null, 1m));
        await SO.OrderedAsync(s, (s.A, 10, null, 1m));
        Assert.Equal(0m, await Rules.AvailableAsync(http, s.A, s.W1));

        // 11 against SO1: stock 9, still reserved 10 (SO2) — availability falls from 0 to −1.
        var (draft, response) = await SO.TryFulfilAsync(http, so1, 1, 11);

        using (response)
            await Rules.ReservedAsync(response, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, draft);

        // Up to the outstanding quantity the delivery leaves availability as it is and posts.
        await SO.FulfilAsync(http, so1, 1, 10);

        Assert.Equal((10m, 0m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task R28_An_excess_delivery_that_takes_nothing_another_order_awaits_posts()
    {
        var s = await ProtectedAsync();
        var http = s.Http;
        await Rules.SetAsync(http, Rules.OverDelivery, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 20);
        var order = await SO.OrderedAsync(s, (s.A, 10, null, 1m));

        // After it nothing is reserved any more (R27: "the reserved quantity after it is above 0" does not hold).
        var posted = await SO.FulfilAsync(http, order, 1, 15);

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal((5m, 5m), (await Stock.QuantityAsync(http, s.A, s.W1), await Rules.AvailableAsync(http, s.A, s.W1)));
        await Rules.AssertBalancedAsync(http);
    }
}
