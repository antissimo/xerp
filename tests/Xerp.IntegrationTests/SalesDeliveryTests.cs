using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 010, AC-40 to AC-54 (delivery): an issue linked to a sales order takes the goods out of stock like any
/// issue and raises the delivered quantity of the order lines it names — never more than was ordered, never
/// more than is on hand, the order checked before the stock, and a refused posting changes nothing.
/// </summary>
[Collection(XerpCollection.Name)]
public class SalesDeliveryTests(XerpFixture app)
{
    private static readonly OrderApi O = OrderApi.Sales;

    private static async Task<JsonElement> AssertRefusedAsync(HttpResponseMessage response, string code, params string[] exactKeys)
    {
        var problem = await Stock.ConflictAsync(response, code, exactKeys);
        Assert.Equal(exactKeys.Order(StringComparer.Ordinal).ToArray(), McpAssert.ErrorKeys(problem));
        return problem;
    }

    private static Task<JsonElement> AssertExceedsAsync(HttpResponseMessage response, params string[] exactKeys) =>
        AssertRefusedAsync(response, "QUANTITY_EXCEEDS_ORDER", exactKeys);

    private static Task<JsonElement> AssertShortAsync(HttpResponseMessage response, params string[] exactKeys) =>
        AssertRefusedAsync(response, "INSUFFICIENT_STOCK", exactKeys);

    // ---- AC-40 to AC-43 ----

    [Fact]
    public async Task AC40_A_linked_draft_issue_names_the_order_and_moves_nothing()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 40, null, 4.5m));
        var body = new JsonObject
        {
            ["type"] = "issue", ["documentDate"] = "2026-10-10", ["warehouseId"] = s.W1.ToString(),
            ["salesOrderId"] = order.Id().ToString(),
            ["lines"] = new JsonArray(new JsonObject { ["articleId"] = s.A.ToString(), ["quantity"] = 25, ["orderLineNo"] = 1 }),
        };

        using var response = await Stock.PostAsync(s.Http, body);

        var draft = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(("issue", "draft"), (draft.Str("type"), draft.Str("status")));
        JsonBody.AssertNull(draft, "number", "postedAt", "postedBy", "purchaseOrder");
        var link = draft.GetProperty("salesOrder");
        Assert.Equal((order.Id(), "SO-000001"), (link.Id(), link.Str("number")));
        Assert.Equal(1, Assert.Single(draft.DocumentLines()).GetProperty("orderLineNo").GetInt32());
        McpAssert.JsonEqual(draft, await Stock.GetAsync(s.Http, draft.Id()));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 40m));
        Assert.Equal(180m, order.Dec("totalAmount"));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(1, (await Stock.LedgerAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC41_AC42_Posting_takes_the_goods_out_of_stock_and_raises_the_delivered_quantity()
    {
        var s = await Orders.SetupAsync(app);
        var clerk = await Keys.CreateAsync(app, s.Http, "dispatch", "human");
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 40, null, 4.5m));
        var draft = await O.DraftDocumentAsync(s.Http, order, 1, 25);

        using var response = await Stock.SendPostAsync(clerk.Client, draft.Id());

        // AC-41
        var posted = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(("posted", "SI-000001"), (posted.Str("status"), posted.Number()));
        Assert.Equal(clerk.Id, posted.GetProperty("postedBy").GetGuid());
        O.AssertLinked(posted, order);
        JsonBody.AssertNull(posted, "purchaseOrder");
        Assert.Equal(75m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, draft.Id()));
        Assert.Equal(-25m, entry.Quantity());
        Assert.Equal((s.A, s.W1), (entry.GetProperty("article").Id(), entry.GetProperty("warehouse").Id()));
        var after = await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (25m, 15m));
        foreach (var property in order.EnumerateObject().Where(p => p.Name is not ("lines" or "deliveryStatus")))
            McpAssert.JsonEqual(property.Value, after.GetProperty(property.Name), $"Posting a delivery changed '{property.Name}' of the order");
        Assert.Equal(40m, after.OrderLines()[0].Dec("baseQuantity"));

        // AC-42
        var second = await O.FulfilAsync(s.Http, order, 1, 15);

        Assert.Equal("SI-000002", second.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (40m, 0m));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(60m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC43_Never_more_than_ordered_and_a_refused_posting_changes_nothing()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 40, null, 4.5m));
        await O.FulfilAsync(s.Http, order, 1, 25);
        var before = await O.GetAsync(s.Http, order.Id());
        var draft = await O.DraftDocumentAsync(s.Http, order, 1, 16);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertExceedsAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await O.AssertUnchangedAsync(s.Http, before);
        Assert.Equal(75m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft.Id()));

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 15, 1, null)));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SI-000002", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (40m, 0m));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-44, AC-45 ----

    [Fact]
    public async Task AC44_Never_more_than_is_there()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var order = await O.OrderedAsync(s, (s.A, 40, null, 1m));

        var (draft, refused) = await O.TryFulfilAsync(s.Http, order, 1, 11);

        await AssertShortAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 40m));
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft));

        var posted = await O.FulfilAsync(s.Http, order, 1, 10);

        // The refused posting consumed no number.
        Assert.Equal("SI-000001", posted.Number());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (10m, 30m));

        // R10: the refused draft is posted later, once the goods are there.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 30);
        await Stock.ReplaceAsync(s.Http, draft, OrderApi.DocumentReplacement(s.W1, (s.A, 30, 1, null)));
        await Stock.PostDocumentAsync(s.Http, draft);

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (40m, 0m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        refused.Dispose();
    }

    [Fact]
    public async Task AC44_The_stock_limit_is_exact_to_the_last_base_unit()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var order = await O.OrderedAsync(s, (s.A, 40, null, 1m));

        var (_, above) = await O.TryFulfilAsync(s.Http, order, 1, 10.000001m);
        await AssertShortAsync(above, "lines[0].quantity");
        await O.FulfilAsync(s.Http, order, 1, 9.999999m);
        await O.FulfilAsync(s.Http, order, 1, 0.000001m);
        var (_, empty) = await O.TryFulfilAsync(s.Http, order, 1, 0.000001m);
        await AssertShortAsync(empty, "lines[0].quantity");

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (10m, 30m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        above.Dispose();
        empty.Dispose();
    }

    [Fact]
    public async Task AC45_E3_The_order_is_checked_before_the_stock()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var order = await O.OrderedAsync(s, (s.A, 5, null, 1m));

        var (draft, refused) = await O.TryFulfilAsync(s.Http, order, 1, 20);

        await AssertExceedsAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        // Above the order but within stock is the same answer: the order's limit does not depend on stock.
        var (_, withinStock) = await O.TryFulfilAsync(s.Http, order, 1, 6);
        await AssertExceedsAsync(withinStock, "lines[0].quantity");
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 5m));
        refused.Dispose();
        withinStock.Dispose();
    }

    [Fact]
    public async Task AC45_R7_Order_of_checks_at_posting_masters_conversion_the_order_its_quantities_then_stock()
    {
        var s = await Orders.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 1);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 4);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 1m));
        // Line 0 in a unit that will not convert and with no stock of A at all; line 1 above the order and above
        // stock; then: article B inactive, the warehouse inactive, the order closed.
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 0.000001m, 1, s.Pack), (s.B, 6, 2, null));
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await O.CloseAsync(s.Http, order.Id());

        async Task<string[]> RefusedAsync(string code)
        {
            using var response = await Stock.SendPostAsync(s.Http, draft.Id());
            var keys = McpAssert.ErrorKeys(await Stock.ConflictAsync(response, code));
            await Stock.AssertDraftAsync(s.Http, draft.Id());
            return keys;
        }

        Assert.Equal(new[] { "warehouseId" }, await RefusedAsync("REFERENCE_INACTIVE"));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        Assert.Equal(new[] { "lines[1].articleId" }, await RefusedAsync("REFERENCE_INACTIVE"));
        await Stock.SetArticleActiveAsync(s.Http, s.B, true);
        Assert.Equal(new[] { "lines[0].quantity" }, await RefusedAsync("QUANTITY_NOT_CONVERTIBLE"));
        await Units.SetAsync(s.Http, s.A, s.Pack, 1);
        Assert.Equal(new[] { "salesOrderId" }, await RefusedAsync("ORDER_NOT_OPEN"));
        await O.ReopenAsync(s.Http, order.Id());
        Assert.Equal(new[] { "lines[1].quantity" }, await RefusedAsync("QUANTITY_EXCEEDS_ORDER"));
        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 0.000001m, 1, s.Pack), (s.B, 5, 2, null)));
        // Within the order now; neither article is covered: A has no stock, B has 4.
        Assert.Equal(new[] { "lines[0].quantity", "lines[1].quantity" }, await RefusedAsync("INSUFFICIENT_STOCK"));
        Assert.Equal(1, (await Stock.LedgerAsync(s.Http)).Total());

        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 1);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        // None of the six refused postings consumed a number.
        Assert.Equal("SI-000001", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (0.000001m, 9.999999m), (5m, 0m));
    }

    // ---- AC-46, AC-47 ----

    [Fact]
    public async Task AC46_E4_One_short_line_refuses_the_whole_delivery_and_only_it_is_named()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 2m));
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 10, 1, null), (s.B, 5, 2, null));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertShortAsync(refused, "lines[1].quantity");
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m), (0m, 5m));
        await Stock.AssertDraftAsync(s.Http, draft.Id());

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 10, 1, null)));
        await Stock.PostDocumentAsync(s.Http, draft.Id());

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (10m, 0m), (0m, 5m));
        Assert.Equal(90m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC46_R9_Stock_is_judged_per_article_over_all_lines_of_the_delivery()
    {
        // Two order lines of the same article, each within its order line, together above stock (005/R15).
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 15);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 15);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 1m), (s.A, 10, null, 0m));
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 8, 1, null), (s.B, 5, 2, null), (s.A, 8, 3, null));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertShortAsync(refused, "lines[0].quantity", "lines[2].quantity");
        Assert.Equal(15m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m), (0m, 5m), (0m, 10m));

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 8, 1, null), (s.B, 5, 2, null), (s.A, 7, 3, null)));
        await Stock.PostDocumentAsync(s.Http, draft.Id());

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (8m, 2m), (5m, 0m), (7m, 3m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC47_E6_Several_delivery_lines_for_one_order_line_are_summed()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 6, 1, null), (s.A, 5, 1, null));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertExceedsAsync(refused, "lines[0].quantity", "lines[1].quantity");
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 6, 1, null), (s.A, 4, 1, null)));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal(new[] { -6m, -4m }, (await Stock.EntriesAsync(s.Http, posted.Id())).Select(e => e.Quantity()).ToArray());
        Assert.Equal(90m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-48 ----

    [Fact]
    public async Task AC48_E5_Base_quantities_are_compared_with_the_order_and_with_stock_whatever_the_units()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var inBoxes = await O.OrderedAsync(s, (s.A, 5, s.Box, 60m));
        Assert.Equal((60m, 300m), (inBoxes.OrderLines()[0].Outstanding(), inBoxes.Dec("totalAmount")));

        var first = await O.FulfilAsync(s.Http, inBoxes, 1, 2, s.Box);

        Units.AssertLine(first.DocumentLines()[0], "box", quantity: 2m, factor: 12m, baseQuantity: 24m);
        Assert.Equal(-24m, Assert.Single(await Stock.EntriesAsync(s.Http, first.Id())).Quantity());
        await O.AssertProgressAsync(s.Http, inBoxes.Id(), "confirmed", "partial", (24m, 36m));

        await O.FulfilAsync(s.Http, inBoxes, 1, 36);

        await O.AssertProgressAsync(s.Http, inBoxes.Id(), "confirmed", "full", (60m, 0m));
        Assert.Equal(40m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        // Ordered in pieces, delivered in boxes: 4 boxes are 48 > 40.
        var inPieces = await O.OrderedAsync(s, (s.A, 40, null, 1m));
        var (draft, above) = await O.TryFulfilAsync(s.Http, inPieces, 1, 4, s.Box);
        await AssertExceedsAsync(above, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        // 3 boxes are 36: within the order and within the 40 on hand.
        await O.FulfilAsync(s.Http, inPieces, 1, 3, s.Box);
        await O.AssertProgressAsync(s.Http, inPieces.Id(), "confirmed", "partial", (36m, 4m));
        // 4 pieces are outstanding and 4 are on hand; a box is within neither.
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        above.Dispose();
    }

    [Fact]
    public async Task AC48_E5_A_delivery_in_boxes_is_judged_against_stock_in_base_units()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 20);
        var order = await O.OrderedAsync(s, (s.A, 5, s.Box, 60m));

        var (draft, shortOf) = await O.TryFulfilAsync(s.Http, order, 1, 2, s.Box);

        // 24 pieces within the 60 ordered, above the 20 on hand.
        await AssertShortAsync(shortOf, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        await O.FulfilAsync(s.Http, order, 1, 1, s.Box);
        Assert.Equal(8m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (12m, 48m));
        shortOf.Dispose();
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_E7_Stock_in_another_warehouse_does_not_serve_the_delivery()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 50);
        var order = await O.OrderedAsync(s, (s.A, 5, null, 1m));

        var (draft, refused) = await O.TryFulfilAsync(s.Http, order, 1, 5);

        await AssertShortAsync(refused, "lines[0].quantity");
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 5m));

        await Stock.TransferAsync(s.Http, s.W2, s.W1, s.A, 5);
        var posted = await Stock.PostDocumentAsync(s.Http, draft);

        Assert.Equal("SI-000001", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (5m, 0m));
        Assert.Equal((0m, 45m), (await Stock.QuantityAsync(s.Http, s.A, s.W1), await Stock.QuantityAsync(s.Http, s.A, s.W2)));
        refused.Dispose();
    }

    // ---- AC-51, AC-52 ----

    [Fact]
    public async Task AC51_R12_Parallel_deliveries_never_take_an_order_line_above_its_quantity()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var drafts = new List<Guid>();
        for (var i = 0; i < 10; i++)
            drafts.Add((await O.DraftDocumentAsync(s.Http, order, 1, 3)).Id());

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        Assert.Equal(Enumerable.Repeat(200, 3).Concat(Enumerable.Repeat(409, 7)).ToArray(), responses.Select(r => (int)r.StatusCode).Order().ToArray());
        var numbers = new List<string?>();
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.OK)
                numbers.Add((await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Number());
            else
                await AssertExceedsAsync(response, "lines[0].quantity");
            response.Dispose();
        }
        Assert.Equal(new[] { "SI-000001", "SI-000002", "SI-000003" }, numbers.Order(StringComparer.Ordinal).ToArray());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (9m, 1m));
        Assert.Equal(91m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(91m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        Assert.Equal(1m, await O.OnHandAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC52_R12_Two_orders_racing_for_the_same_stock_only_one_is_served()
    {
        var s = await Orders.SetupAsync(app);
        for (var round = 0; round < 10; round++)
        {
            await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
            var first = await O.OrderedAsync(s, (s.A, 10, null, 1m));
            var second = await O.OrderedAsync(s, (s.A, 10, null, 1m));
            var drafts = new[] { await O.DraftDocumentAsync(s.Http, first, 1, 10), await O.DraftDocumentAsync(s.Http, second, 1, 10) };

            var responses = await Task.WhenAll(drafts.Select(d => Task.Run(() => Stock.SendPostAsync(s.Http, d.Id()))));

            Assert.Equal(new[] { 200, 409 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
            await AssertShortAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), "lines[0].quantity");
            Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
            var progress = new[] { O.ProgressOf(await O.GetAsync(s.Http, first.Id())), O.ProgressOf(await O.GetAsync(s.Http, second.Id())) };
            Assert.Equal(new[] { "full", "none" }, progress.Order(StringComparer.Ordinal).ToArray());
            foreach (var response in responses)
                response.Dispose();
            // The order that was not served stays outstanding; close it so the next round starts clean.
            var loser = progress[0] == "none" ? first : second;
            await O.AssertProgressAsync(s.Http, loser.Id(), "confirmed", "none", (0m, 10m));
            await O.CloseAsync(s.Http, loser.Id());
            Assert.Equal(0m, await O.OnHandAsync(s.Http, s.A, s.W1));
        }
        Assert.Equal(10, (await Stock.DocumentsAsync(s.Http, "?type=issue&status=posted")).Total());
        await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC52_R12_Deliveries_and_unlinked_issues_racing_for_the_same_stock_never_overdraw_it()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var work = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var order = await O.OrderedAsync(s, (s.A, 3, null, 1m));
            work.Add((await O.DraftDocumentAsync(s.Http, order, 1, 3)).Id());
            work.Add((await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 3))).Id());
        }

        var responses = await Task.WhenAll(work.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        Assert.Equal(Enumerable.Repeat(200, 3).Concat(Enumerable.Repeat(409, 5)).ToArray(), responses.Select(r => (int)r.StatusCode).Order().ToArray());
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
                await AssertShortAsync(response, "lines[0].quantity");
            response.Dispose();
        }
        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
    }

    // ---- AC-53 ----

    [Fact]
    public async Task AC53_The_customer_is_not_rechecked_at_posting()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await Orders.SetPartnerActiveAsync(s.Http, s.Cus.Id(), false);
        await O.RemoveRoleAsync(s.Http, s.Cus.Id());

        var posted = await O.FulfilAsync(s.Http, order, 1, 5);

        Assert.Equal("SI-000001", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (5m, 5m));
    }

    // ---- AC-54 ----

    [Fact]
    public async Task AC54_Delivered_equals_what_was_posted()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 200);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 60);
        var order = await O.OrderedAsync(s, (s.A, 10, s.Box, 60m), (s.B, 50, null, 2m), (s.A, 30, null, 2.5m));

        await O.FulfilAsync(s.Http, order, 1, 3, s.Box);                                            // line 1: 36
        await Stock.PostDocumentAsync(s.Http, (await O.CreateDocumentAsync(s.Http, s.W1, order.Id(),
            (s.B, 20, 2, null), (s.A, 12.5m, 3, null), (s.A, 1, 3, s.Box))).Id());                      // line 2: 20, line 3: 24.5
        var reversed = await O.FulfilAsync(s.Http, order, 2, 30);                                   // line 2: 50 …
        await Stock.ReverseAsync(s.Http, reversed.Id(), Stock.NextDay);                             // … and back to 20
        await O.FulfilAsync(s.Http, order, 1, 30);                                                  // line 1: 66
        await O.FulfilAsync(s.Http, order, 2, 0.000001m);                                           // line 2: 20.000001
        var left = await O.DraftDocumentAsync(s.Http, order, 3, 5);                                 // a draft: nothing
        var (_, aboveOrder) = await O.TryFulfilAsync(s.Http, order, 1, 5, s.Box);                   // 60 > 54: nothing
        await Stock.ConflictAsync(aboveOrder, "QUANTITY_EXCEEDS_ORDER");
        // Unlinked movements of the same articles; then the rest of line 2 is within the order but not in stock.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 7);
        await Stock.IssueAsync(s.Http, s.W1, s.B, 30);
        var (_, aboveStock) = await O.TryFulfilAsync(s.Http, order, 2, 29.999999m);                 // 9.999999 on hand: nothing
        await Stock.ConflictAsync(aboveStock, "INSUFFICIENT_STOCK");
        aboveOrder.Dispose();
        aboveStock.Dispose();

        var read = await O.GetAsync(s.Http, order.Id());
        var counted = (await O.DocumentsAsync(s.Http, order.Id(), "&status=posted"))
            .Where(d => d.GetProperty("reversalOf").ValueKind == JsonValueKind.Null).ToArray();
        var lines = new List<JsonElement>();
        foreach (var summary in counted)
            lines.AddRange((await Stock.GetAsync(s.Http, summary.Id())).DocumentLines());
        foreach (var orderLine in read.OrderLines())
        {
            var k = orderLine.GetProperty("lineNo").GetInt32();
            var sum = lines.Where(l => l.GetProperty("orderLineNo").GetInt32() == k).Sum(l => l.BaseQuantity());
            Assert.True(O.DoneOf(orderLine) == sum, $"Delivered({k}) is {O.DoneOf(orderLine)}, the posted lines sum to {sum}.");
            Assert.InRange(O.DoneOf(orderLine), 0m, orderLine.Dec("baseQuantity"));
            Assert.Equal(orderLine.Dec("baseQuantity") - sum, orderLine.Outstanding());
        }
        Assert.Equal(new[] { 66m, 20.000001m, 24.5m }, read.OrderLines().Select(O.DoneOf).ToArray());
        Assert.Equal(4, counted.Length);
        Assert.Equal("partial", O.ProgressOf(read));
        Assert.Equal("draft", (await Stock.GetAsync(s.Http, left.Id())).Str("status"));
        // 005/AC-60: for every pair the ledger sum equals stock on hand.
        var stock = await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
        Assert.Equal(200m + 7m - 66m - 24.5m, stock[(s.A, s.W1)]);
        Assert.Equal(60m - 20.000001m - 30m, stock[(s.B, s.W1)]);
        Assert.Equal(29.999999m, await O.OnHandAsync(s.Http, s.B, s.W1));
        Assert.Equal(54m + 5.5m, await O.OnHandAsync(s.Http, s.A, s.W1));
    }
}
