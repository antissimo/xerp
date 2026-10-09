using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 010, AC-60 to AC-62 (close and reopen), AC-70 to AC-72 (reversal of a delivery), AC-75 to AC-79
/// (reserved and available quantity: reservation informs and does not block, ADR-0017), AC-95 (purchase to
/// sale) and what AC-34, AC-49 and AC-93 add to their mirrors of spec 009.
/// </summary>
[Collection(XerpCollection.Name)]
public class SalesOrderProgressTests(XerpFixture app)
{
    private static readonly OrderApi O = OrderApi.Sales;
    private static readonly OrderApi Po = OrderApi.Purchase;

    /// <summary>
    /// Asserts the four quantities of a pair in stock on hand (R19: every item has all four) and that
    /// <c>availableQuantity</c> is <c>quantity − reservedQuantity</c> (R16).
    /// </summary>
    private static async Task AssertOnHandAsync(HttpClient client, Guid article, Guid warehouse, decimal quantity, decimal reserved, decimal incoming = 0m)
    {
        var item = await Orders.OnHandItemAsync(client, article, warehouse);
        Assert.True(item is not null, $"The pair is not listed; expected quantity {quantity}, reserved {reserved}, incoming {incoming}.");
        var i = item.Value;
        var actual = (i.Quantity(), i.Dec("reservedQuantity"), i.Dec("availableQuantity"), i.Dec("incomingQuantity"));
        Assert.True(actual == (quantity, reserved, quantity - reserved, incoming),
            $"(quantity, reserved, available, incoming) is {actual}, expected {(quantity, reserved, quantity - reserved, incoming)}: {i}");
    }

    private static Task<decimal> ReservedAsync(OrderSetup s, Guid article, Guid warehouse) => O.OnHandAsync(s.Http, article, warehouse);

    private static Task<decimal> AvailableAsync(OrderSetup s, Guid article, Guid warehouse) =>
        Orders.OnHandAsync(s.Http, article, warehouse, "availableQuantity");

    // ---- AC-34, AC-49: what spec 010 adds to the mirrored criteria ----

    [Fact]
    public async Task AC34_SO_numbers_are_independent_of_SI_and_PO_numbers()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 1);
        await Po.OrderedAsync(s, (s.A, 1, null, 1m));
        await Po.OrderedAsync(s, (s.A, 1, null, 1m));

        var first = await O.OrderedAsync(s, (s.A, 5, null, 1m));
        var delivery = await O.FulfilAsync(s.Http, first, 1, 5);
        var second = await O.OrderedAsync(s, (s.A, 5, null, 1m));

        Assert.Equal(("SO-000001", "SO-000002"), (first.Number(), second.Number()));
        Assert.Equal("SI-000002", delivery.Number());
        Assert.Equal("PO-000003", (await Po.OrderedAsync(s, (s.A, 1, null, 1m))).Number());
        // A number of one kind is not a number of the other.
        using var poAsSo = await O.ByNumberAsync(s.Http, "PO-000001");
        using var soAsPo = await Po.ByNumberAsync(s.Http, "SO-000001");
        await HttpAssert.NotFoundAsync(poAsSo);
        await HttpAssert.NotFoundAsync(soAsPo);
    }

    [Fact]
    public async Task AC49_E11_R6_A_stock_document_has_at_most_one_order_link_and_each_link_its_own_kind_of_order()
    {
        var s = await Orders.SetupAsync(app);
        var sales = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var purchase = await Po.OrderedAsync(s, (s.A, 10, null, 1m));

        // Both links set: one of them is on the wrong type whatever the type is.
        foreach (var type in new[] { "issue", "receipt" })
        {
            using var both = await Stock.PostAsync(s.Http, O.Document(s.W1, sales.Id(), (s.A, 1, 1, null))
                .With("type", type).With("purchaseOrderId", purchase.Id().ToString()));
            var problem = await HttpAssert.ValidationAsync(both);
            Assert.Contains(type == "issue" ? "purchaseOrderId" : "salesOrderId", McpAssert.ErrorKeys(problem));
        }
        // An order of the other kind is not an order of this kind.
        using var purchaseAsSales = await Stock.PostAsync(s.Http, O.Document(s.W1, purchase.Id(), (s.A, 1, 1, null)));
        using var salesAsPurchase = await Stock.PostAsync(s.Http, Po.Document(s.W1, sales.Id(), (s.A, 1, 1, null)));
        Assert.Equal(new[] { "salesOrderId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(purchaseAsSales, "salesOrderId")));
        Assert.Equal(new[] { "purchaseOrderId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(salesAsPurchase, "purchaseOrderId")));
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());

        // One explicit null beside the other link is an ordinary linked document; each shows the other link as null.
        var delivery = await Stock.CreateAsync(s.Http, O.Document(s.W1, sales.Id(), (s.A, 1, 1, null)).With("purchaseOrderId", null));
        var receipt = await Stock.CreateAsync(s.Http, Po.Document(s.W1, purchase.Id(), (s.A, 1, 1, null)).With("salesOrderId", null));
        O.AssertLinked(delivery, sales);
        Po.AssertLinked(receipt, purchase);
        JsonBody.AssertNull(delivery, "purchaseOrder");
        JsonBody.AssertNull(receipt, "salesOrder");
        // The filters are separate.
        Assert.Equal(new[] { delivery.Id() }, (await O.DocumentsAsync(s.Http, sales.Id())).Select(d => d.Id()));
        Assert.Equal(new[] { receipt.Id() }, (await Po.DocumentsAsync(s.Http, purchase.Id())).Select(d => d.Id()));
        Assert.Empty(await O.DocumentsAsync(s.Http, purchase.Id()));
        // Neither link is part of a replace body.
        using var put = await Stock.PutAsync(s.Http, delivery.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 1, 1, null)).With("purchaseOrderId", null));
        await HttpAssert.ValidationAsync(put);
    }

    [Fact]
    public async Task AC27_E10_R3_A_partner_with_both_roles_is_supplier_on_a_purchase_order_and_customer_on_a_sales_order()
    {
        var s = await Orders.SetupAsync(app);
        var both = await Orders.PartnerAsync(s.Http, "BOTH", "Trader", isSupplier: true, isCustomer: true);

        var purchase = await Po.ConfirmAsync(s.Http, (await Po.CreateAsync(s.Http, Po.Body(both.Id(), s.W1, (s.A, 10, null, 1m)))).Id());
        var sales = await O.ConfirmAsync(s.Http, (await O.CreateAsync(s.Http, O.Body(both.Id(), s.W1, (s.A, 4, null, 2m)))).Id());

        Assert.Equal(both.Id(), purchase.GetProperty("supplier").Id());
        Assert.Equal(both.Id(), sales.GetProperty("customer").Id());
        // Each order checks only its own role.
        using var supAsCustomer = await O.PostAsync(s.Http, O.Body(s.Sup.Id(), s.W1, (s.A, 1, null, 1m)));
        using var cusAsSupplier = await Po.PostAsync(s.Http, Po.Body(s.Cus.Id(), s.W1, (s.A, 1, null, 1m)));
        await Stock.ConflictAsync(supAsCustomer, "PARTNER_ROLE_MISSING", "customerId");
        await Stock.ConflictAsync(cusAsSupplier, "PARTNER_ROLE_MISSING", "supplierId");
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 4m, incoming: 10m);
    }

    // ---- AC-60 to AC-62 ----

    [Fact]
    public async Task AC60_AC61_AC62_Close_ends_delivering_and_frees_the_reservation_and_reopen_restores_both()
    {
        var s = await Orders.SetupAsync(app);
        var closer = await Keys.CreateAsync(app, s.Http, "sales", "human");
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await O.FulfilAsync(s.Http, order, 1, 4);
        var before = await O.GetAsync(s.Http, order.Id());
        var early = await O.DraftDocumentAsync(s.Http, order, 1, 3);
        var other = await O.DraftDocumentAsync(s.Http, order, 1, 1);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 96m, reserved: 6m);

        // AC-60
        using var response = await O.SendAsync(closer.Client, order.Id(), "close");

        var closed = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("closed", closed.Str("status"));
        Assert.Equal(JsonValueKind.String, closed.GetProperty("closedAt").ValueKind);
        Assert.Equal(closer.Id, closed.GetProperty("closedBy").GetGuid());
        Assert.Equal("SO-000001", closed.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "closed", "partial", (4m, 0m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 96m, reserved: 0m);

        // AC-61
        using var post = await Stock.SendPostAsync(s.Http, early.Id());
        using var create = await Stock.PostAsync(s.Http, O.Document(s.W1, order.Id(), (s.A, 1, 1, null)));
        using var put = await Stock.PutAsync(s.Http, early.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 2, 1, null)));

        Assert.Equal(new[] { "salesOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(post, "ORDER_NOT_OPEN", "salesOrderId")));
        Assert.Equal(new[] { "salesOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(create, "ORDER_NOT_OPEN", "salesOrderId")));
        Assert.Equal(new[] { "salesOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(put, "ORDER_NOT_OPEN", "salesOrderId")));
        await Stock.AssertUnchangedAsync(s.Http, early);
        using var delete = await Stock.DeleteAsync(s.Http, other.Id());
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(96m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        // AC-62
        var reopened = await O.ReopenAsync(s.Http, order.Id());

        Assert.Equal("confirmed", reopened.Str("status"));
        JsonBody.AssertNull(reopened, "closedAt", "closedBy");
        McpAssert.JsonEqual(before, reopened, "Close and reopen changed the order");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 96m, reserved: 6m);

        var posted = await Stock.PostDocumentAsync(s.Http, early.Id());

        Assert.Equal("SI-000002", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (7m, 3m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 93m, reserved: 3m);
    }

    [Fact]
    public async Task AC60_R1_A_fully_delivered_order_can_be_closed_and_reopening_it_opens_nothing()
    {
        // 009/R15 mirrored (010-q.md, T-Q2): an order is closed whatever was delivered.
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 2, null, 1m));
        await Stock.PostDocumentAsync(s.Http, (await O.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.B, 2, 2, null), (s.A, 10, 1, null))).Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m), (2m, 0m));
        var before = await O.GetAsync(s.Http, order.Id());
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 90m, reserved: 0m);

        var closed = await O.CloseAsync(s.Http, order.Id());

        Assert.Equal("closed", closed.Str("status"));
        Assert.Equal(JsonValueKind.String, closed.GetProperty("closedAt").ValueKind);
        await O.AssertProgressAsync(s.Http, order.Id(), "closed", "full", (10m, 0m), (2m, 0m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 90m, reserved: 0m);
        await AssertOnHandAsync(s.Http, s.B, s.W1, quantity: 98m, reserved: 0m);
        using var onClosed = await Stock.PostAsync(s.Http, O.Document(s.W1, order.Id(), (s.A, 1, 1, null)));
        Assert.Equal(new[] { "salesOrderId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(onClosed, "ORDER_NOT_OPEN", "salesOrderId")));

        // A fully delivered order is not closed by itself, and reopening it reserves and opens nothing.
        var reopened = await O.ReopenAsync(s.Http, order.Id());

        McpAssert.JsonEqual(before, reopened, "Close and reopen changed the order");
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m), (2m, 0m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 90m, reserved: 0m);
        var (_, above) = await O.TryFulfilAsync(s.Http, order, 1, 1);
        await Stock.ConflictAsync(above, "QUANTITY_EXCEEDS_ORDER", "lines[0].quantity");
        above.Dispose();
        Assert.Equal(90m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-70 to AC-72 ----

    [Fact]
    public async Task AC70_R14_Reversing_a_delivery_brings_the_goods_back_and_reserves_them_again()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var delivery = await O.FulfilAsync(s.Http, order, 1, 4);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 96m, reserved: 6m);

        using var response = await Stock.SendReverseAsync(s.Http, delivery.Id(), Stock.NextDay);

        var reversing = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(("issue", "posted", "SI-000002"), (reversing.Str("type"), reversing.Str("status"), reversing.Number()));
        O.AssertLinked(reversing, order);
        JsonBody.AssertNull(reversing, "purchaseOrder");
        Assert.Equal(1, Assert.Single(reversing.DocumentLines()).GetProperty("orderLineNo").GetInt32());
        Assert.Equal(4m, Assert.Single(await Stock.EntriesAsync(s.Http, reversing.Id())).Quantity());
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 100m, reserved: 10m);

        // E8: the quantity can be delivered again.
        var again = await O.FulfilAsync(s.Http, order, 1, 10);

        Assert.Equal("SI-000003", again.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 90m, reserved: 0m);
        var (_, further) = await O.TryFulfilAsync(s.Http, order, 1, 1);
        await Stock.ConflictAsync(further, "QUANTITY_EXCEEDS_ORDER", "lines[0].quantity");
        further.Dispose();
    }

    [Fact]
    public async Task AC70_R14_Reversing_a_delivery_is_never_refused_for_stock()
    {
        // The goods come back: whatever happened to the stock since, the reversal only adds (006/R16).
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var delivery = await O.FulfilAsync(s.Http, order, 1, 10);
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.A, s.W1));

        await Stock.ReverseAsync(s.Http, delivery.Id());

        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 10m, reserved: 10m);
    }

    [Fact]
    public async Task AC71_A_delivery_against_an_order_closed_since_can_be_reversed()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var delivery = await O.FulfilAsync(s.Http, order, 1, 4);
        await O.FulfilAsync(s.Http, order, 1, 1);
        var closed = await O.CloseAsync(s.Http, order.Id());

        using var response = await Stock.SendReverseAsync(s.Http, delivery.Id());

        O.AssertLinked(await HttpAssert.JsonAsync(response, HttpStatusCode.Created), order);
        var after = await O.AssertProgressAsync(s.Http, order.Id(), "closed", "partial", (1m, 0m));
        McpAssert.JsonEqual(closed.GetProperty("closedAt"), after.GetProperty("closedAt"));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 99m, reserved: 0m);
        // After a reopen the reversed quantity is outstanding, and reserved, again.
        await O.ReopenAsync(s.Http, order.Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (1m, 9m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 99m, reserved: 9m);
    }

    [Fact]
    public async Task AC72_The_reversed_delivery_and_the_reversing_document_are_final()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var delivery = await O.FulfilAsync(s.Http, order, 1, 4);
        var reversing = await Stock.ReverseAsync(s.Http, delivery.Id());
        var original = await Stock.GetAsync(s.Http, delivery.Id());
        Assert.Equal("reversed", original.Str("status"));

        foreach (var document in new[] { original, reversing })
        {
            using var put = await Stock.PutAsync(s.Http, document.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 1, 1, null)));
            using var delete = await Stock.DeleteAsync(s.Http, document.Id());
            using var post = await Stock.SendPostAsync(s.Http, document.Id());
            using var reverse = await Stock.SendReverseAsync(s.Http, document.Id());
            foreach (var response in new[] { put, delete, post, reverse })
                await Stock.ConflictAsync(response, "INVALID_STATE");
            await Stock.AssertUnchangedAsync(s.Http, document);
        }
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-75 to AC-79 ----

    [Fact]
    public async Task AC75_AC76_A_confirmed_order_reserves_and_a_delivery_leaves_the_available_quantity_unchanged()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = await O.DraftAsync(s, (s.A, 30, null, 1m));

        // AC-75: a draft reserves nothing.
        Assert.Equal(0m, await ReservedAsync(s, s.A, s.W1));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 100m, reserved: 0m);

        var order = await O.ConfirmAsync(s.Http, draft.Id());

        var list = await Stock.OnHandAsync(s.Http, $"?articleId={s.A}");
        var item = Assert.Single(list.Items());
        Assert.Equal((100m, 30m, 70m, 0m),
            (item.Quantity(), item.Dec("reservedQuantity"), item.Dec("availableQuantity"), item.Dec("incomingQuantity")));
        Assert.Equal(s.W1, item.GetProperty("warehouse").Id());

        // AC-76
        await O.FulfilAsync(s.Http, order, 1, 12);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 88m, reserved: 18m);
        Assert.Equal(70m, await AvailableAsync(s, s.A, s.W1));

        await Stock.IssueAsync(s.Http, s.W1, s.A, 8);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 80m, reserved: 18m);
        Assert.Equal(62m, await AvailableAsync(s, s.A, s.W1));

        // R18: an unlinked receipt raises quantity and available, not reserved.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 85m, reserved: 18m);
    }

    [Fact]
    public async Task AC77_Reserved_is_the_sum_of_the_outstanding_base_quantities_of_confirmed_orders()
    {
        var s = await Orders.SetupAsync(app);
        var first = await O.OrderedAsync(s, (s.A, 30, null, 1m));
        var second = await O.OrderedAsync(s, (s.A, 2, s.Box, 50m), (s.A, 6, null, 1m));

        Assert.Equal(60m, await ReservedAsync(s, s.A, s.W1));

        await O.CloseAsync(s.Http, first.Id());
        Assert.Equal(30m, await ReservedAsync(s, s.A, s.W1));
        await O.ReopenAsync(s.Http, first.Id());
        Assert.Equal(60m, await ReservedAsync(s, s.A, s.W1));

        // An order shipping from W2 reserves in W2 only.
        await O.OrderedAsync(s, s.W2, (s.A, 7, null, 1m), (s.B, 3, null, 1m));
        Assert.Equal(60m, await ReservedAsync(s, s.A, s.W1));
        await AssertOnHandAsync(s.Http, s.A, s.W2, quantity: 0m, reserved: 7m);
        await AssertOnHandAsync(s.Http, s.B, s.W2, quantity: 0m, reserved: 3m);
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.B, s.W1));

        // A draft reserves nothing; the base quantity frozen at confirmation is what is reserved.
        await O.DraftAsync(s, (s.A, 1000, null, 1m));
        await Units.SetAsync(s.Http, s.A, s.Box, 20);
        Assert.Equal(60m, await ReservedAsync(s, s.A, s.W1));
        // A partial delivery lowers it by what was delivered.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await O.FulfilAsync(s.Http, second, 2, 6);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 4m, reserved: 54m);
        Assert.Equal(-50m, await AvailableAsync(s, s.A, s.W1));
    }

    [Fact]
    public async Task AC78_E2_Selling_before_buying()
    {
        var s = await Orders.SetupAsync(app);

        // R4: confirmation is never refused for stock reasons.
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m));

        Assert.Equal(("confirmed", "SO-000001"), (order.Str("status"), order.Number()));
        var listed = Assert.Single((await Stock.OnHandAsync(s.Http)).Items());
        Assert.Equal((s.A, s.W1), (listed.GetProperty("article").Id(), listed.GetProperty("warehouse").Id()));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 10m);
        Assert.Equal(-10m, await AvailableAsync(s, s.A, s.W1));

        var (delivery, refused) = await O.TryFulfilAsync(s.Http, order, 1, 10);
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity")));
        refused.Dispose();

        var purchase = await Po.OrderedAsync(s, (s.A, 10, null, 1m));
        // Incoming is not part of available (R16).
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 10m, incoming: 10m);
        using var stillShort = await Stock.SendPostAsync(s.Http, delivery);
        await Stock.ConflictAsync(stillShort, "INSUFFICIENT_STOCK");

        await Po.FulfilAsync(s.Http, purchase, 1, 10);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 10m, reserved: 10m, incoming: 0m);
        Assert.Equal(0m, await AvailableAsync(s, s.A, s.W1));

        var posted = await Stock.PostDocumentAsync(s.Http, delivery);

        Assert.Equal("SI-000001", posted.Number());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC78_R4_Orders_can_be_confirmed_for_more_than_is_on_hand_in_any_number()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        for (var i = 1; i <= 3; i++)
            Assert.Equal(O.Number(i), (await O.OrderedAsync(s, (s.A, 100, null, 1m))).Number());

        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 5m, reserved: 300m);
        Assert.Equal(-295m, await AvailableAsync(s, s.A, s.W1));
    }

    [Fact]
    public async Task AC79_R17_Reservation_informs_and_does_not_block()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 10);
        var order = await O.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 10, null, 1m));
        Assert.Equal(0m, await AvailableAsync(s, s.A, s.W1));

        // An unlinked issue takes what the order counts on.
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        // Likewise a transfer of reserved stock to W2.
        var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.B, 10);

        Assert.Equal(("posted", "posted"), (issue.Str("status"), transfer.Str("status")));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 10m);
        await AssertOnHandAsync(s.Http, s.B, s.W1, quantity: 0m, reserved: 10m);
        await AssertOnHandAsync(s.Http, s.B, s.W2, quantity: 10m, reserved: 0m);
        Assert.Equal(-10m, await AvailableAsync(s, s.A, s.W1));

        var (delivery, refused) = await O.TryFulfilAsync(s.Http, order, 1, 10);
        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, delivery);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m), (0m, 10m));
        refused.Dispose();

        // The delivery is refused only until stock is there.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.PostDocumentAsync(s.Http, delivery);
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (10m, 0m), (0m, 10m));
    }

    [Fact]
    public async Task AC79_R9_Of_two_orders_for_the_same_goods_the_first_delivery_posted_is_served()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var earlier = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        var later = await O.OrderedAsync(s, (s.A, 10, null, 1m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 10m, reserved: 20m);

        // The later order is delivered first: the earlier order's reservation plays no part.
        await O.FulfilAsync(s.Http, later, 1, 10);
        var (_, refused) = await O.TryFulfilAsync(s.Http, earlier, 1, 1);

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await O.AssertProgressAsync(s.Http, later.Id(), "confirmed", "full", (10m, 0m));
        await O.AssertProgressAsync(s.Http, earlier.Id(), "confirmed", "none", (0m, 10m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 10m);
        refused.Dispose();
    }

    [Fact]
    public async Task AC77_R19_Stock_on_hand_lists_a_pair_with_any_of_the_three_quantities_and_every_item_has_all_four()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W2, s.B, 5);                // only in stock
        await O.OrderedAsync(s, (s.A, 10, null, 1m));                  // only reserved
        await Po.OrderedAsync(s, s.W2, (s.A, 7, null, 1m));            // only incoming

        var list = await Stock.OnHandAsync(s.Http);

        Assert.Equal(3, list.Total());
        foreach (var item in list.Items())
        {
            foreach (var property in new[] { "quantity", "incomingQuantity", "reservedQuantity", "availableQuantity" })
                Assert.True(item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number,
                    $"A stock-on-hand item has no number '{property}': {item}");
            Assert.Equal(item.Quantity() - item.Dec("reservedQuantity"), item.Dec("availableQuantity"));
        }
        await AssertOnHandAsync(s.Http, s.B, s.W2, quantity: 5m, reserved: 0m);
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 10m);
        await AssertOnHandAsync(s.Http, s.A, s.W2, quantity: 0m, reserved: 0m, incoming: 7m);
        Assert.Null(await Orders.OnHandItemAsync(s.Http, s.B, s.W1));
        // Filters and paging count the pairs that are only on order.
        Assert.Equal(2, (await Stock.OnHandAsync(s.Http, $"?articleId={s.A}")).Total());
        Assert.Equal(1, (await Stock.OnHandAsync(s.Http, $"?warehouseId={s.W1}")).Total());
        var page = await Stock.OnHandAsync(s.Http, "?limit=2&offset=2");
        Assert.Equal((3, 1), (page.Total(), page.Items().Length));
        await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
    }

    // ---- AC-93: what spec 010 adds to the mirrored isolation criteria ----

    [Fact]
    public async Task AC93_T2_Reserved_and_available_are_computed_from_the_callers_own_orders_and_ledger()
    {
        var x = await Orders.SetupAsync(app);
        var y = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(x.Http, x.W1, x.A, 10);
        await Stock.ReceiveAsync(y.Http, y.W1, y.A, 10);
        var ofX = await O.OrderedAsync(x, (x.A, 10, null, 1m));

        // Y's article has the same code and Y's pair has stock: nothing of X's order shows.
        Assert.Equal(x.U.S.ArticleA.Str("code"), y.U.S.ArticleA.Str("code"));
        await AssertOnHandAsync(y.Http, y.A, y.W1, quantity: 10m, reserved: 0m);
        await AssertOnHandAsync(x.Http, x.A, x.W1, quantity: 10m, reserved: 10m);

        // Y's unlinked issue of its own stock is not refused and X's reservation is unchanged.
        await Stock.IssueAsync(y.Http, y.W1, y.A, 10);
        Assert.Null(await Orders.OnHandItemAsync(y.Http, y.A, y.W1));
        await AssertOnHandAsync(x.Http, x.A, x.W1, quantity: 10m, reserved: 10m);

        var ofY = await O.OrderedAsync(y, (y.A, 3, null, 1m));
        Assert.Equal(("SO-000001", "SO-000001"), (ofX.Number(), ofY.Number()));
        await AssertOnHandAsync(y.Http, y.A, y.W1, quantity: 0m, reserved: 3m);
        // Y has no stock; X's stock does not serve Y's delivery, and X's delivery is untouched by Y's order.
        var (_, refused) = await O.TryFulfilAsync(y.Http, ofY, 1, 3);
        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        refused.Dispose();
        await O.FulfilAsync(x.Http, ofX, 1, 10);
        await O.AssertProgressAsync(x.Http, ofX.Id(), "confirmed", "full", (10m, 0m));
        await O.AssertProgressAsync(y.Http, ofY.Id(), "confirmed", "none", (0m, 3m));
    }

    // ---- AC-95 ----

    [Fact]
    public async Task AC95_Purchase_to_sale()
    {
        var s = await Orders.SetupAsync(app);

        var purchase = await Po.OrderedAsync(s, (s.A, 5, s.Box, 30m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 0m, reserved: 0m, incoming: 60m);
        await Po.FulfilAsync(s.Http, purchase, 1, 5, s.Box);
        var sales = await O.OrderedAsync(s, (s.A, 40, null, 4.5m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 60m, reserved: 40m);
        await O.FulfilAsync(s.Http, sales, 1, 25);
        var second = await O.FulfilAsync(s.Http, sales, 1, 15);

        var ledger = await Stock.LedgerAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}&limit=500");
        Assert.Equal(new[] { 60m, -25m, -15m }, ledger.Items().Select(e => e.Quantity()).ToArray());
        Assert.Equal(20m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 20m, reserved: 0m, incoming: 0m);
        Assert.Equal(20m, await AvailableAsync(s, s.A, s.W1));
        await Po.AssertProgressAsync(s.Http, purchase.Id(), "confirmed", "full", (60m, 0m));
        var delivered = await O.AssertProgressAsync(s.Http, sales.Id(), "confirmed", "full", (40m, 0m));
        Assert.Equal(180m, delivered.Dec("totalAmount"));
        Assert.Equal(150m, (await Po.GetAsync(s.Http, purchase.Id())).Dec("totalAmount"));

        await Stock.ReverseAsync(s.Http, second.Id());

        Assert.Equal(35m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await O.AssertProgressAsync(s.Http, sales.Id(), "confirmed", "partial", (25m, 15m));
        await AssertOnHandAsync(s.Http, s.A, s.W1, quantity: 35m, reserved: 15m, incoming: 0m);
        Assert.Equal(20m, await AvailableAsync(s, s.A, s.W1));
        await Po.AssertProgressAsync(s.Http, purchase.Id(), "confirmed", "full", (60m, 0m));
        await Orders.AssertQuantityEqualsLedgerAsync(s.Http);
    }
}
