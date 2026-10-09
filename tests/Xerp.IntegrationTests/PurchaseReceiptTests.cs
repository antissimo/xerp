using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-40 to AC-47, AC-51 to AC-53 and AC-55 (goods receipt): a receipt linked to a purchase order
/// posts to the stock ledger like any receipt and raises the received quantity of the order lines it names —
/// in base units, never above what was ordered, and a refused posting changes nothing.
/// </summary>
[Collection(XerpCollection.Name)]
public class PurchaseReceiptTests(XerpFixture app)
{
    private static readonly OrderApi O = OrderApi.Purchase;

    private static async Task<JsonElement> AssertExceedsAsync(HttpResponseMessage response, params string[] exactKeys)
    {
        var problem = await Stock.ConflictAsync(response, "QUANTITY_EXCEEDS_ORDER", exactKeys);
        Assert.Equal(exactKeys.Order(StringComparer.Ordinal).ToArray(), McpAssert.ErrorKeys(problem));
        return problem;
    }

    // ---- AC-40 to AC-43 ----

    [Fact]
    public async Task AC40_A_linked_draft_receipt_names_the_order_and_moves_nothing()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 2.5m));
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "receipt", ["documentDate"] = "2026-10-10", ["warehouseId"] = s.W1.ToString(),
            ["purchaseOrderId"] = order.Id().ToString(),
            ["lines"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["articleId"] = s.A.ToString(), ["quantity"] = 4, ["orderLineNo"] = 1,
            }),
        };

        using var response = await Stock.PostAsync(s.Http, body);

        var draft = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(("receipt", "draft"), (draft.Str("type"), draft.Str("status")));
        JsonBody.AssertNull(draft, "number", "postedAt", "postedBy");
        var link = draft.GetProperty("purchaseOrder");
        Assert.Equal((order.Id(), "PO-000001"), (link.Id(), link.Str("number")));
        var line = Assert.Single(draft.DocumentLines());
        Assert.Equal(1, line.GetProperty("orderLineNo").GetInt32());
        Assert.Equal(4m, line.Quantity());
        McpAssert.JsonEqual(draft, await Stock.GetAsync(s.Http, draft.Id()));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await O.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC41_AC42_Posting_receives_into_stock_and_raises_the_received_quantity()
    {
        var s = await Orders.SetupAsync(app);
        var clerk = await Keys.CreateAsync(app, s.Http, "goods-in", "human");
        var order = await O.OrderedAsync(s, (s.A, 10, null, 2.5m));
        var draft = await O.DraftDocumentAsync(s.Http, order, 1, 4);

        using var response = await Stock.SendPostAsync(clerk.Client, draft.Id());

        // AC-41
        var posted = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(("posted", "SR-000001"), (posted.Str("status"), posted.Number()));
        Assert.Equal(clerk.Id, posted.GetProperty("postedBy").GetGuid());
        O.AssertLinked(posted, order);
        Assert.Equal(1, posted.DocumentLines()[0].GetProperty("orderLineNo").GetInt32());
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, draft.Id()));
        Assert.Equal(4m, entry.Quantity());
        Assert.Equal((s.A, s.W1), (entry.GetProperty("article").Id(), entry.GetProperty("warehouse").Id()));
        var after = await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m));
        // The order itself was not edited: only its progress moved (R17, R25).
        Assert.Equal(order.GetProperty("updatedAt").GetDateTimeOffset(), after.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(order.GetProperty("updatedBy").GetGuid(), after.GetProperty("updatedBy").GetGuid());
        foreach (var property in order.EnumerateObject().Where(p => p.Name is not ("lines" or "receiptStatus")))
            McpAssert.JsonEqual(property.Value, after.GetProperty(property.Name), $"Posting a receipt changed '{property.Name}' of the order");
        Assert.Equal(10m, after.OrderLines()[0].Dec("baseQuantity"));

        // AC-42
        var second = await O.FulfilAsync(s.Http, order, 1, 6);

        Assert.Equal("SR-000002", second.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await O.OnHandAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC43_Never_more_than_ordered_and_a_refused_posting_changes_nothing()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 2.5m));
        await O.FulfilAsync(s.Http, order, 1, 4);
        var before = await O.GetAsync(s.Http, order.Id());
        // R23: saving does not compare quantities.
        var draft = await O.DraftDocumentAsync(s.Http, order, 1, 7);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertExceedsAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await O.AssertUnchangedAsync(s.Http, before);
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(1, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft.Id()));

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 6, 1, null)));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        // The refused posting consumed no number.
        Assert.Equal("SR-000002", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_E10_The_limit_is_exact_to_the_last_base_unit()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));

        var (tooMuch, above) = await O.TryFulfilAsync(s.Http, order, 1, 10.000001m);
        await AssertExceedsAsync(above, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, tooMuch);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));

        var exact = await O.FulfilAsync(s.Http, order, 1, 10);
        var (_, further) = await O.TryFulfilAsync(s.Http, order, 1, 0.000001m);

        Assert.Equal("SR-000001", exact.Number());
        await AssertExceedsAsync(further, "lines[0].quantity");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        above.Dispose();
        further.Dispose();
    }

    [Fact]
    public async Task AC44_The_last_fraction_can_be_received()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 0.3m, null, 1m));

        await O.FulfilAsync(s.Http, order, 1, 0.1m);
        await O.FulfilAsync(s.Http, order, 1, 0.199999m);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (0.299999m, 0.000001m));
        var (_, above) = await O.TryFulfilAsync(s.Http, order, 1, 0.000002m);
        await AssertExceedsAsync(above, "lines[0].quantity");
        await O.FulfilAsync(s.Http, order, 1, 0.000001m);

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (0.3m, 0m));
        Assert.Equal(0.3m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        above.Dispose();
    }

    // ---- AC-45 ----

    [Fact]
    public async Task AC45_E11_Several_receipt_lines_for_one_order_line_are_summed()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 6, 1, null), (s.A, 5, 1, null));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertExceedsAsync(refused, "lines[0].quantity", "lines[1].quantity");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 6, 1, null), (s.A, 4, 1, null)));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal(new[] { 6m, 4m }, (await Stock.EntriesAsync(s.Http, posted.Id())).Select(e => e.Quantity()).ToArray());
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-46 ----

    [Fact]
    public async Task AC46_E12_Only_the_lines_of_the_exceeded_order_line_are_named_and_nothing_is_posted()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 2m), (s.A, 3, null, 0m));
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 3, 3, null), (s.B, 6, 2, null), (s.A, 2, 1, null));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertExceedsAsync(refused, "lines[1].quantity");
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m), (0m, 5m), (0m, 3m));

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 3, 3, null), (s.B, 5, 2, null), (s.A, 2, 1, null)));
        await Stock.PostDocumentAsync(s.Http, draft.Id());

        // The same article on order lines 1 and 3: the line number decides which one is received.
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (2m, 8m), (5m, 0m), (3m, 0m));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(8m, await O.OnHandAsync(s.Http, s.A, s.W1));

        await O.FulfilAsync(s.Http, order, 1, 8);

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m), (5m, 0m), (3m, 0m));
        Assert.Equal(13m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC46_R26_Two_exceeded_order_lines_name_all_their_document_lines()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 2m), (s.A, 3, null, 0m));
        // Line 3 (3 of A) is exceeded by lines 0 and 3 together, line 2 (5 of B) by line 1; line 1 is within.
        var draft = await O.CreateDocumentAsync(s.Http, s.W1, order.Id(),
            (s.A, 2, 3, null), (s.B, 5.5m, 2, null), (s.A, 10, 1, null), (s.A, 2, 3, null));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertExceedsAsync(refused, "lines[0].quantity", "lines[1].quantity", "lines[3].quantity");
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
    }

    // ---- AC-47 ----

    [Fact]
    public async Task AC47_E9_Only_base_quantities_are_compared_whatever_the_units()
    {
        var s = await Orders.SetupAsync(app);
        var inBoxes = await O.OrderedAsync(s, (s.A, 5, s.Box, 30m));
        Assert.Equal(60m, inBoxes.OrderLines()[0].Outstanding());

        var first = await O.FulfilAsync(s.Http, inBoxes, 1, 2, s.Box);

        Units.AssertLine(first.DocumentLines()[0], "box", quantity: 2m, factor: 12m, baseQuantity: 24m);
        Assert.Equal(24m, Assert.Single(await Stock.EntriesAsync(s.Http, first.Id())).Quantity());
        await O.AssertProgressAsync(s.Http, inBoxes.Id(), "confirmed", "partial", (24m, 36m));

        await O.FulfilAsync(s.Http, inBoxes, 1, 36);

        var full = await O.AssertProgressAsync(s.Http, inBoxes.Id(), "confirmed", "full", (60m, 0m));
        // The order line is still 5 boxes at 30: receipts change neither its unit nor its amount (R9).
        Units.AssertLine(full.OrderLines()[0], "box", quantity: 5m, factor: 12m, baseQuantity: 60m);
        Assert.Equal(150m, full.Dec("totalAmount"));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        // Ordered in pieces, received in boxes.
        var inPieces = await O.OrderedAsync(s, (s.A, 60, null, 1m));
        await O.FulfilAsync(s.Http, inPieces, 1, 5, s.Box);
        await O.AssertProgressAsync(s.Http, inPieces.Id(), "confirmed", "full", (60m, 0m));

        var another = await O.OrderedAsync(s, (s.A, 60, null, 1m));
        var (draft, above) = await O.TryFulfilAsync(s.Http, another, 1, 6, s.Box);
        await AssertExceedsAsync(above, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        await O.AssertProgressAsync(s.Http, another.Id(), "confirmed", "none", (0m, 60m));
        Assert.Equal(120m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        above.Dispose();
    }

    [Fact]
    public async Task AC47_E8_A_receipt_converts_with_the_factor_at_posting_and_the_order_keeps_its_own()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 5, s.Box, 30m));
        // Saved while 5 boxes are 60 pieces — exactly the order.
        var draft = await O.DraftDocumentAsync(s.Http, order, 1, 5, s.Box);

        await Units.SetAsync(s.Http, s.A, s.Box, 13);
        using var above = await Stock.SendPostAsync(s.Http, draft.Id());

        // 5 boxes are now 65 pieces; the order still expects 60 (R14, 007/R17).
        await AssertExceedsAsync(above, "lines[0].quantity");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 60m));

        await Units.SetAsync(s.Http, s.A, s.Box, 10);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Units.AssertLine(posted.DocumentLines()[0], "box", quantity: 5m, factor: 10m, baseQuantity: 50m);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (50m, 10m));
        // A later factor change moves neither what was received nor what was ordered.
        await Units.SetAsync(s.Http, s.A, s.Box, 100);
        var after = await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (50m, 10m));
        Units.AssertLine(after.OrderLines()[0], "box", quantity: 5m, factor: 12m, baseQuantity: 60m);
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-51 ----

    [Fact]
    public async Task AC51_Unlinked_documents_do_not_count_towards_the_order()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));

        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 1);
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 1);
        await Counts.PostedAsync(s.Http, s.W1, (s.A, 5, null));

        JsonBody.AssertNull(receipt, "purchaseOrder");
        JsonBody.AssertNull(Assert.Single(receipt.DocumentLines()), "orderLineNo");
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(10m, await O.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Empty(await O.DocumentsAsync(s.Http, order.Id()));
        // Requests of specs 005 to 008 behave as before: every unlinked document and line shows null.
        foreach (var summary in (await Stock.DocumentsAsync(s.Http)).Items())
        {
            var document = await Stock.GetAsync(s.Http, summary.Id());
            JsonBody.AssertNull(summary, "purchaseOrder");
            JsonBody.AssertNull(document, "purchaseOrder");
            Assert.All(document.DocumentLines(), l => JsonBody.AssertNull(l, "orderLineNo"));
        }
    }

    // ---- AC-52 ----

    [Fact]
    public async Task AC52_R30_Parallel_receipts_never_take_an_order_line_above_its_quantity()
    {
        var s = await Orders.SetupAsync(app);
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
        Assert.Equal(new[] { "SR-000001", "SR-000002", "SR-000003" }, numbers.Order(StringComparer.Ordinal).ToArray());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (9m, 1m));
        Assert.Equal(9m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(9m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        Assert.Equal(1m, await O.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(7, (await O.DocumentsAsync(s.Http, order.Id(), "&status=draft")).Length);
    }

    [Fact]
    public async Task AC52_R30_Parallel_receipts_for_two_order_lines_in_opposite_order_stay_within_both()
    {
        // Each receipt takes 4 of line 1 and 4 of line 2, half of them listing line 2 first; the order covers two.
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 10, null, 1m));
        var drafts = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var lines = i % 2 == 0
                ? new (Guid, decimal, int, Guid?)[] { (s.A, 4, 1, null), (s.B, 4, 2, null) }
                : [(s.B, 4, 2, null), (s.A, 4, 1, null)];
            drafts.Add((await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), lines)).Id());
        }

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var response in responses)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                await AssertExceedsAsync(response, "lines[0].quantity", "lines[1].quantity");
            response.Dispose();
        }
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (8m, 2m), (8m, 2m));
        Assert.Equal((8m, 8m), (await Stock.QuantityAsync(s.Http, s.A, s.W1), await Stock.QuantityAsync(s.Http, s.B, s.W1)));
    }

    [Fact]
    public async Task AC52_R30_A_posting_racing_with_a_close_precedes_it_or_finds_the_order_not_open()
    {
        var s = await Orders.SetupAsync(app);
        var outcomes = new List<int>();
        for (var round = 0; round < 8; round++)
        {
            var order = await O.OrderedAsync(s, (s.B, 10, null, 1m));
            var draft = await O.DraftDocumentAsync(s.Http, order, 1, 4);
            var stockBefore = await Stock.QuantityAsync(s.Http, s.B, s.W1);

            var posting = Task.Run(() => Stock.SendPostAsync(s.Http, draft.Id()));
            var closing = Task.Run(() => O.SendAsync(s.Http, order.Id(), "close"));
            using var post = await posting;
            using var close = await closing;

            await HttpAssert.JsonAsync(close, HttpStatusCode.OK);
            outcomes.Add((int)post.StatusCode);
            if (post.StatusCode == HttpStatusCode.OK)
            {
                await O.AssertProgressAsync(s.Http, order.Id(), "closed", "partial", (4m, 0m));
                Assert.Equal(stockBefore + 4m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
            }
            else
            {
                Assert.Equal(new[] { "purchaseOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(post, "ORDER_NOT_OPEN", "purchaseOrderId")));
                await O.AssertProgressAsync(s.Http, order.Id(), "closed", "none", (0m, 0m));
                await Stock.AssertDraftAsync(s.Http, draft.Id());
                Assert.Equal(stockBefore, await Stock.QuantityAsync(s.Http, s.B, s.W1));
            }
        }
        Assert.All(outcomes, status => Assert.True(status is 200 or 409, $"A posting racing with a close answered {status}."));
        await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC52_E13_Two_drafts_for_the_whole_outstanding_quantity_the_second_stays_a_draft()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var first = await O.DraftDocumentAsync(s.Http, order, 1, 10);
        var second = await O.DraftDocumentAsync(s.Http, order, 1, 10);

        await Stock.PostDocumentAsync(s.Http, first.Id());
        using var refused = await Stock.SendPostAsync(s.Http, second.Id());

        await AssertExceedsAsync(refused, "lines[0].quantity");
        await Stock.AssertUnchangedAsync(s.Http, second);
        // E14: against a fully received order nothing more posts; the draft can still be deleted.
        using var deleted = await Stock.DeleteAsync(s.Http, second.Id());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal("SR-000002", (await Stock.ReceiveAsync(s.Http, s.W1, s.B, 1)).Number());
    }

    // ---- AC-53 ----

    [Fact]
    public async Task AC53_The_supplier_is_not_rechecked_at_posting()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await Orders.SetPartnerActiveAsync(s.Http, s.Sup.Id(), false);
        await O.RemoveRoleAsync(s.Http, s.Sup.Id());

        // Saving and posting: goods on the way are received even from a partner deactivated since (R24).
        var posted = await O.FulfilAsync(s.Http, order, 1, 4);

        Assert.Equal("SR-000001", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m));
    }

    [Fact]
    public async Task AC53_R24_Order_of_checks_at_posting_masters_then_conversion_then_the_order_then_its_quantities()
    {
        var s = await Orders.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 1);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 1m));
        // Above the order on line 2; line 1 in a unit that will not convert; the article of line 2 inactive;
        // the warehouse inactive; the order closed.
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
        Assert.Equal(new[] { "purchaseOrderId" }, await RefusedAsync("ORDER_NOT_OPEN"));
        await O.ReopenAsync(s.Http, order.Id());
        Assert.Equal(new[] { "lines[1].quantity" }, await RefusedAsync("QUANTITY_EXCEEDS_ORDER"));
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());

        await Stock.ReplaceAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 0.000001m, 1, s.Pack), (s.B, 5, 2, null)));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        // None of the five refused postings consumed a number.
        Assert.Equal("SR-000001", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (0.000001m, 9.999999m), (5m, 0m));
    }

    [Fact]
    public async Task AC53_A_deactivated_warehouse_refuses_the_posting_as_for_any_receipt()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var draft = await O.DraftDocumentAsync(s.Http, order, 1, 4);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await HttpAssert.ReferenceInactiveAsync(refused, "warehouseId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
    }

    // ---- AC-55 ----

    [Fact]
    public async Task AC55_Received_equals_what_was_posted()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, s.Box, 30m), (s.B, 50, null, 2m), (s.A, 30, null, 2.5m));

        await O.FulfilAsync(s.Http, order, 1, 3, s.Box);                                            // line 1: 36
        await Stock.PostDocumentAsync(s.Http, (await O.CreateDocumentAsync(s.Http, s.W1, order.Id(),
            (s.B, 20, 2, null), (s.A, 12.5m, 3, null), (s.A, 1, 3, s.Box))).Id());                      // line 2: 20, line 3: 24.5
        var reversed = await O.FulfilAsync(s.Http, order, 2, 30);                                   // line 2: 50 …
        await Stock.ReverseAsync(s.Http, reversed.Id(), Stock.NextDay); // … and back to 20
        await O.FulfilAsync(s.Http, order, 1, 30);                                                  // line 1: 66
        await O.FulfilAsync(s.Http, order, 2, 0.000001m);                                           // line 2: 20.000001
        var left = await O.DraftDocumentAsync(s.Http, order, 3, 5);                                 // a draft: nothing
        var (_, refused) = await O.TryFulfilAsync(s.Http, order, 1, 5, s.Box);                      // 60 > 54: nothing
        await Stock.ConflictAsync(refused, "QUANTITY_EXCEEDS_ORDER");
        refused.Dispose();
        // Unlinked movements of the same articles.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 7);
        await Stock.IssueAsync(s.Http, s.W1, s.B, 3);

        var read = await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (66m, 54m), (20.000001m, 29.999999m), (24.5m, 5.5m));
        var counted = (await O.DocumentsAsync(s.Http, order.Id(), "&status=posted"))
            .Where(d => d.GetProperty("reversalOf").ValueKind == JsonValueKind.Null).ToArray();
        Assert.Equal(4, counted.Length);
        var lines = new List<JsonElement>();
        foreach (var summary in counted)
            lines.AddRange((await Stock.GetAsync(s.Http, summary.Id())).DocumentLines());
        foreach (var orderLine in read.OrderLines())
        {
            var k = orderLine.GetProperty("lineNo").GetInt32();
            var sum = lines.Where(l => l.GetProperty("orderLineNo").GetInt32() == k).Sum(l => l.BaseQuantity());
            Assert.True(O.DoneOf(orderLine) == sum, $"Received({k}) is {O.DoneOf(orderLine)}, the posted lines sum to {sum}.");
            Assert.InRange(O.DoneOf(orderLine), 0m, orderLine.Dec("baseQuantity"));
            Assert.Equal(orderLine.Dec("baseQuantity") - sum, orderLine.Outstanding());
        }
        Assert.Equal("draft", (await Stock.GetAsync(s.Http, left.Id())).Str("status"));
        // 005/AC-60: for every pair the ledger sum equals stock on hand.
        var stock = await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
        Assert.Equal(66m + 24.5m + 7m, stock[(s.A, s.W1)]);
        Assert.Equal(20.000001m - 3m, stock[(s.B, s.W1)]);
        Assert.Equal(54m + 5.5m, await O.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(29.999999m, await O.OnHandAsync(s.Http, s.B, s.W1));
    }
}
