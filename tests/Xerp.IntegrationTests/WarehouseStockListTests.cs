using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-40 to AC-48 (R11–R17, E11–E13, E16): the stock list of a warehouse has one item per stock
/// article of the tenant — zero quantities included, inactive articles included, service articles never — and
/// each item is the stock-on-hand item of the pair.
/// </summary>
[Collection(XerpCollection.Name)]
public class WarehouseStockListTests(XerpFixture app)
{
    private static readonly OrderApi Po = OrderApi.Purchase;
    private static readonly OrderApi So = OrderApi.Sales;

    // Spec 011, 4.3: exactly the properties of a stock-on-hand item.
    private static readonly string[] Item =
        ["article", "availableQuantity", "incomingQuantity", "quantity", "reservedQuantity", "unit", "warehouse"];

    private static readonly string[] Reference = ["code", "id", "name"];

    private static void AssertAllZero(JsonElement item) => Assert.Equal((0m, 0m, 0m, 0m), item.Quantities());

    /// <summary>Every stock-on-hand item of the warehouse is on its stock list, JSON-equal; returns how many.</summary>
    private static async Task<int> AssertOnHandIsOnTheListAsync(HttpClient client, Guid warehouse)
    {
        var listed = (await Balance.StockListAsync(client, warehouse)).ToDictionary(i => i.GetProperty("article").Id());
        var onHand = await Balance.AllAsync(client, $"{Stock.OnHand}?warehouseId={warehouse}");
        foreach (var item in onHand)
        {
            Assert.True(listed.TryGetValue(item.GetProperty("article").Id(), out var onList), $"A stock-on-hand item is not on the stock list: {item}");
            McpAssert.JsonEqual(item, onList, "The stock list item differs from the stock-on-hand item");
        }
        return onHand.Length;
    }

    // ---- AC-40 ----

    [Fact]
    public async Task AC40_With_nothing_posted_every_warehouse_lists_every_stock_article_with_zeros()
    {
        var s = await Stock.SetupAsync(app);
        var dw = await Balance.DefaultAsync(s.Http);

        foreach (var (warehouse, code) in new[] { (s.W1, "W1"), (s.W2, "W2"), (dw.Id(), "CENTRAL") })
        {
            var page = await Balance.StockPageAsync(s.Http, warehouse);
            Assert.Equal(new[] { "items", "limit", "offset", "total" }, page.PropertyNames());
            Assert.Equal(2, page.Total());
            Assert.Equal(50, page.GetProperty("limit").GetInt32());
            Assert.Equal(0, page.GetProperty("offset").GetInt32());
            var items = page.Items();
            Assert.Equal(new[] { "A", "B" }, items.ArticleCodes());
            Assert.Equal(new[] { s.A, s.B }, items.Select(i => i.GetProperty("article").Id()).ToArray());
            foreach (var item in items)
            {
                Assert.Equal(Item, item.PropertyNames());
                Assert.Equal(Reference, item.GetProperty("article").PropertyNames());
                Assert.Equal(Reference, item.GetProperty("warehouse").PropertyNames());
                Assert.Equal(Reference, item.GetProperty("unit").PropertyNames());
                Assert.Equal(warehouse, item.GetProperty("warehouse").Id());
                Assert.Equal(code, item.GetProperty("warehouse").Str("code"));
                Assert.Equal(s.UnitId, item.GetProperty("unit").Id());
                Assert.Equal("pcs", item.GetProperty("unit").Str("code"));
                Assert.Equal("Piece", item.GetProperty("unit").Str("name"));
                foreach (var quantity in new[] { "quantity", "incomingQuantity", "reservedQuantity", "availableQuantity" })
                    Assert.Equal(JsonValueKind.Number, item.GetProperty(quantity).ValueKind);
                AssertAllZero(item);
            }
            Assert.Equal("Article A", items[0].GetProperty("article").Str("name"));
            Assert.DoesNotContain(items, i => i.GetProperty("article").Id() == s.S);
        }
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        Assert.Empty((await Stock.OnHandAsync(s.Http)).Items());
    }

    [Fact]
    public async Task AC40_E11_A_tenant_without_stock_articles_has_an_empty_stock_list()
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultIdAsync(tenant.Client);

        var empty = await Balance.StockPageAsync(tenant.Client, dw);
        Assert.Equal(0, empty.Total());
        Assert.Empty(empty.Items());

        // A service article alone changes nothing.
        var unit = await Uom.CreateAsync(tenant.Client, "h", "Hour");
        await Art.CreateAsync(tenant.Client, "S", "Service S", unit.Id(), "service");
        Assert.Equal(0, (await Balance.StockPageAsync(tenant.Client, dw)).Total());
    }

    // ---- AC-41 ----

    [Fact]
    public async Task AC41_A_receipt_shows_on_the_list_of_its_warehouse_only_and_the_item_is_the_stock_on_hand_item()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        var w1 = await Balance.StockListAsync(s.Http, s.W1);
        Assert.Equal(new[] { "A", "B" }, w1.ArticleCodes());
        Assert.Equal((100m, 0m, 0m, 100m), w1[0].Quantities());
        AssertAllZero(w1[1]);
        var w2 = await Balance.StockListAsync(s.Http, s.W2);
        Assert.Equal(new[] { "A", "B" }, w2.ArticleCodes());
        Assert.All(w2, AssertAllZero);
        Assert.All(await Balance.StockListAsync(s.Http, await Balance.DefaultIdAsync(s.Http)), AssertAllZero);

        var onHand = (await Stock.OnHandAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}")).Items();
        McpAssert.JsonEqual(Assert.Single(onHand), w1[0], "The stock list item differs from the stock-on-hand item");
    }

    // ---- AC-42 ----

    [Fact]
    public async Task AC42_E13_The_list_shows_all_four_quantities_as_stock_on_hand_does()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await So.OrderedAsync(s, (s.A, 30, null, 1m));
        await Po.OrderedAsync(s, (s.B, 60, null, 1m));

        Assert.Equal((100m, 0m, 30m, 70m), (await Balance.ListedAsync(s.Http, s.A, s.W1)).Quantities());
        Assert.Equal((0m, 60m, 0m, 0m), (await Balance.ListedAsync(s.Http, s.B, s.W1)).Quantities());
        Assert.Equal(2, await AssertOnHandIsOnTheListAsync(s.Http, s.W1));

        // The other warehouse has neither the stock nor the orders.
        Assert.All(await Balance.StockListAsync(s.Http, s.W2), AssertAllZero);
        Assert.Equal(0, await AssertOnHandIsOnTheListAsync(s.Http, s.W2));

        // E13: no stock but an incoming quantity — "has no stock" for the filter.
        Assert.Equal(new[] { "B" }, (await Balance.StockListAsync(s.Http, s.W1, "?hasStock=false")).ArticleCodes());
        Assert.Equal(new[] { "A" }, (await Balance.StockListAsync(s.Http, s.W1, "?hasStock=true")).ArticleCodes());
    }

    [Fact]
    public async Task AC42_The_list_follows_orders_and_their_fulfilment()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var sales = await So.OrderedAsync(s, (s.A, 30, null, 1m));
        var purchase = await Po.OrderedAsync(s, (s.B, 60, null, 1m));

        await So.FulfilAsync(s.Http, sales, 1, 10);
        await Po.FulfilAsync(s.Http, purchase, 1, 25);

        Assert.Equal((90m, 0m, 20m, 70m), (await Balance.ListedAsync(s.Http, s.A, s.W1)).Quantities());
        Assert.Equal((25m, 35m, 0m, 25m), (await Balance.ListedAsync(s.Http, s.B, s.W1)).Quantities());
        Assert.Equal(2, await AssertOnHandIsOnTheListAsync(s.Http, s.W1));

        await So.CloseAsync(s.Http, sales.Id());
        await Po.CloseAsync(s.Http, purchase.Id());
        Assert.Equal((90m, 0m, 0m, 90m), (await Balance.ListedAsync(s.Http, s.A, s.W1)).Quantities());
        Assert.Equal((25m, 0m, 0m, 25m), (await Balance.ListedAsync(s.Http, s.B, s.W1)).Quantities());
        await AssertOnHandIsOnTheListAsync(s.Http, s.W1);
    }

    // ---- AC-43 ----

    [Fact]
    public async Task AC43_E16_A_pair_that_went_to_zero_stays_on_the_list()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        await Stock.IssueAsync(s.Http, s.W1, s.A, 100);

        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}")).Total());
        var listed = await Balance.ListedAsync(s.Http, s.A, s.W1);
        AssertAllZero(listed);
        Assert.Equal(2, (await Balance.StockPageAsync(s.Http, s.W1)).Total());
        Assert.Equal(new[] { "A", "B" }, (await Balance.StockListAsync(s.Http, s.W1, "?hasStock=false")).ArticleCodes());
        Assert.Equal(0, (await Balance.StockPageAsync(s.Http, s.W1, "?hasStock=true")).Total());
        // Not a difference: the stored balance of zero is the ledger sum of zero.
        Assert.Equal(0, await Balance.DifferencesAsync(s.Http));
        await Balance.AssertBalancedAsync(s, "after the stock went to zero");
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_E12_Every_stock_article_is_on_every_list_always()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 40);

        var c = await Art.CreateAsync(s.Http, "C", "Article C", s.UnitId);

        foreach (var warehouse in new[] { s.W1, s.W2, await Balance.DefaultIdAsync(s.Http) })
        {
            var list = await Balance.StockListAsync(s.Http, warehouse);
            Assert.Equal(new[] { "A", "B", "C" }, list.ArticleCodes());
            Assert.Equal(c.Id(), list[2].GetProperty("article").Id());
            Assert.Equal(warehouse, list[2].GetProperty("warehouse").Id());
            AssertAllZero(list[2]);
        }
        Assert.Equal(60m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(40m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W2));

        await Stock.SetArticleActiveAsync(s.Http, c.Id(), false);
        Assert.Equal(new[] { "A", "B", "C" }, (await Balance.StockListAsync(s.Http, s.W1)).ArticleCodes());
        Assert.Equal(new[] { "A", "B", "C" }, (await Balance.StockListAsync(s.Http, s.W2)).ArticleCodes());

        // Another service article never shows; neither does the one of the setup.
        await Art.CreateAsync(s.Http, "AA", "Service between A and B", s.UnitId, "service");
        Assert.Equal(new[] { "A", "B", "C" }, (await Balance.StockListAsync(s.Http, s.W1)).ArticleCodes());

        var w3 = await MasterApi.Warehouses.CreateAsync(s.Http, "W3", "Warehouse three");
        var fresh = await Balance.StockPageAsync(s.Http, w3.Id());
        Assert.Equal(3, fresh.Total());
        Assert.Equal(new[] { "A", "B", "C" }, fresh.Items().ArticleCodes());
        Assert.All(fresh.Items(), i =>
        {
            AssertAllZero(i);
            Assert.Equal(w3.Id(), i.GetProperty("warehouse").Id());
        });
    }

    [Fact]
    public async Task AC44_A_new_stock_article_appears_and_a_deleted_one_disappears()
    {
        var s = await Stock.SetupAsync(app);
        var d = await Art.CreateAsync(s.Http, "D", "Article D", s.UnitId);
        Assert.Equal(new[] { "A", "B", "D" }, (await Balance.StockListAsync(s.Http, s.W1)).ArticleCodes());

        using var deleted = await s.Http.DeleteAsync($"{Art.Path}/{d.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(new[] { "A", "B" }, (await Balance.StockListAsync(s.Http, s.W1)).ArticleCodes());
    }

    // ---- AC-45 ----

    [Fact]
    public async Task AC45_Filters_and_paging()
    {
        var s = await Stock.SetupAsync(app);
        var c = await Art.CreateAsync(s.Http, "C", "Article C", s.UnitId);
        await Stock.SetArticleActiveAsync(s.Http, c.Id(), false);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        async Task<string[]> CodesAsync(string query)
        {
            var page = await Balance.StockPageAsync(s.Http, s.W1, query);
            Assert.Equal(page.Items().Length, page.Total());
            return page.Items().ArticleCodes();
        }

        Assert.Equal(new[] { "A", "B", "C" }, await CodesAsync(""));
        Assert.Equal(new[] { "A" }, await CodesAsync("?hasStock=true"));
        Assert.Equal(new[] { "B", "C" }, await CodesAsync("?hasStock=false"));
        Assert.Equal(new[] { "C" }, await CodesAsync("?isActive=false"));
        Assert.Equal(new[] { "A", "B" }, await CodesAsync("?isActive=true"));
        Assert.Equal(new[] { "B" }, await CodesAsync("?search=ARTICLE%20B"));
        Assert.Equal(new[] { "B" }, await CodesAsync("?search=ticle%20b"));
        Assert.Equal(new[] { "A", "B", "C" }, await CodesAsync("?search=aRtIcLe"));
        Assert.Empty(await CodesAsync("?search=zzz"));
        Assert.Equal(new[] { "B" }, await CodesAsync("?hasStock=false&isActive=true"));
        Assert.Equal(new[] { "C" }, await CodesAsync("?hasStock=false&isActive=false"));
        Assert.Empty(await CodesAsync("?hasStock=true&isActive=false"));
        Assert.Equal(new[] { "A" }, await CodesAsync("?hasStock=true&isActive=true&search=article"));
        // The search is over the article, not over the warehouse or the unit of every item.
        Assert.Empty(await CodesAsync("?search=W1"));
        Assert.Empty(await CodesAsync("?search=pcs"));

        var second = await Balance.StockPageAsync(s.Http, s.W1, "?limit=1&offset=1");
        Assert.Equal(3, second.Total());
        Assert.Equal(1, second.GetProperty("limit").GetInt32());
        Assert.Equal(1, second.GetProperty("offset").GetInt32());
        Assert.Equal(new[] { "B" }, second.Items().ArticleCodes());
        var tail = await Balance.StockPageAsync(s.Http, s.W1, "?limit=2&offset=2");
        Assert.Equal(new[] { "C" }, tail.Items().ArticleCodes());
        Assert.Equal(3, tail.Total());
        var beyond = await Balance.StockPageAsync(s.Http, s.W1, "?offset=3");
        Assert.Empty(beyond.Items());
        Assert.Equal(3, beyond.Total());
        var filteredPage = await Balance.StockPageAsync(s.Http, s.W1, "?hasStock=false&limit=1&offset=1");
        Assert.Equal(2, filteredPage.Total());
        Assert.Equal(new[] { "C" }, filteredPage.Items().ArticleCodes());

        // The filters are per warehouse: nothing has stock in W2.
        Assert.Equal(0, (await Balance.StockPageAsync(s.Http, s.W2, "?hasStock=true")).Total());
        Assert.Equal(3, (await Balance.StockPageAsync(s.Http, s.W2, "?hasStock=false")).Total());
    }

    [Fact]
    public async Task AC45_The_list_is_ordered_by_article_code_without_regard_to_letter_case()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = await Uom.CreateAsync(http, "pcs", "Piece");
        foreach (var code in new[] { "b", "A", "c" })
            await Art.CreateAsync(http, code, $"Article {code}", unit.Id());
        var w = await MasterApi.Warehouses.CreateAsync(http, "W1", "Warehouse one");

        Assert.Equal(new[] { "A", "b", "c" }, (await Balance.StockListAsync(http, w.Id())).ArticleCodes());
        Assert.Equal(new[] { "b" }, (await Balance.StockPageAsync(http, w.Id(), "?limit=1&offset=1")).Items().ArticleCodes());
        Assert.Equal(new[] { "A", "b", "c" }, (await Balance.StockListAsync(http, await Balance.DefaultIdAsync(http))).ArticleCodes());
    }

    [Fact]
    public async Task AC45_The_list_pages_to_its_end_without_gaps_or_repeats()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = await Uom.CreateAsync(http, "pcs", "Piece");
        var expected = new List<string>();
        for (var i = 1; i <= 7; i++)
        {
            expected.Add($"P-{i:00}");
            await Art.CreateAsync(http, $"P-{i:00}", $"Part {i}", unit.Id());
        }
        var dw = await Balance.DefaultIdAsync(http);

        Assert.Equal(expected, (await Balance.AllAsync(http, Balance.StockPath(dw), pageSize: 3)).ArticleCodes());
        Assert.Equal(7, (await Balance.StockPageAsync(http, dw, "?limit=500")).Items().Length);
    }

    // ---- AC-46 ----

    [Fact]
    public async Task AC46_Quantities_are_in_the_base_unit()
    {
        var u = await Units.SetupAsync(app);

        await Units.PostedAsync(u.Http, "receipt", u.W1, (u.A, 2, u.Box));

        var listed = await Balance.ListedAsync(u.Http, u.A, u.W1);
        Assert.Equal((24m, 0m, 0m, 24m), listed.Quantities());
        Assert.Equal("pcs", listed.GetProperty("unit").Str("code"));
        Assert.Equal(u.Pcs, listed.GetProperty("unit").Id());
        McpAssert.JsonEqual((await Orders.OnHandItemAsync(u.Http, u.A, u.W1))!.Value, listed);
    }

    // ---- AC-47 ----

    [Theory]
    [InlineData("random")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task AC47_The_stock_list_of_an_unknown_warehouse_is_404(string id)
    {
        var s = await Stock.SetupAsync(app);
        var segment = id == "random" ? Guid.NewGuid().ToString() : id;

        using var response = await s.Http.GetAsync($"{MasterApi.Warehouses.Path}/{segment}/stock");

        await HttpAssert.NotFoundAsync(response);
    }

    [Fact]
    public async Task AC47_An_article_id_or_a_deleted_warehouse_is_not_a_warehouse()
    {
        var s = await Stock.SetupAsync(app);

        using var article = await s.Http.GetAsync(Balance.StockPath(s.A));
        await HttpAssert.NotFoundAsync(article);
        using var deleted = await MasterApi.Warehouses.DeleteAsync(s.Http, s.W2);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await s.Http.GetAsync(Balance.StockPath(s.W2));
        await HttpAssert.NotFoundAsync(gone);
    }

    [Fact]
    public async Task AC47_An_inactive_warehouse_still_lists_its_stock()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 7);

        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);

        var page = await Balance.StockPageAsync(s.Http, s.W2);
        Assert.Equal(2, page.Total());
        Assert.Equal(new[] { "A", "B" }, page.Items().ArticleCodes());
        Assert.Equal(7m, page.Items()[0].Quantity());
        Assert.Equal(new[] { "A" }, (await Balance.StockListAsync(s.Http, s.W2, "?hasStock=true")).ArticleCodes());
    }

    // ---- AC-48 ----

    [Fact]
    public async Task AC48_A_renamed_and_recoded_article_shows_its_new_values_in_the_new_place()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        await Stock.ReplaceArticleAsync(s.Http, s.A, Stock.ArticleBody(s.ArticleA).With("code", "Z-9").With("name", "Zinc plate"));

        var list = await Balance.StockListAsync(s.Http, s.W1);
        Assert.Equal(new[] { "B", "Z-9" }, list.ArticleCodes());
        Assert.Equal(s.A, list[1].GetProperty("article").Id());
        Assert.Equal("Zinc plate", list[1].GetProperty("article").Str("name"));
        Assert.Equal((100m, 0m, 0m, 100m), list[1].Quantities());
        Assert.Equal(new[] { "Z-9" }, (await Balance.StockListAsync(s.Http, s.W1, "?search=zinc")).ArticleCodes());
        Assert.Empty(await Balance.StockListAsync(s.Http, s.W1, "?search=Article%20A"));
        await Balance.AssertBalancedAsync(s, "after the article was renamed");
    }

    [Fact]
    public async Task AC48_A_renamed_warehouse_and_unit_show_their_new_values()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        var w = MasterApi.Warehouses;

        await w.ReplaceAsync(s.Http, s.W1, Stock.WarehouseBody(s.Warehouse1).With("code", "MAIN").With("name", "Main hall"));

        var listed = await Balance.ListedAsync(s.Http, s.A, s.W1);
        Assert.Equal("MAIN", listed.GetProperty("warehouse").Str("code"));
        Assert.Equal("Main hall", listed.GetProperty("warehouse").Str("name"));
        Assert.Equal(5m, listed.Quantity());
        McpAssert.JsonEqual((await Orders.OnHandItemAsync(s.Http, s.A, s.W1))!.Value, listed);
    }
}
