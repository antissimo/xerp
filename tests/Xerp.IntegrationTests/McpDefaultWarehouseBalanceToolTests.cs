using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-71 to AC-73: the default warehouse, the stock list and the stored balance through MCP tools —
/// the same results and the same errors as HTTP. The tool list itself (AC-70) is in <see cref="McpToolListTests"/>.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpDefaultWarehouseBalanceToolTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;
    private static readonly OrderApi Po = OrderApi.Purchase;
    private static readonly OrderApi So = OrderApi.Sales;

    private sealed record Session(OrderSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the tenant's first key (not the MCP key).</summary>
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await Orders.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-warehouse", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    /// <summary>The same refused input over both surfaces: same code, same <c>errors</c> keys (AC-73).</summary>
    private static async Task<JsonElement> AssertParityAsync(
        HttpResponseMessage http, CallToolResult tool, HttpStatusCode status, string code, params string[] errorKeys)
    {
        var problem = await HttpAssert.ProblemAsync(http, status, code);
        var error = McpAssert.Error(tool, code, errorKeys);
        Assert.Equal(errorKeys.Order(StringComparer.Ordinal).ToArray(), McpAssert.ErrorKeys(problem));
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(error));
        return error;
    }

    // ---- AC-71 ----

    [Fact]
    public async Task AC71_Default_warehouse_stock_list_verify_and_rebuild_through_tools_only()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = await Uom.CreateAsync(http, "pcs", "Piece");
        var a = await Art.CreateAsync(http, "A", "Article A", unit.Id());
        var agent = await Keys.CreateAsync(app, http, "claude-warehouse", "agent");
        await using var mcp = await app.McpAsync(agent.Key);

        var defaults = await mcp.OkAsync("warehouse_list", new { isDefault = true });
        Assert.Equal(1, defaults.Total());
        var dw = Assert.Single(defaults.Items());
        Assert.Equal("CENTRAL", dw.Str("code"));
        Assert.True(dw.Bool("isDefault"));
        McpAssert.JsonEqual(await W.ListAsync(http, "?isDefault=true"), defaults, "warehouse_list differs from HTTP");
        McpAssert.JsonEqual(await W.ListAsync(http, "?isDefault=false"), await mcp.OkAsync("warehouse_list", new { isDefault = false }));
        McpAssert.JsonEqual(await W.GetAsync(http, dw.Id()), await mcp.OkAsync("warehouse_get", new { id = dw.Id() }));

        var draft = await mcp.OkAsync("stock_document_create",
            new JsonObject { ["type"] = "receipt", ["documentDate"] = Stock.Date, ["lines"] = Stock.Lines((a.Id(), 100)) });
        Assert.Equal("CENTRAL", draft.GetProperty("warehouse").Str("code"));
        Assert.Equal(dw.Id(), draft.GetProperty("warehouse").Id());
        Assert.Equal(agent.Id, draft.GetProperty("createdBy").Id());
        McpAssert.JsonEqual(await Stock.GetAsync(http, draft.Id()), draft, "stock_document_create differs from HTTP");

        var posted = await mcp.OkAsync("stock_document_post", new { id = draft.Id() });
        Assert.Equal("posted", posted.Str("status"));
        McpAssert.JsonEqual(await Stock.GetAsync(http, draft.Id()), posted, "stock_document_post differs from HTTP");

        var stock = await mcp.OkAsync("warehouse_stock_list", new { id = dw.Id() });
        Assert.Equal(1, stock.Total());
        var item = Assert.Single(stock.Items());
        Assert.Equal(a.Id(), item.GetProperty("article").Id());
        Assert.Equal((100m, 0m, 0m, 100m), item.Quantities());
        McpAssert.JsonEqual(await Balance.StockPageAsync(http, dw.Id()), stock, "warehouse_stock_list differs from HTTP");
        McpAssert.JsonEqual((await mcp.OkAsync("stock_on_hand_list", new { })).Items()[0], item, "The stock list item differs from stock_on_hand_list");

        var differences = await mcp.OkAsync("stock_balance_difference_list", new { });
        Assert.Equal(0, differences.Total());
        McpAssert.JsonEqual(await Balance.DifferenceListAsync(http), differences, "stock_balance_difference_list differs from HTTP");
        McpAssert.JsonEqual(await Balance.DifferenceListAsync(http, "?limit=7&offset=3"),
            await mcp.OkAsync("stock_balance_difference_list", new { limit = 7, offset = 3 }));

        var rebuilt = await mcp.OkAsync("stock_balance_rebuild", new { });
        Assert.Equal(new[] { "corrected", "pairs" }, rebuilt.PropertyNames());
        Assert.Equal(1, rebuilt.GetProperty("pairs").GetInt32());
        Assert.Equal(0, rebuilt.GetProperty("corrected").GetInt32());
        using (var overHttp = await Balance.SendRebuildAsync(http))
            McpAssert.JsonEqual(await HttpAssert.JsonAsync(overHttp, HttpStatusCode.OK), rebuilt, "stock_balance_rebuild differs from HTTP");
        McpAssert.JsonEqual(rebuilt, await mcp.OkAsync("stock_balance_rebuild", new { }), "A second rebuild differs");

        var shop = await mcp.OkAsync("warehouse_create", new { code = "SHOP", name = "Shop" });
        Assert.False(shop.Bool("isDefault"));
        var made = await mcp.OkAsync("warehouse_set_default", new { id = shop.Id() });
        Assert.True(made.Bool("isDefault"));
        Assert.Equal(shop.Id(), made.Id());
        Assert.Equal(agent.Id, made.GetProperty("updatedBy").Id());
        McpAssert.JsonEqual(await W.GetAsync(http, shop.Id()), made, "warehouse_set_default differs from HTTP");
        Assert.Equal(shop.Id(), await Balance.DefaultIdAsync(http));
        var former = await W.GetAsync(http, dw.Id());
        Assert.False(former.Bool("isDefault"));
        Assert.Equal(agent.Id, former.GetProperty("updatedBy").Id());

        // The tool is safe to repeat (idempotentHint): the same representation again.
        McpAssert.JsonEqual(made, await mcp.OkAsync("warehouse_set_default", new { id = shop.Id() }), "Repeating warehouse_set_default changed the warehouse");

        // And the next document without a warehouse goes to the new default.
        var next = await mcp.OkAsync("stock_document_create",
            new JsonObject { ["type"] = "issue", ["documentDate"] = Stock.Date, ["warehouseId"] = null, ["lines"] = Stock.Lines((a.Id(), 1)) });
        Assert.Equal("SHOP", next.GetProperty("warehouse").Str("code"));
        Assert.Equal(100m, await Balance.ListedQuantityAsync(http, a.Id(), dw.Id()));
    }

    // ---- AC-72 ----

    [Fact]
    public async Task AC72_Warehouse_stock_list_with_every_filter_equals_HTTP()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await Art.CreateAsync(s.Http, "C", "Article C", s.S.Pcs);
        var d = await Art.CreateAsync(s.Http, "D", "Article D", s.S.Pcs);
        await Art.CreateAsync(s.Http, "E", "Something else", s.S.Pcs);
        await Stock.SetArticleActiveAsync(s.Http, d.Id(), false);
        await Stock.ReceiveAsync(s.Http, w1, a, 100);

        var page = await s.Mcp.OkAsync("warehouse_stock_list",
            new { id = w1, hasStock = false, isActive = true, search = "ARTICLE", limit = 1, offset = 1 });

        // Without stock, active, "article" in the name: B and C; the second of them.
        Assert.Equal(2, page.Total());
        Assert.Equal(new[] { "C" }, page.Items().ArticleCodes());
        Assert.Equal(1, page.GetProperty("limit").GetInt32());
        Assert.Equal(1, page.GetProperty("offset").GetInt32());
        McpAssert.JsonEqual(await Balance.StockPageAsync(s.Http, w1, "?hasStock=false&isActive=true&search=ARTICLE&limit=1&offset=1"), page,
            "warehouse_stock_list differs from HTTP");

        McpAssert.JsonEqual(await Balance.StockPageAsync(s.Http, w1), await s.Mcp.OkAsync("warehouse_stock_list", new { id = w1 }));
        McpAssert.JsonEqual(await Balance.StockPageAsync(s.Http, w1, "?hasStock=true"),
            await s.Mcp.OkAsync("warehouse_stock_list", new { id = w1, hasStock = true }));
        McpAssert.JsonEqual(await Balance.StockPageAsync(s.Http, w1, "?isActive=false"),
            await s.Mcp.OkAsync("warehouse_stock_list", new { id = w1, isActive = false }));
        McpAssert.JsonEqual(await Balance.StockPageAsync(s.Http, s.S.W2, "?search=zzz"),
            await s.Mcp.OkAsync("warehouse_stock_list", new { id = s.S.W2, search = "zzz" }));
    }

    [Theory]
    [InlineData("purchase", false)]
    [InlineData("purchase", true)]
    [InlineData("sales", false)]
    [InlineData("sales", true)]
    public async Task AC72_An_order_created_through_a_tool_without_warehouseId_is_on_the_default_warehouse(string kind, bool asNull)
    {
        var o = kind == "purchase" ? Po : So;
        await using var s = await AgentAsync();
        var dw = await Balance.DefaultIdAsync(s.Http);

        var created = await s.Mcp.OkAsync(o.Tool("create"), o.Body(s.S, (s.S.A, 10, null, 1m)).NoWarehouse(asNull));

        Assert.Equal(dw, created.GetProperty("warehouse").Id());
        Assert.Equal(s.Agent.Id, created.GetProperty("createdBy").Id());
        McpAssert.JsonEqual(await o.GetAsync(s.Http, created.Id()), created, $"{o.Tool("create")} differs from HTTP");

        // The linked document made through a tool follows the order.
        var confirmed = await s.Mcp.OkAsync(o.Tool("confirm"), new { id = created.Id() });
        await Stock.ReceiveAsync(s.Http, dw, s.S.A, 50);
        var document = await s.Mcp.OkAsync("stock_document_create", o.Document(s.S.W2, confirmed.Id(), (s.S.A, 4, 1, null)).NoWarehouse());
        Assert.Equal(dw, document.GetProperty("warehouse").Id());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, document.Id()), document, "stock_document_create differs from HTTP");
        await s.Mcp.OkAsync("stock_document_post", new { id = document.Id() });
        await Balance.AssertBalancedAsync(s.S.U.S, "after the linked document", dw);
    }

    // ---- AC-73 ----

    [Fact]
    public async Task AC73_The_default_warehouse_is_protected_through_tools_as_over_HTTP()
    {
        await using var s = await AgentAsync();
        var dw = await Balance.DefaultAsync(s.Http);
        JsonObject Deactivated() => Stock.WarehouseBody(dw).With("isActive", false);

        using var put = await W.PutAsync(s.Http, dw.Id(), Deactivated());
        await AssertParityAsync(put, await s.Mcp.CallAsync("warehouse_update", Deactivated().WithId(dw.Id())),
            HttpStatusCode.Conflict, "DEFAULT_WAREHOUSE", "isActive");

        using var delete = await W.DeleteAsync(s.Http, dw.Id());
        await AssertParityAsync(delete, await s.Mcp.CallAsync("warehouse_delete", new { id = dw.Id() }),
            HttpStatusCode.Conflict, "DEFAULT_WAREHOUSE");

        await Stock.SetWarehouseActiveAsync(s.Http, s.S.W2, false);
        using var inactive = await Balance.SendSetDefaultAsync(s.Http, s.S.W2);
        await AssertParityAsync(inactive, await s.Mcp.CallAsync("warehouse_set_default", new { id = s.S.W2 }),
            HttpStatusCode.Conflict, "DEFAULT_WAREHOUSE", "isActive");

        var random = Guid.NewGuid();
        using var unknown = await Balance.SendSetDefaultAsync(s.Http, random);
        await AssertParityAsync(unknown, await s.Mcp.CallAsync("warehouse_set_default", new { id = random }),
            HttpStatusCode.NotFound, "NOT_FOUND");

        // Nothing of it changed the default.
        McpAssert.JsonEqual(dw, await Balance.DefaultAsync(s.Http), "The default warehouse changed");
        McpAssert.JsonEqual(dw, await s.Mcp.OkAsync("warehouse_get", new { id = dw.Id() }));
    }

    [Fact]
    public async Task AC73_Stock_list_and_rebuild_tool_errors_are_those_of_HTTP()
    {
        await using var s = await AgentAsync();
        var (w1, random) = (s.S.W1, Guid.NewGuid());

        using var unknown = await s.Http.GetAsync(Balance.StockPath(random));
        await AssertParityAsync(unknown, await s.Mcp.CallAsync("warehouse_stock_list", new { id = random }),
            HttpStatusCode.NotFound, "NOT_FOUND");

        using var foo = await s.Http.GetAsync(Balance.StockPath(w1) + "?foo=1");
        await AssertParityAsync(foo, await s.Mcp.CallAsync("warehouse_stock_list", new { id = w1, foo = 1 }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "foo");

        using var limit = await s.Http.GetAsync(Balance.StockPath(w1) + "?limit=0");
        await AssertParityAsync(limit, await s.Mcp.CallAsync("warehouse_stock_list", new { id = w1, limit = 0 }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "limit");

        await s.Mcp.ErrorAsync("warehouse_stock_list", new { id = w1, hasStock = "yes" }, "VALIDATION_FAILED", "hasStock");
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("warehouse_stock_list", new { }));
        await s.Mcp.ErrorAsync("warehouse_list", new { isDefault = "yes" }, "VALIDATION_FAILED", "isDefault");
        await s.Mcp.ErrorAsync("warehouse_create", new { code = "W9", name = "Nine", isDefault = true }, "VALIDATION_FAILED", "isDefault");

        using var filtered = await s.Http.GetAsync(Balance.Differences + $"?warehouseId={w1}");
        await AssertParityAsync(filtered, await s.Mcp.CallAsync("stock_balance_difference_list", new { warehouseId = w1 }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");

        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("stock_balance_rebuild", new { tenantId = s.S.Tenant.Id }));
        await s.Mcp.ErrorAsync("stock_balance_rebuild", new { tenantId = s.S.Tenant.Id }, "VALIDATION_FAILED", "tenantId");
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("stock_balance_difference_list", new { tenantId = s.S.Tenant.Id }));
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("warehouse_set_default", new { id = w1, tenantId = s.S.Tenant.Id }));

        // None of the refused calls made W1 the default or created W9.
        Assert.NotEqual(w1, await Balance.DefaultIdAsync(s.Http));
        Assert.Equal(0, (await W.ListAsync(s.Http, "?search=W9")).Total());
    }

    [Fact]
    public async Task AC73_Replace_and_transfer_through_tools_must_name_the_warehouse()
    {
        await using var s = await AgentAsync();
        var (a, w1, w2) = (s.S.A, s.S.W1, s.S.W2);
        var draft = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", w1, (a, 1)).NoWarehouse());

        using var put = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(w1, (a, 2)).NoWarehouse());
        await AssertParityAsync(put, await s.Mcp.CallAsync("stock_document_update", Stock.Replacement(w1, (a, 2)).NoWarehouse().WithId(draft.Id())),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");
        await s.Mcp.ErrorAsync("stock_document_update", Stock.Replacement(w1, (a, 2)).NoWarehouse(asNull: true).WithId(draft.Id()),
            "VALIDATION_FAILED", "warehouseId");
        await Stock.AssertUnchangedAsync(s.Http, draft);

        using var transfer = await Stock.PostAsync(s.Http, Stock.Transfer(w1, w2, (a, 1)).NoWarehouse());
        await AssertParityAsync(transfer, await s.Mcp.CallAsync("stock_document_create", Stock.Transfer(w1, w2, (a, 1)).NoWarehouse()),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");

        foreach (var o in new[] { Po, So })
        {
            var order = await o.DraftAsync(s.S, (a, 1, null, 1m));
            using var replace = await o.PutAsync(s.Http, order.Id(), o.Replacement(s.S, (a, 2, null, 1m)).NoWarehouse());
            await AssertParityAsync(replace, await s.Mcp.CallAsync(o.Tool("update"), o.Replacement(s.S, (a, 2, null, 1m)).NoWarehouse().WithId(order.Id())),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");
            await o.AssertUnchangedAsync(s.Http, order);
        }

        // An unknown order and no warehouse: the link is what is wrong, over both surfaces.
        var unknownOrder = Guid.NewGuid();
        using var linked = await Stock.PostAsync(s.Http, Po.Document(w1, unknownOrder, (a, 1, 1, null)).NoWarehouse());
        await AssertParityAsync(linked, await s.Mcp.CallAsync("stock_document_create", Po.Document(w1, unknownOrder, (a, 1, 1, null)).NoWarehouse()),
            HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND", Po.LinkId);

        Assert.Equal(1, (await Stock.DocumentsAsync(s.Http)).Total());
    }
}
