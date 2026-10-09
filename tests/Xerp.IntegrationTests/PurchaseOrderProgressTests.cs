using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-60 to AC-62 and AC-64 (close and reopen), AC-70 to AC-72 (reversal of a receipt) and AC-75 to
/// AC-77 (incoming quantity): what moves the outstanding quantity of a purchase order besides a receipt.
/// </summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderProgressTests(XerpFixture app)
{
    private static readonly OrderApi O = OrderApi.Purchase;

    // ---- AC-60 to AC-62 ----

    [Fact]
    public async Task AC60_AC61_AC62_Close_ends_receiving_and_reopen_resumes_it()
    {
        var s = await Orders.SetupAsync(app);
        var closer = await Keys.CreateAsync(app, s.Http, "buyer", "human");
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await O.FulfilAsync(s.Http, order, 1, 4);
        var before = await O.GetAsync(s.Http, order.Id());
        var early = await O.DraftDocumentAsync(s.Http, order, 1, 3);
        var other = await O.DraftDocumentAsync(s.Http, order, 1, 1);

        // AC-60 (E15: closing with draft receipts open is allowed)
        using var response = await O.SendAsync(closer.Client, order.Id(), "close");

        var closed = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("closed", closed.Str("status"));
        Assert.Equal(JsonValueKind.String, closed.GetProperty("closedAt").ValueKind);
        Assert.Equal(closer.Id, closed.GetProperty("closedBy").GetGuid());
        Assert.Equal("PO-000001", closed.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "closed", "partial", (4m, 0m));
        foreach (var property in before.EnumerateObject().Where(p => p.Name is not ("status" or "closedAt" or "closedBy" or "lines")))
            McpAssert.JsonEqual(property.Value, closed.GetProperty(property.Name), $"Close changed '{property.Name}'");
        foreach (var (was, now) in before.OrderLines().Zip(closed.OrderLines()))
            foreach (var property in was.EnumerateObject().Where(p => p.Name != "outstandingBaseQuantity"))
                McpAssert.JsonEqual(property.Value, now.GetProperty(property.Name), $"Close changed '{property.Name}' of a line");
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await O.OnHandAsync(s.Http, s.A, s.W1));

        // AC-61
        using var post = await Stock.SendPostAsync(s.Http, early.Id());
        using var create = await Stock.PostAsync(s.Http, O.Document(s.W1, order.Id(), (s.A, 1, 1, null)));
        using var put = await Stock.PutAsync(s.Http, early.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 2, 1, null)));

        Assert.Equal(new[] { "purchaseOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(post, "ORDER_NOT_OPEN", "purchaseOrderId")));
        Assert.Equal(new[] { "purchaseOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(create, "ORDER_NOT_OPEN", "purchaseOrderId")));
        Assert.Equal(new[] { "purchaseOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(put, "ORDER_NOT_OPEN", "purchaseOrderId")));
        await Stock.AssertDraftAsync(s.Http, early.Id());
        await Stock.AssertUnchangedAsync(s.Http, early);
        O.AssertLinked(await Stock.GetAsync(s.Http, other.Id()), closed);
        using var delete = await Stock.DeleteAsync(s.Http, other.Id());
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(new[] { early.Id() }, (await O.DocumentsAsync(s.Http, order.Id(), "&status=draft")).Select(d => d.Id()));
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // Unlinked receipts into the same warehouse are not the order's business.
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 1);

        // AC-62
        var reopened = await O.ReopenAsync(s.Http, order.Id());

        Assert.Equal("confirmed", reopened.Str("status"));
        JsonBody.AssertNull(reopened, "closedAt", "closedBy");
        McpAssert.JsonEqual(before, reopened, "Close and reopen changed the order");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m));
        Assert.Equal(6m, await O.OnHandAsync(s.Http, s.A, s.W1));

        var posted = await Stock.PostDocumentAsync(s.Http, early.Id());

        Assert.Equal("SR-000003", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (7m, 3m));
        Assert.Equal(7m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-64 ----

    [Fact]
    public async Task AC64_An_order_can_be_closed_whatever_was_received()
    {
        var s = await Orders.SetupAsync(app);
        var nothing = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 2, null, 1m));
        var everything = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 2, null, 1m));
        await Stock.PostDocumentAsync(s.Http, (await O.CreateDocumentAsync(s.Http, s.W1, everything.Id(), (s.B, 2, 2, null), (s.A, 10, 1, null))).Id());
        await O.AssertProgressAsync(s.Http, everything.Id(), "confirmed", "full", (10m, 0m), (2m, 0m));

        await O.CloseAsync(s.Http, nothing.Id());
        await O.CloseAsync(s.Http, everything.Id());

        // A confirmed order closed with nothing received is a cancelled order (ADR-0016, decision 2).
        await O.AssertProgressAsync(s.Http, nothing.Id(), "closed", "none", (0m, 0m), (0m, 0m));
        await O.AssertProgressAsync(s.Http, everything.Id(), "closed", "full", (10m, 0m), (2m, 0m));
        Assert.Equal(0m, await O.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // R29: a fully received order is not closed by itself, and reopening it opens nothing.
        await O.ReopenAsync(s.Http, everything.Id());
        await O.AssertProgressAsync(s.Http, everything.Id(), "confirmed", "full", (10m, 0m), (2m, 0m));
        var (_, above) = await O.TryFulfilAsync(s.Http, everything, 1, 1);
        await Stock.ConflictAsync(above, "QUANTITY_EXCEEDS_ORDER", "lines[0].quantity");
        above.Dispose();
    }

    // ---- AC-70 to AC-72 ----

    [Fact]
    public async Task AC70_Reversing_a_receipt_gives_the_quantity_back_to_the_order()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var receipt = await O.FulfilAsync(s.Http, order, 1, 4);

        using var response = await Stock.SendReverseAsync(s.Http, receipt.Id());

        var reversing = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(order.Id(), reversing.GetProperty("purchaseOrder").Id());
        Assert.Equal(1, Assert.Single(reversing.DocumentLines()).GetProperty("orderLineNo").GetInt32());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var again = await O.FulfilAsync(s.Http, order, 1, 10);

        Assert.Equal("SR-000003", again.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC70_R32_R33_The_reversing_document_carries_the_link_and_the_original_no_longer_counts()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 1m));
        var receipt = await Stock.PostDocumentAsync(s.Http,
            (await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 4, 1, null), (s.B, 5, 2, null))).Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m), (5m, 0m));

        using var response = await Stock.SendReverseAsync(s.Http, receipt.Id(), Stock.NextDay);

        var reversing = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(("receipt", "posted", "SR-000002"), (reversing.Str("type"), reversing.Str("status"), reversing.Number()));
        O.AssertLinked(reversing, order);
        Assert.Equal([1, 2], reversing.DocumentLines().Select(l => l.GetProperty("orderLineNo").GetInt32()));
        Stock.AssertLink(reversing, "reversalOf", receipt);
        var original = await Stock.GetAsync(s.Http, receipt.Id());
        Assert.Equal("reversed", original.Str("status"));
        O.AssertLinked(original, order);
        Assert.Equal(new[] { -4m, -5m }, (await Stock.EntriesAsync(s.Http, reversing.Id())).Select(e => e.Quantity()).ToArray());
        // The reversing document is posted and names the order lines, but it is not a receipt of goods (R25).
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m), (0m, 5m));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(10m, await O.OnHandAsync(s.Http, s.A, s.W1));

        // E14: the quantity can be received again.
        await O.FulfilAsync(s.Http, order, 1, 10);
        await O.FulfilAsync(s.Http, order, 2, 5);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m), (5m, 0m));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // 006/R18: neither the reversed original nor the reversing document can be reversed again.
        using var again = await Stock.SendReverseAsync(s.Http, receipt.Id());
        using var ofReversing = await Stock.SendReverseAsync(s.Http, reversing.Id());
        await Stock.ConflictAsync(again, "INVALID_STATE");
        await Stock.ConflictAsync(ofReversing, "INVALID_STATE");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m), (5m, 0m));
    }

    [Fact]
    public async Task AC70_Only_the_reversed_receipt_stops_counting()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 5, s.Box, 30m));
        var first = await O.FulfilAsync(s.Http, order, 1, 2, s.Box);
        var second = await O.FulfilAsync(s.Http, order, 1, 30);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (54m, 6m));

        // The factor changes before the reversal: the reversal undoes the 24 that were posted (006, 007/R17).
        await Units.SetAsync(s.Http, s.A, s.Box, 10);
        await Stock.ReverseAsync(s.Http, first.Id());

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (30m, 30m));
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal("posted", (await Stock.GetAsync(s.Http, second.Id())).Str("status"));
        await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC71_R34_A_receipt_against_an_order_closed_since_can_be_reversed()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var receipt = await O.FulfilAsync(s.Http, order, 1, 4);
        await O.FulfilAsync(s.Http, order, 1, 1);
        var closed = await O.CloseAsync(s.Http, order.Id());

        using var response = await Stock.SendReverseAsync(s.Http, receipt.Id());

        var reversing = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        O.AssertLinked(reversing, order);
        var after = await O.AssertProgressAsync(s.Http, order.Id(), "closed", "partial", (1m, 0m));
        // The reversal did not change the order's status or who closed it.
        McpAssert.JsonEqual(closed.GetProperty("closedAt"), after.GetProperty("closedAt"));
        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await O.OnHandAsync(s.Http, s.A, s.W1));
        // After a reopen the reversed quantity is outstanding again.
        await O.ReopenAsync(s.Http, order.Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (1m, 9m));
    }

    [Fact]
    public async Task AC72_A_reversal_that_would_make_stock_negative_is_refused_and_the_order_keeps_its_progress()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var receipt = await O.FulfilAsync(s.Http, order, 1, 10);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 6);

        using var refused = await Stock.SendReverseAsync(s.Http, receipt.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Equal("posted", (await Stock.GetAsync(s.Http, receipt.Id())).Str("status"));
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await O.OnHandAsync(s.Http, s.A, s.W1));
        // Once the goods are back the reversal goes through.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 6);
        await Stock.ReverseAsync(s.Http, receipt.Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
    }

    // ---- AC-75 to AC-77 ----

    private static async Task AssertOnHandAsync(OrderSetup s, Guid article, Guid warehouse, decimal quantity, decimal incoming)
    {
        var item = await Orders.OnHandItemAsync(s.Http, article, warehouse);
        Assert.True(item is not null, $"The pair is not listed; expected quantity {quantity}, incoming {incoming}.");
        var actual = (item.Value.Quantity(), item.Value.Dec("incomingQuantity"));
        Assert.True(actual == (quantity, incoming), $"(quantity, incomingQuantity) is {actual}, expected {(quantity, incoming)}: {item}");
    }

    [Fact]
    public async Task AC75_A_confirmed_order_is_incoming_until_it_is_received()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));

        var list = await Stock.OnHandAsync(s.Http, $"?articleId={s.A}");
        var item = Assert.Single(list.Items());
        Assert.Equal(1, list.Total());
        Assert.Equal((0m, 10m), (item.Quantity(), item.Dec("incomingQuantity")));
        Assert.Equal((s.A, s.W1), (item.GetProperty("article").Id(), item.GetProperty("warehouse").Id()));

        await O.FulfilAsync(s.Http, order, 1, 4);
        await AssertOnHandAsync(s, s.A, s.W1, quantity: 4m, incoming: 6m);

        await O.FulfilAsync(s.Http, order, 1, 6);
        await AssertOnHandAsync(s, s.A, s.W1, quantity: 10m, incoming: 0m);
        Assert.Equal(1, (await Stock.OnHandAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC76_Incoming_is_the_sum_of_the_outstanding_base_quantities_of_confirmed_orders()
    {
        var s = await Orders.SetupAsync(app);
        var first = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await O.OrderedAsync(s, (s.A, 5, s.Box, 30m), (s.A, 2, null, 1m));

        Assert.Equal(72m, await O.OnHandAsync(s.Http, s.A, s.W1));

        // A draft adds nothing.
        var draft = await O.DraftAsync(s, (s.A, 1000, null, 1m));
        Assert.Equal(72m, await O.OnHandAsync(s.Http, s.A, s.W1));

        await O.CloseAsync(s.Http, first.Id());
        Assert.Equal(62m, await O.OnHandAsync(s.Http, s.A, s.W1));
        await O.ReopenAsync(s.Http, first.Id());
        Assert.Equal(72m, await O.OnHandAsync(s.Http, s.A, s.W1));

        // An order into W2 counts for W2 only.
        await O.OrderedAsync(s, s.W2, (s.A, 7, null, 1m), (s.B, 3, null, 1m));
        Assert.Equal(72m, await O.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(7m, await O.OnHandAsync(s.Http, s.A, s.W2));
        Assert.Equal(3m, await O.OnHandAsync(s.Http, s.B, s.W2));
        Assert.Equal(0m, await O.OnHandAsync(s.Http, s.B, s.W1));

        // The draft counts from its confirmation on.
        await O.ConfirmAsync(s.Http, draft.Id());
        Assert.Equal(1072m, await O.OnHandAsync(s.Http, s.A, s.W1));
        // Stock itself is untouched by all of this (R35).
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.All((await Stock.OnHandAsync(s.Http)).Items(), i => Assert.Equal(0m, i.Quantity()));
    }

    [Fact]
    public async Task AC76_E8_Incoming_uses_the_base_quantity_frozen_at_confirmation()
    {
        var s = await Orders.SetupAsync(app);
        await O.OrderedAsync(s, (s.A, 5, s.Box, 30m));

        await Units.SetAsync(s.Http, s.A, s.Box, 20);

        Assert.Equal(60m, await O.OnHandAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC77_Stock_on_hand_lists_a_pair_with_stock_or_incoming_quantity_and_every_item_has_both()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await Stock.ReceiveAsync(s.Http, s.W2, s.B, 5);

        var list = await Stock.OnHandAsync(s.Http);

        // (A, W1) only on order; (B, W2) only in stock; the other pairs are not listed.
        Assert.Equal(2, list.Total());
        Assert.Equal(new[] { (s.A, s.W1, 0m, 10m), (s.B, s.W2, 5m, 0m) }.OrderBy(p => p.Item1),
            list.Items().Select(i => (i.GetProperty("article").Id(), i.GetProperty("warehouse").Id(), i.Quantity(), i.Dec("incomingQuantity")))
                .OrderBy(p => p.Item1));
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.B, s.W1));
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.A, s.W2));
        // Filters and paging count the pair that is only on order.
        Assert.Equal(1, (await Stock.OnHandAsync(s.Http, $"?warehouseId={s.W1}")).Total());
        Assert.Equal(1, (await Stock.OnHandAsync(s.Http, $"?articleId={s.A}")).Total());
        var page = await Stock.OnHandAsync(s.Http, "?limit=1&offset=1");
        Assert.Equal((2, 1), (page.Total(), page.Items().Length));

        // After a receipt is reversed, incoming rises by its quantity.
        var receipt = await O.FulfilAsync(s.Http, order, 1, 4);
        await AssertOnHandAsync(s, s.A, s.W1, quantity: 4m, incoming: 6m);
        await Stock.ReverseAsync(s.Http, receipt.Id());
        await AssertOnHandAsync(s, s.A, s.W1, quantity: 0m, incoming: 10m);

        // Closed: neither stock nor incoming, so the pair is gone.
        await O.CloseAsync(s.Http, order.Id());
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.A, s.W1));
        Assert.Equal(1, (await Stock.OnHandAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC77_A_fully_received_and_issued_pair_is_no_longer_listed()
    {
        var s = await Orders.SetupAsync(app);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await O.FulfilAsync(s.Http, order, 1, 10);
        await AssertOnHandAsync(s, s.A, s.W1, quantity: 10m, incoming: 0m);

        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);

        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
    }
}
