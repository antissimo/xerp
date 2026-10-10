using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-30 to AC-35 (R8–R10, E7–E10): a receipt, an issue, a count, a purchase order and a sales order
/// created without <c>warehouseId</c> land on the tenant's default warehouse of that moment; a document linked
/// to an order lands on the order's warehouse; replace and transfers still have to name the warehouse.
/// </summary>
[Collection(XerpCollection.Name)]
public class DocumentDefaultWarehouseTests(XerpFixture app)
{
    private static readonly OrderApi Po = OrderApi.Purchase;
    private static readonly OrderApi So = OrderApi.Sales;

    private static Guid WarehouseOf(JsonElement document) => document.GetProperty("warehouse").Id();

    /// <summary>The create body of an order of the kind without <c>warehouseId</c> (or with <c>null</c>).</summary>
    private static JsonObject OrderBody(OrderApi o, OrderSetup s, bool asNull, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        o.Body(s, lines).NoWarehouse(asNull);

    // ---- AC-30 ----

    [Theory]
    [InlineData("receipt", false)]
    [InlineData("receipt", true)]
    [InlineData("issue", false)]
    [InlineData("issue", true)]
    [InlineData("count", false)]
    [InlineData("count", true)]
    public async Task AC30_A_stock_document_created_without_warehouseId_is_on_the_default_warehouse(string type, bool asNull)
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultAsync(s.Http);
        Assert.NotEqual(s.W1, dw.Id());
        Assert.NotEqual(s.W2, dw.Id());

        using var response = await Stock.PostAsync(s.Http, Stock.Draft(type, s.W1, (s.A, 10)).NoWarehouse(asNull));
        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.Equal(type, created.Str("type"));
        Assert.Equal("draft", created.Str("status"));
        Assert.Equal(dw.Id(), WarehouseOf(created));
        Assert.Equal("CENTRAL", created.GetProperty("warehouse").Str("code"));
        Assert.Equal("Central warehouse", created.GetProperty("warehouse").Str("name"));
        McpAssert.JsonEqual(created, await Stock.GetAsync(s.Http, created.Id()), "GET differs from the create response");
        var listed = (await Stock.DocumentsAsync(s.Http, $"?warehouseId={dw.Id()}")).Items();
        Assert.Equal(created.Id(), Assert.Single(listed).Id());
    }

    [Theory]
    [InlineData("purchase", false)]
    [InlineData("purchase", true)]
    [InlineData("sales", false)]
    [InlineData("sales", true)]
    public async Task AC30_An_order_created_without_warehouseId_is_on_the_default_warehouse(string kind, bool asNull)
    {
        var o = kind == "purchase" ? Po : So;
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);

        using var response = await o.PostAsync(s.Http, OrderBody(o, s, asNull, (s.A, 10, null, 1m)));
        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.Equal(dw, WarehouseOf(created));
        Assert.Equal("CENTRAL", created.GetProperty("warehouse").Str("code"));
        McpAssert.JsonEqual(created, await o.GetAsync(s.Http, created.Id()), "GET differs from the create response");
    }

    [Fact]
    public async Task AC30_A_receipt_without_warehouseId_posts_into_the_default_warehouse()
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        var draft = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 10)).NoWarehouse());

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal(dw, WarehouseOf(posted));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, dw));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id()));
        Assert.Equal(dw, entry.GetProperty("warehouse").Id());
        Assert.Equal(10m, entry.Quantity());
        Assert.Equal(10m, await Balance.ListedQuantityAsync(s.Http, s.A, dw));
        await Balance.AssertBalancedAsync(s.U.S, "after the receipt into DW", dw);
    }

    [Fact]
    public async Task AC30_E1_In_a_new_tenant_a_receipt_can_be_created_and_posted_before_any_warehouse_was_created()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = await Uom.CreateAsync(http, "pcs", "Piece");
        var a = await Art.CreateAsync(http, "A", "Article A", unit.Id());
        var body = new JsonObject { ["type"] = "receipt", ["documentDate"] = Stock.Date, ["lines"] = Stock.Lines((a.Id(), 7)) };

        var draft = await Stock.CreateAsync(http, body);
        var posted = await Stock.PostDocumentAsync(http, draft.Id());

        var dw = await Balance.DefaultIdAsync(http);
        Assert.Equal(dw, WarehouseOf(posted));
        Assert.Equal(1, (await MasterApi.Warehouses.ListAsync(http)).Total());
        Assert.Equal(7m, await Stock.QuantityAsync(http, a.Id(), dw));
    }

    [Fact]
    public async Task AC30_An_issue_and_a_count_without_warehouseId_work_on_the_stock_of_the_default_warehouse()
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        await Stock.ReceiveAsync(s.Http, dw, s.A, 10);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 50);

        var issue = await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 4)).NoWarehouse());
        await Stock.PostDocumentAsync(s.Http, issue.Id());
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.A, dw));
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        // The count takes its book quantity from the warehouse it landed on (008/R5): 6 in DW, not 50 in W1.
        var count = await Stock.CreateAsync(s.Http, Counts.Draft(s.W1, (s.A, 9, null)).NoWarehouse());
        Assert.Equal(dw, WarehouseOf(count));
        Assert.Equal(6m, count.DocumentLines()[0].Book());
        await Stock.PostDocumentAsync(s.Http, count.Id());
        Assert.Equal(9m, await Stock.QuantityAsync(s.Http, s.A, dw));
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Balance.AssertBalancedAsync(s.U.S, "after issue and count in DW", dw);
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_E7_The_default_at_the_moment_of_creation_counts()
    {
        var s = await Orders.SetupAsync(app);
        var former = await Balance.DefaultIdAsync(s.Http);
        var draft = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 10)).NoWarehouse());
        var order = await Po.CreateAsync(s.Http, OrderBody(Po, s, false, (s.A, 5, null, 1m)));
        Assert.Equal(former, WarehouseOf(draft));

        await Balance.SetDefaultAsync(s.Http, s.W1);

        await Stock.AssertUnchangedAsync(s.Http, draft);
        await Po.AssertUnchangedAsync(s.Http, order);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        Assert.Equal(former, WarehouseOf(posted));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, former));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var second = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W2, (s.A, 3)).NoWarehouse());
        Assert.Equal(s.W1, WarehouseOf(second));
        await Stock.PostDocumentAsync(s.Http, second.Id());
        Assert.Equal(3m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, former));
        Assert.Equal(s.W1, WarehouseOf(await So.CreateAsync(s.Http, OrderBody(So, s, true, (s.A, 1, null, 1m)))));
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_Naming_a_warehouse_is_unchanged()
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);

        var named = await Stock.CreateAsync(s.Http, "receipt", s.W2, (s.A, 1));
        Assert.Equal(s.W2, WarehouseOf(named));
        Assert.Equal(s.W2, WarehouseOf(await Po.CreateAsync(s.Http, Po.Body(Po.PartnerOf(s).Id(), s.W2, (s.A, 1, null, 1m)))));

        using var unknown = await Stock.PostAsync(s.Http, Stock.Draft("receipt", Guid.NewGuid(), (s.A, 1)));
        await HttpAssert.ReferenceNotFoundAsync(unknown, "warehouseId");

        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);
        using var inactive = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W2, (s.A, 1)));
        await HttpAssert.ReferenceInactiveAsync(inactive, "warehouseId");

        // Nothing was created on the default instead.
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http, $"?warehouseId={dw}")).Total());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public async Task AC32_E8_A_malformed_warehouseId_is_still_400(string value)
    {
        var s = await Orders.SetupAsync(app);

        using var document = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("warehouseId", value));
        await HttpAssert.ValidationAsync(document, "warehouseId");
        using var count = await Stock.PostAsync(s.Http, Stock.Draft("count", s.W1, (s.A, 1)).With("warehouseId", value));
        await HttpAssert.ValidationAsync(count, "warehouseId");
        using var purchase = await Po.PostAsync(s.Http, Po.Body(s, (s.A, 1, null, 1m)).With("warehouseId", value));
        await HttpAssert.ValidationAsync(purchase, "warehouseId");
        using var sales = await So.PostAsync(s.Http, So.Body(s, (s.A, 1, null, 1m)).With("warehouseId", value));
        await HttpAssert.ValidationAsync(sales, "warehouseId");

        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
        Assert.Equal(0, (await Po.ListAsync(s.Http)).Total());
        Assert.Equal(0, (await So.ListAsync(s.Http)).Total());
    }

    // ---- AC-33 ----

    [Theory]
    [InlineData("receipt", false)]
    [InlineData("receipt", true)]
    [InlineData("issue", false)]
    [InlineData("count", true)]
    public async Task AC33_Replace_of_a_draft_stock_document_must_name_the_warehouse(string type, bool asNull)
    {
        var s = await Orders.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, Stock.Draft(type, s.W1, (s.A, 1)).NoWarehouse());

        using var response = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 2)).NoWarehouse(asNull));

        await HttpAssert.ValidationAsync(response, "warehouseId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
    }

    [Theory]
    [InlineData("purchase", false)]
    [InlineData("purchase", true)]
    [InlineData("sales", false)]
    [InlineData("sales", true)]
    public async Task AC33_Replace_of_a_draft_order_must_name_the_warehouse(string kind, bool asNull)
    {
        var o = kind == "purchase" ? Po : So;
        var s = await Orders.SetupAsync(app);
        var draft = await o.CreateAsync(s.Http, OrderBody(o, s, false, (s.A, 1, null, 1m)));

        using var response = await o.PutAsync(s.Http, draft.Id(), o.Replacement(s, (s.A, 2, null, 1m)).NoWarehouse(asNull));

        await HttpAssert.ValidationAsync(response, "warehouseId");
        await o.AssertUnchangedAsync(s.Http, draft);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AC33_E9_A_transfer_must_name_both_warehouses(bool toTheDefault, bool asNull)
    {
        var s = await Orders.SetupAsync(app);
        var to = toTheDefault ? await Balance.DefaultIdAsync(s.Http) : s.W2;

        using var response = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, to, (s.A, 1)).NoWarehouse(asNull));

        var problem = await HttpAssert.ValidationAsync(response, "warehouseId");
        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC33_Replace_of_a_draft_transfer_must_name_the_warehouse()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1));

        using var response = await Stock.PutAsync(s.Http, draft.Id(), Stock.TransferReplacement(s.W1, s.W2, (s.A, 2)).NoWarehouse());

        await HttpAssert.ValidationAsync(response, "warehouseId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
    }

    // ---- AC-34 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC34_E10_A_goods_receipt_without_warehouseId_takes_the_warehouse_of_the_purchase_order(bool asNull)
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        var order = await Po.OrderedAsync(s, s.W2, (s.A, 10, null, 1m));

        var body = Po.Document(s.W1, order.Id(), (s.A, 4, 1, null)).NoWarehouse(asNull);
        var receipt = await Stock.CreateAsync(s.Http, body);

        Assert.Equal(s.W2, WarehouseOf(receipt));
        Assert.Equal(order.Id(), receipt.GetProperty(Po.Link).Id());
        var posted = await Stock.PostDocumentAsync(s.Http, receipt.Id());
        Assert.Equal(s.W2, WarehouseOf(posted));
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, dw));
        Assert.Equal(6m, await Orders.OnHandAsync(s.Http, s.A, s.W2, "incomingQuantity"));
        await Balance.AssertBalancedAsync(s.U.S, "after the linked receipt", dw);
    }

    [Fact]
    public async Task AC34_A_delivery_without_warehouseId_takes_the_warehouse_of_the_sales_order()
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 20);
        var order = await So.OrderedAsync(s, s.W2, (s.A, 10, null, 1m));

        var issue = await Stock.CreateAsync(s.Http, So.Document(s.W1, order.Id(), (s.A, 4, 1, null)).NoWarehouse());

        Assert.Equal(s.W2, WarehouseOf(issue));
        Assert.Equal(order.Id(), issue.GetProperty(So.Link).Id());
        await Stock.PostDocumentAsync(s.Http, issue.Id());
        Assert.Equal(16m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(6m, await Orders.OnHandAsync(s.Http, s.A, s.W2, "reservedQuantity"));
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http, $"?warehouseId={dw}")).Total());
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("sales")]
    public async Task AC34_An_unknown_order_and_no_warehouseId_is_REFERENCE_NOT_FOUND_on_the_link_only(string kind)
    {
        var o = kind == "purchase" ? Po : So;
        var s = await Orders.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http, o.Document(s.W1, Guid.NewGuid(), (s.A, 4, 1, null)).NoWarehouse());

        var problem = await HttpAssert.ReferenceNotFoundAsync(response, o.LinkId);
        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(problem));
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    // ---- AC-35 ----

    [Fact]
    public async Task AC35_A_purchase_order_without_warehouseId_is_received_in_the_default_warehouse()
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        var draft = await Po.CreateAsync(s.Http, OrderBody(Po, s, false, (s.A, 10, null, 1m)));
        var order = await Po.ConfirmAsync(s.Http, draft.Id());
        Assert.Equal(dw, WarehouseOf(order));

        Assert.Equal(10m, await Orders.OnHandAsync(s.Http, s.A, dw, "incomingQuantity"));
        Assert.Equal(0m, await Orders.OnHandAsync(s.Http, s.A, s.W1, "incomingQuantity"));
        Assert.Equal(10m, (await Balance.ListedAsync(s.Http, s.A, dw)).Dec("incomingQuantity"));

        var receipt = await Stock.CreateAsync(s.Http, Po.Document(s.W1, order.Id(), (s.A, 4, 1, null)).NoWarehouse());
        Assert.Equal(dw, WarehouseOf(receipt));
        await Stock.PostDocumentAsync(s.Http, receipt.Id());

        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, dw));
        Assert.Equal(6m, await Orders.OnHandAsync(s.Http, s.A, dw, "incomingQuantity"));
        Assert.Equal((4m, 6m, 0m, 4m), (await Balance.ListedAsync(s.Http, s.A, dw)).Quantities());
        await Balance.AssertBalancedAsync(s.U.S, "after the receipt against the order", dw);
    }

    [Fact]
    public async Task AC35_A_sales_order_without_warehouseId_is_delivered_from_the_default_warehouse()
    {
        var s = await Orders.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        await Stock.ReceiveAsync(s.Http, dw, s.A, 20);
        var draft = await So.CreateAsync(s.Http, OrderBody(So, s, true, (s.A, 10, null, 1m)));
        var order = await So.ConfirmAsync(s.Http, draft.Id());
        Assert.Equal(dw, WarehouseOf(order));

        Assert.Equal((20m, 0m, 10m, 10m), (await Balance.ListedAsync(s.Http, s.A, dw)).Quantities());

        var issue = await Stock.CreateAsync(s.Http, So.Document(s.W1, order.Id(), (s.A, 4, 1, null)).NoWarehouse());
        Assert.Equal(dw, WarehouseOf(issue));
        await Stock.PostDocumentAsync(s.Http, issue.Id());

        Assert.Equal((16m, 0m, 6m, 10m), (await Balance.ListedAsync(s.Http, s.A, dw)).Quantities());
        await Balance.AssertBalancedAsync(s.U.S, "after the delivery against the order", dw);
    }

    [Fact]
    public async Task AC35_A_linked_document_follows_its_order_not_the_default_of_today()
    {
        var s = await Orders.SetupAsync(app);
        var former = await Balance.DefaultIdAsync(s.Http);
        var draft = await Po.CreateAsync(s.Http, OrderBody(Po, s, false, (s.A, 10, null, 1m)));
        var order = await Po.ConfirmAsync(s.Http, draft.Id());

        await Balance.SetDefaultAsync(s.Http, s.W1);

        var receipt = await Stock.CreateAsync(s.Http, Po.Document(s.W2, order.Id(), (s.A, 10, 1, null)).NoWarehouse());
        Assert.Equal(former, WarehouseOf(receipt));
        await Stock.PostDocumentAsync(s.Http, receipt.Id());
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, former));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }
}
