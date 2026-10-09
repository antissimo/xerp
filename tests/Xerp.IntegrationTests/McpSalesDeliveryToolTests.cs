using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 010, AC-86 and AC-88, the parts a delivery adds to the mirrored tool criteria (those run in
/// <see cref="McpSalesOrderToolTests"/>): the reserved and the available quantity through
/// <c>stock_on_hand_list</c>, reversal of a delivery, and a delivery refused for stock.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpSalesDeliveryToolTests(XerpFixture app)
{
    private static readonly OrderApi O = OrderApi.Sales;

    private sealed record Session(OrderSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await Orders.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-sales", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    [Fact]
    public async Task AC86_Sell_deliver_reverse_close_and_reopen_through_tools_only()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        var receipt = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("receipt", w1, (a, 100)));
        await s.Mcp.OkAsync("stock_document_post", new { id = receipt.Id() });

        var draft = await s.Mcp.OkAsync("sales_order_create", O.Body(s.S, (a, 40, null, 4.5m)));

        Assert.Equal(("draft", 180m), (draft.Str("status"), draft.Dec("totalAmount")));
        McpAssert.JsonEqual(await O.GetAsync(s.Http, draft.Id()), draft);

        var confirmed = await s.Mcp.OkAsync("sales_order_confirm", new { id = draft.Id() });

        Assert.Equal("SO-000001", confirmed.Number());
        Assert.Equal(s.Agent.Id, confirmed.GetProperty("confirmedBy").GetGuid());
        McpAssert.JsonEqual(await O.GetAsync(s.Http, draft.Id()), confirmed);

        var onHand = await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = a });

        var item = Assert.Single(onHand.Items());
        Assert.Equal((100m, 40m, 60m, 0m),
            (item.Quantity(), item.Dec("reservedQuantity"), item.Dec("availableQuantity"), item.Dec("incomingQuantity")));
        McpAssert.JsonEqual(await Stock.OnHandAsync(s.Http, $"?articleId={a}"), onHand);

        var delivery = await s.Mcp.OkAsync("stock_document_create", O.Document(w1, draft.Id(), (a, 25, 1, null)));
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = delivery.Id() });

        Assert.Equal("SI-000001", posted.Number());
        Assert.Equal(s.Agent.Id, posted.GetProperty("postedBy").GetGuid());
        O.AssertLinked(posted, confirmed);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, delivery.Id()), posted);

        var partial = await s.Mcp.OkAsync("sales_order_get", new { number = "SO-000001" });

        Assert.Equal("partial", partial.Str("deliveryStatus"));
        Assert.Equal((25m, 15m), (partial.OrderLines()[0].Dec("deliveredBaseQuantity"), partial.OrderLines()[0].Outstanding()));
        McpAssert.JsonEqual(await O.GetAsync(s.Http, draft.Id()), partial);
        var afterDelivery = Assert.Single((await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = a, warehouseId = w1 })).Items());
        Assert.Equal((75m, 15m, 60m), (afterDelivery.Quantity(), afterDelivery.Dec("reservedQuantity"), afterDelivery.Dec("availableQuantity")));

        var reversing = await s.Mcp.OkAsync("stock_document_reverse", new { id = delivery.Id(), documentDate = Stock.NextDay });

        Assert.Equal("SI-000002", reversing.Number());
        O.AssertLinked(reversing, confirmed);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, reversing.Id()), reversing);
        var none = await s.Mcp.OkAsync("sales_order_get", new { id = draft.Id() });
        Assert.Equal("none", none.Str("deliveryStatus"));
        McpAssert.JsonEqual(confirmed, none, "After the reversal the order is as it was confirmed");
        var linked = await s.Mcp.OkAsync("stock_document_list", new { salesOrderId = draft.Id() });
        Assert.Equal(2, linked.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, $"?salesOrderId={draft.Id()}"), linked);

        var closed = await s.Mcp.OkAsync("sales_order_close", new { id = draft.Id() });
        Assert.Equal("closed", closed.Str("status"));
        Assert.Equal(s.Agent.Id, closed.GetProperty("closedBy").GetGuid());
        McpAssert.JsonEqual(await O.GetAsync(s.Http, draft.Id()), closed);
        Assert.Equal(0m, Assert.Single((await s.Mcp.OkAsync("stock_on_hand_list")).Items()).Dec("reservedQuantity"));

        var reopened = await s.Mcp.OkAsync("sales_order_reopen", new { id = draft.Id() });
        McpAssert.JsonEqual(confirmed, reopened);
        McpAssert.JsonEqual(await O.GetAsync(s.Http, draft.Id()), reopened);
    }

    [Fact]
    public async Task AC88_A_delivery_above_stock_is_the_same_error_as_over_http_and_selling_before_buying_is_not_an_error()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await Stock.ReceiveAsync(s.Http, w1, a, 10);

        // Confirmation is not refused when stock is short (section 5).
        var draft = await s.Mcp.OkAsync("sales_order_create", O.Body(s.S, (a, 40, null, 1m)));
        var order = await s.Mcp.OkAsync("sales_order_confirm", new { id = draft.Id() });
        var available = Assert.Single((await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = a })).Items()).Dec("availableQuantity");
        Assert.Equal(-30m, available);

        var delivery = await s.Mcp.OkAsync("stock_document_create", O.Document(w1, order.Id(), (a, 11, 1, null)));
        using var http = await Stock.SendPostAsync(s.Http, delivery.Id());
        var problem = await HttpAssert.ProblemAsync(http, HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        var error = await s.Mcp.ErrorAsync("stock_document_post", new { id = delivery.Id() }, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(error));
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(error));

        // Above the order and above stock: the order answers first, through the tool as over HTTP.
        var above = await s.Mcp.OkAsync("stock_document_create", O.Document(w1, order.Id(), (a, 41, 1, null)));
        await s.Mcp.ErrorAsync("stock_document_post", new { id = above.Id() }, "QUANTITY_EXCEEDS_ORDER", "lines[0].quantity");

        await Stock.AssertDraftAsync(s.Http, delivery.Id());
        await O.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 40m));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, a, w1));
        // Both links in one call are refused like over HTTP.
        await s.Mcp.ErrorAsync("stock_document_create",
            O.Document(w1, order.Id(), (a, 1, 1, null)).With("purchaseOrderId", order.Id().ToString()), "VALIDATION_FAILED", "purchaseOrderId");
    }
}
