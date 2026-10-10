using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-80 to AC-83 (T1–T6): every tenant has its own default warehouse, its own stock lists and its
/// own stored balances; another tenant's default warehouse does not exist, and one tenant's verify and
/// rebuild neither see nor change the other's — over HTTP and through tools.
/// </summary>
[Collection(XerpCollection.Name)]
public class DefaultWarehouseBalanceIsolationTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    private sealed record Side(StockSetup S, McpConnection? Connection) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public McpConnection Mcp => Connection ?? throw new InvalidOperationException("This side has no MCP client.");

        public ValueTask DisposeAsync() => Connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private async Task<Side> SideAsync(bool mcp = false)
    {
        var setup = await Stock.SetupAsync(app);
        return new Side(setup, mcp ? await app.McpAsync(setup.Tenant.Key) : null);
    }

    private static Guid WarehouseOf(JsonElement document) => document.GetProperty("warehouse").Id();

    // ---- AC-80 ----

    [Fact]
    public async Task AC80_Each_tenant_has_its_own_default_and_the_others_does_not_exist()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        var xDefault = await Balance.DefaultAsync(x.Http);
        var yDefault = await Balance.DefaultAsync(y.Http);
        Assert.Equal("CENTRAL", xDefault.Str("code"));
        Assert.Equal("CENTRAL", yDefault.Str("code"));
        Assert.NotEqual(xDefault.Id(), yDefault.Id());
        Assert.Equal(x.S.Tenant.ApiKeyId, xDefault.GetProperty("createdBy").Id());
        Assert.Equal(y.S.Tenant.ApiKeyId, yDefault.GetProperty("createdBy").Id());
        var id = xDefault.Id();

        using (var setDefault = await Balance.SendSetDefaultAsync(y.Http, id))
            await HttpAssert.NotFoundAsync(setDefault);
        using (var stock = await y.Http.GetAsync(Balance.StockPath(id)))
            await HttpAssert.NotFoundAsync(stock);
        using (var get = await y.Http.GetAsync($"{W.Path}/{id}"))
            await HttpAssert.NotFoundAsync(get);
        using (var put = await W.PutAsync(y.Http, id, Stock.WarehouseBody(xDefault).With("name", "Taken over")))
            await HttpAssert.NotFoundAsync(put);
        using (var deactivate = await W.PutAsync(y.Http, id, Stock.WarehouseBody(xDefault).With("isActive", false)))
            await HttpAssert.NotFoundAsync(deactivate);
        using (var delete = await W.DeleteAsync(y.Http, id))
            await HttpAssert.NotFoundAsync(delete);

        await y.Mcp.ErrorAsync("warehouse_set_default", new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("warehouse_stock_list", new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("warehouse_get", new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("warehouse_update", Stock.WarehouseBody(xDefault).With("isActive", false).WithId(id), "NOT_FOUND");
        await y.Mcp.ErrorAsync("warehouse_delete", new { id }, "NOT_FOUND");

        // T2: neither is it a warehouse for one of Y's documents.
        using (var document = await Stock.PostAsync(y.Http, Stock.Draft("receipt", id, (y.S.A, 1))))
            await HttpAssert.ReferenceNotFoundAsync(document, "warehouseId");
        await y.Mcp.ErrorAsync("stock_document_create", Stock.Draft("receipt", id, (y.S.A, 1)), "REFERENCE_NOT_FOUND", "warehouseId");

        McpAssert.JsonEqual(xDefault, await Balance.DefaultAsync(x.Http), "X's default warehouse changed");
        McpAssert.JsonEqual(yDefault, await Balance.DefaultAsync(y.Http), "Y's default warehouse changed");
        Assert.Equal(new[] { "CENTRAL", "W1", "W2" }, (await W.ListAsync(y.Http)).Codes());
        Assert.Single((await y.Mcp.OkAsync("warehouse_list", new { isDefault = true })).Items());
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_An_omitted_warehouseId_resolves_to_the_callers_default_only()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        var xDefault = await Balance.DefaultAsync(x.Http);
        var yCentral = await Balance.DefaultIdAsync(y.Http);

        await Balance.SetDefaultAsync(y.Http, y.S.W1);

        McpAssert.JsonEqual(xDefault, await Balance.DefaultAsync(x.Http), "X's default warehouse changed");
        McpAssert.JsonEqual(x.S.Warehouse1, await W.GetAsync(x.Http, x.S.W1), "X's W1 changed");

        var ofX = await Stock.CreateAsync(x.Http, Stock.Draft("receipt", x.S.W1, (x.S.A, 10)).NoWarehouse());
        var ofY = await Stock.CreateAsync(y.Http, Stock.Draft("receipt", y.S.W2, (y.S.A, 10)).NoWarehouse());
        var ofYByTool = await y.Mcp.OkAsync("stock_document_create", Stock.Draft("receipt", y.S.W2, (y.S.A, 1)).NoWarehouse());

        Assert.Equal(xDefault.Id(), WarehouseOf(ofX));
        Assert.Equal("CENTRAL", ofX.GetProperty("warehouse").Str("code"));
        Assert.Equal(y.S.W1, WarehouseOf(ofY));
        Assert.Equal(y.S.W1, WarehouseOf(ofYByTool));

        await Stock.PostDocumentAsync(x.Http, ofX.Id());
        await Stock.PostDocumentAsync(y.Http, ofY.Id());
        Assert.Equal(10m, await Balance.ListedQuantityAsync(x.Http, x.S.A, xDefault.Id()));
        Assert.Equal(0m, await Balance.ListedQuantityAsync(x.Http, x.S.A, x.S.W1));
        Assert.Equal(10m, await Balance.ListedQuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(0m, await Balance.ListedQuantityAsync(y.Http, y.S.A, yCentral));
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_A_stock_list_and_verify_show_the_callers_tenant_only()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);

        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);

        foreach (var warehouse in new[] { y.S.W1, y.S.W2, await Balance.DefaultIdAsync(y.Http) })
        {
            var list = await Balance.StockListAsync(y.Http, warehouse);
            Assert.Equal(new[] { y.S.A, y.S.B }, list.Select(i => i.GetProperty("article").Id()).ToArray());
            Assert.All(list, i =>
            {
                Assert.Equal((0m, 0m, 0m, 0m), i.Quantities());
                Assert.Equal(warehouse, i.GetProperty("warehouse").Id());
                Assert.Equal(y.S.UnitId, i.GetProperty("unit").Id());
            });
        }
        Assert.Equal(0m, await Balance.ListedQuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(0, (await Balance.StockPageAsync(y.Http, y.S.W1, "?hasStock=true")).Total());
        Assert.Equal(0, (await Stock.OnHandAsync(y.Http)).Total());
        Assert.Equal(0, await Balance.DifferencesAsync(y.Http));

        var byTool = await y.Mcp.OkAsync("warehouse_stock_list", new { id = y.S.W1 });
        McpAssert.JsonEqual(await Balance.StockPageAsync(y.Http, y.S.W1), byTool, "warehouse_stock_list differs from HTTP");
        Assert.Equal(0, (await y.Mcp.OkAsync("warehouse_stock_list", new { id = y.S.W1, hasStock = true })).Total());
        Assert.Equal(0, (await y.Mcp.OkAsync("stock_balance_difference_list", new { })).Total());

        // X sees its own 100 and nothing of Y.
        var ofX = await Balance.StockListAsync(x.Http, x.S.W1);
        Assert.Equal(new[] { x.S.A, x.S.B }, ofX.Select(i => i.GetProperty("article").Id()).ToArray());
        Assert.Equal(100m, ofX[0].Quantity());
        Assert.Equal(0, await Balance.DifferencesAsync(x.Http));
    }

    // ---- AC-83 ----

    [Fact]
    public async Task AC83_Rebuild_reads_and_writes_the_callers_tenant_only()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        var xLedger = (await Stock.LedgerAsync(x.Http)).Total();

        using (var response = await Balance.SendRebuildAsync(y.Http))
        {
            var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
            Assert.Equal(0, body.GetProperty("pairs").GetInt32());
            Assert.Equal(0, body.GetProperty("corrected").GetInt32());
        }
        var byTool = await y.Mcp.OkAsync("stock_balance_rebuild", new { });
        Assert.Equal(0, byTool.GetProperty("pairs").GetInt32());
        Assert.Equal(0, byTool.GetProperty("corrected").GetInt32());

        var balanced = await Balance.AssertBalancedAsync(x.S, "after Y's rebuild");
        Assert.Equal(100m, balanced[(x.S.A, x.S.W1)]);
        Assert.Equal(100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
        Assert.Equal(xLedger, (await Stock.LedgerAsync(x.Http)).Total());
        Assert.Equal((1, 0), await Balance.RebuildAsync(x.Http));

        // And the other way round: X's rebuild leaves Y's postings alone.
        await Stock.ReceiveAsync(y.Http, y.S.W2, y.S.B, 7);
        await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.B, 3);
        Assert.Equal((1, 0), await Balance.RebuildAsync(x.Http));
        Assert.Equal((2, 0), await Balance.RebuildAsync(y.Http));
        var ofY = await Balance.AssertBalancedAsync(y.S, "after X's rebuild");
        Assert.Equal(7m, ofY[(y.S.B, y.S.W2)]);
        Assert.Equal(3m, ofY[(y.S.B, y.S.W1)]);
        Assert.Equal(0m, ofY[(y.S.A, y.S.W1)]);
        await Balance.AssertBalancedAsync(x.S, "at the end");
    }
}
