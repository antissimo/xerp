using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-60 to AC-65 and AC-120: <c>purchase.overReceiptAllowed</c> and <c>sales.overDeliveryAllowed</c>
/// (R22–R25, E5). With <c>true</c> more than ordered is received / delivered, without limit; the outstanding
/// quantity never goes below zero; switching back changes nothing received and refuses the next excess.
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleOverFulfilmentTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;

    /// <summary>(fulfilled, outstanding) of order line k and the order's progress status, as the order reads now.</summary>
    private static async Task<(decimal Done, decimal Outstanding, string Progress)> LineAsync(OrderApi o, HttpClient client, Guid order, int k = 1)
    {
        var read = await o.GetAsync(client, order);
        var line = read.OrderLines()[k - 1];
        return (o.DoneOf(line), line.Outstanding(), o.ProgressOf(read));
    }

    private async Task<OrderSetup> AllowedAsync(string key)
    {
        var s = await Orders.SetupAsync(app);
        await Rules.SetAsync(s.Http, key, true);
        return s;
    }

    /// <summary>AC-60: over-receipt allowed, PO [A, 3 box, price 1] (base 36), receipts of 36 and of 100 posted.</summary>
    private async Task<(OrderSetup S, JsonElement Order, JsonElement First, JsonElement Excess)> ReceivedAboveAsync()
    {
        var s = await AllowedAsync(Rules.OverReceipt);
        var order = await PO.OrderedAsync(s, (s.A, 3, s.Box, 1m));
        Assert.Equal(36m, order.OrderLines()[0].Dec("baseQuantity"));
        var first = await PO.FulfilAsync(s.Http, order, 1, 36);
        var excess = await PO.FulfilAsync(s.Http, order, 1, 100);
        return (s, order, first, excess);
    }

    // ---- AC-60, AC-61 ----

    [Fact]
    public async Task AC60_With_over_receipt_allowed_more_than_ordered_is_received_and_outstanding_is_zero()
    {
        var (s, order, first, excess) = await ReceivedAboveAsync();

        Assert.Equal(("posted", "posted"), (first.Str("status"), excess.Str("status")));
        Assert.Equal((136m, 0m, "full"), await LineAsync(PO, s.Http, order.Id()));
        Assert.Equal("confirmed", (await PO.GetAsync(s.Http, order.Id())).Str("status"));
        // R23: the excess never lowers what is still on order.
        Assert.Equal(0m, await PO.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(136m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(new[] { "full" }, (await PO.ListAsync(s.Http, "?receiptStatus=full")).Items().Select(i => i.Str("receiptStatus")));
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC60_R23_An_excess_on_one_order_does_not_lower_what_another_order_still_awaits()
    {
        var s = await AllowedAsync(Rules.OverReceipt);
        var order = await PO.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 10, null, 1m));
        var other = await PO.OrderedAsync(s, (s.A, 10, null, 1m));

        await PO.FulfilAsync(s.Http, order, 1, 25);

        Assert.Equal((25m, 0m, "partial"), await LineAsync(PO, s.Http, order.Id(), 1));
        Assert.Equal((0m, 10m, "partial"), await LineAsync(PO, s.Http, order.Id(), 2));
        Assert.Equal((0m, 10m, "none"), await LineAsync(PO, s.Http, other.Id()));
        // incomingQuantity sums max(0, outstanding): 0 + 10, not 10 − 15.
        Assert.Equal(10m, await PO.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await PO.OnHandAsync(s.Http, s.B, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC61_With_over_receipt_allowed_there_is_no_limit()
    {
        var s = await AllowedAsync(Rules.OverReceipt);
        var order = await PO.OrderedAsync(s, (s.A, 3, s.Box, 1m));

        var posted = await PO.FulfilAsync(s.Http, order, 1, 500);

        Assert.Equal(("posted", "SR-000001"), (posted.Str("status"), posted.Number()));
        Assert.Equal((500m, 0m, "full"), await LineAsync(PO, s.Http, order.Id()));
        Assert.Equal(500m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC61_At_the_default_one_unit_above_the_order_is_refused_and_the_ordered_quantity_posts()
    {
        var s = await Orders.SetupAsync(app);
        var order = await PO.OrderedAsync(s, (s.A, 3, s.Box, 1m));

        var (draft, response) = await PO.TryFulfilAsync(s.Http, order, 1, 37);

        using (response)
            await Rules.ExceedsAsync(response, PO, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        Assert.Equal((0m, 36m, "none"), await LineAsync(PO, s.Http, order.Id()));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var posted = await PO.FulfilAsync(s.Http, order, 1, 36);

        // Nothing was posted and no number consumed by the refusal.
        Assert.Equal("SR-000001", posted.Number());
        Assert.Equal((36m, 0m, "full"), await LineAsync(PO, s.Http, order.Id()));
        await Rules.AssertBalancedAsync(s.Http);
    }

    // ---- AC-62 ----

    [Fact]
    public async Task AC62_Switching_back_changes_nothing_received_and_refuses_the_next_receipt()
    {
        var (s, order, _, excess) = await ReceivedAboveAsync();
        var http = s.Http;
        var before = await PO.GetAsync(http, order.Id());
        var onHand = await Stock.OnHandAsync(http);

        await Rules.SetAsync(http, Rules.OverReceipt, false);

        McpAssert.JsonEqual(before, await PO.GetAsync(http, order.Id()), "The order changed with the rule");
        McpAssert.JsonEqual(onHand, await Stock.OnHandAsync(http), "Stock on hand changed with the rule");
        Assert.Equal((136m, 0m, "full"), await LineAsync(PO, http, order.Id()));

        var (draft, further) = await PO.TryFulfilAsync(http, order, 1, 1);
        using (further)
            await Rules.ExceedsAsync(further, PO, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, draft);

        // R24: a reversal is not judged by this rule.
        var reversing = await Stock.ReverseAsync(http, excess.Id());

        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal((36m, 0m, "full"), await LineAsync(PO, http, order.Id()));
        Assert.Equal(36m, await Stock.QuantityAsync(http, s.A, s.W1));
        // 36 of 36: a receipt of 1 is still refused — and it is the same draft as before.
        using (var still = await Stock.SendPostAsync(http, draft))
            await Rules.ExceedsAsync(still, PO, "lines[0].quantity");
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-63 ----

    [Fact]
    public async Task AC63_Partial_then_excess()
    {
        var s = await AllowedAsync(Rules.OverReceipt);
        var order = await PO.OrderedAsync(s, (s.A, 100, null, 1m));

        await PO.FulfilAsync(s.Http, order, 1, 50);

        Assert.Equal((50m, 50m, "partial"), await LineAsync(PO, s.Http, order.Id()));
        Assert.Equal(50m, await PO.OnHandAsync(s.Http, s.A, s.W1));

        await PO.FulfilAsync(s.Http, order, 1, 60);

        Assert.Equal((110m, 0m, "full"), await LineAsync(PO, s.Http, order.Id()));
        Assert.Equal(0m, await PO.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(110m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    // ---- AC-64 ----

    [Fact]
    public async Task AC64_With_over_delivery_allowed_more_than_ordered_is_delivered_and_switching_back_refuses_one_more()
    {
        var s = await AllowedAsync(Rules.OverDelivery);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 1000);
        var order = await SO.OrderedAsync(s, (s.A, 100, null, 1m));
        Assert.Equal(100m, await SO.OnHandAsync(http, s.A, s.W1));

        var delivery = await SO.FulfilAsync(http, order, 1, 110);

        Assert.Equal("posted", delivery.Str("status"));
        var read = await SO.GetAsync(http, order.Id());
        Assert.Equal(110m, read.OrderLines()[0].Dec("deliveredBaseQuantity"));
        Assert.Equal(0m, read.OrderLines()[0].Outstanding());
        Assert.Equal("full", read.Str("deliveryStatus"));
        var item = (await Orders.OnHandItemAsync(http, s.A, s.W1))!.Value;
        Assert.Equal((890m, 0m, 0m, 890m), item.Quantities());

        await Rules.SetAsync(http, Rules.OverDelivery, false);

        McpAssert.JsonEqual(read, await SO.GetAsync(http, order.Id()), "The order changed with the rule");
        var (draft, response) = await SO.TryFulfilAsync(http, order, 1, 1);
        using (response)
            await Rules.ExceedsAsync(response, SO, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, draft);
        Assert.Equal(890m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-65 ----

    [Fact]
    public async Task AC65_The_two_rules_are_separate()
    {
        var s = await AllowedAsync(Rules.OverReceipt);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 100);
        var sales = await SO.OrderedAsync(s, (s.A, 10, null, 1m));

        var (delivery, aboveSales) = await SO.TryFulfilAsync(http, sales, 1, 11);

        using (aboveSales)
            await Rules.ExceedsAsync(aboveSales, SO, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, delivery);

        // And the reverse: only over-delivery allowed, a receipt above its order is refused.
        await Rules.ResetAsync(http, Rules.OverReceipt);
        await Rules.SetAsync(http, Rules.OverDelivery, true);
        var purchase = await PO.OrderedAsync(s, (s.A, 10, null, 1m));

        var (receipt, abovePurchase) = await PO.TryFulfilAsync(http, purchase, 1, 11);

        using (abovePurchase)
            await Rules.ExceedsAsync(abovePurchase, PO, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, receipt);
        // The delivery refused above now posts.
        await Stock.PostDocumentAsync(http, delivery);
        Assert.Equal(89m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC65_Over_delivery_allows_more_than_ordered_not_more_than_there_is()
    {
        var s = await AllowedAsync(Rules.OverDelivery);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 105);
        var order = await SO.OrderedAsync(s, (s.A, 100, null, 1m));

        var (draft, response) = await SO.TryFulfilAsync(http, order, 1, 110);

        // R25, R30: the order's quantities pass, then stock refuses — naming the stock rule, not the order rule.
        using (response)
            await Rules.InsufficientAsync(response, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, draft);
        Assert.Equal(105m, await Stock.QuantityAsync(http, s.A, s.W1));

        var posted = await SO.FulfilAsync(http, order, 1, 105);

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }
}
