using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The conventions of spec 011, section 10: <c>DW</c> (the default warehouse), "StockList(W)", "Listed(A, W)",
/// "Ledger(A, W)", "Differences" and "Balanced" — all read through HTTP, every list paged to its end.
/// </summary>
public static class Balance
{
    public const string Differences = "/api/v1/stock-balance-differences";
    public const string Rebuild = "/api/v1/stock-balances/rebuild";

    private static readonly MasterApi W = MasterApi.Warehouses;

    /// <summary><c>GET /warehouses/{id}/stock</c>.</summary>
    public static string StockPath(Guid warehouse) => $"{W.Path}/{warehouse}/stock";

    public static string SetDefaultPath(Guid warehouse) => $"{W.Path}/{warehouse}/set-default";

    // ---- the default warehouse ----

    /// <summary>"DW": the single item of <c>GET /warehouses?isDefault=true</c> (R2: there is exactly one, and it is active).</summary>
    public static async Task<JsonElement> DefaultAsync(HttpClient client)
    {
        var list = await W.ListAsync(client, "?isDefault=true");
        Assert.True(list.Total() == 1 && list.Items().Length == 1, $"A tenant must have exactly one default warehouse: {list}");
        var warehouse = list.Items()[0];
        Assert.True(warehouse.Bool("isDefault") && warehouse.Bool("isActive"), $"The default warehouse must be the default and active: {warehouse}");
        return warehouse;
    }

    public static async Task<Guid> DefaultIdAsync(HttpClient client) => (await DefaultAsync(client)).Id();

    /// <summary><c>POST /warehouses/{id}/set-default</c> without a body, the raw response.</summary>
    public static Task<HttpResponseMessage> SendSetDefaultAsync(HttpClient client, Guid warehouse) =>
        client.PostAsync(SetDefaultPath(warehouse), null);

    public static Task<HttpResponseMessage> SendSetDefaultAsync(HttpClient client, string warehouse) =>
        client.PostAsync($"{W.Path}/{warehouse}/set-default", null);

    /// <summary>Makes a warehouse the default and asserts <c>200</c>; returns the warehouse.</summary>
    public static async Task<JsonElement> SetDefaultAsync(HttpClient client, Guid warehouse)
    {
        using var response = await SendSetDefaultAsync(client, warehouse);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><c>409 DEFAULT_WAREHOUSE</c> whose <c>errors</c> has exactly the given keys (section 4.6).</summary>
    public static async Task<JsonElement> DefaultWarehouseAsync(HttpResponseMessage response, params string[] exactKeys)
    {
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "DEFAULT_WAREHOUSE");
        Assert.Equal(exactKeys, McpAssert.ErrorKeys(problem));
        return problem;
    }

    // ---- paging ----

    /// <summary>All items of a list, read page by page; <paramref name="url"/> may already have a query string.</summary>
    public static async Task<JsonElement[]> AllAsync(HttpClient client, string url, int pageSize = 200)
    {
        var items = new List<JsonElement>();
        var separator = url.Contains('?') ? '&' : '?';
        while (true)
        {
            var page = await Stock.ListAsync(client, $"{url}{separator}limit={pageSize}&offset={items.Count}");
            items.AddRange(page.Items());
            if (items.Count >= page.Total() || page.Items().Length == 0)
            {
                Assert.True(items.Count == page.Total(), $"{url}: read {items.Count} items, total is {page.Total()}");
                return items.ToArray();
            }
        }
    }

    // ---- the stock list ----

    /// <summary>One page of the stock list as returned; <paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public static Task<JsonElement> StockPageAsync(HttpClient client, Guid warehouse, string query = "") =>
        Stock.ListAsync(client, StockPath(warehouse) + query);

    /// <summary>"StockList(W)": all pages of <c>GET /warehouses/{W}/stock</c>.</summary>
    public static Task<JsonElement[]> StockListAsync(HttpClient client, Guid warehouse, string query = "") =>
        AllAsync(client, StockPath(warehouse) + query);

    /// <summary>"Listed(A, W)": the item of the article on the warehouse's stock list; the test fails when it is absent.</summary>
    public static async Task<JsonElement> ListedAsync(HttpClient client, Guid article, Guid warehouse)
    {
        var items = (await StockListAsync(client, warehouse)).Where(i => i.GetProperty("article").Id() == article).ToArray();
        Assert.True(items.Length == 1, $"The stock list of {warehouse} must have exactly one item for article {article}, has {items.Length}.");
        return items[0];
    }

    public static async Task<decimal> ListedQuantityAsync(HttpClient client, Guid article, Guid warehouse) =>
        (await ListedAsync(client, article, warehouse)).Quantity();

    public static string[] ArticleCodes(this IEnumerable<JsonElement> stockItems) =>
        stockItems.Select(i => i.GetProperty("article").Str("code")).ToArray();

    /// <summary>The four quantities of a stock list or stock-on-hand item.</summary>
    public static (decimal Quantity, decimal Incoming, decimal Reserved, decimal Available) Quantities(this JsonElement item) =>
        (item.Dec("quantity"), item.Dec("incomingQuantity"), item.Dec("reservedQuantity"), item.Dec("availableQuantity"));

    // ---- the ledger ----

    /// <summary>The whole ledger of the tenant, every page.</summary>
    public static Task<JsonElement[]> LedgerAsync(HttpClient client, string query = "") => AllAsync(client, Stock.Ledger + query);

    /// <summary>The sum of ledger quantities per (article, warehouse) pair, zero sums included.</summary>
    public static Dictionary<(Guid Article, Guid Warehouse), decimal> Sums(IEnumerable<JsonElement> entries) =>
        entries
            .GroupBy(e => (e.GetProperty("article").Id(), e.GetProperty("warehouse").Id()))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity()));

    // ---- verify and rebuild ----

    /// <summary><c>GET /stock-balance-differences</c>, the list envelope.</summary>
    public static Task<JsonElement> DifferenceListAsync(HttpClient client, string query = "") =>
        Stock.ListAsync(client, Differences + query);

    /// <summary>"Differences": <c>total</c> of <c>GET /stock-balance-differences</c>.</summary>
    public static async Task<int> DifferencesAsync(HttpClient client) => (await DifferenceListAsync(client)).Total();

    public static Task<HttpResponseMessage> SendRebuildAsync(HttpClient client) => client.PostAsync(Rebuild, null);

    /// <summary><c>POST /stock-balances/rebuild</c>; asserts <c>200</c> and returns (pairs, corrected).</summary>
    public static async Task<(int Pairs, int Corrected)> RebuildAsync(HttpClient client)
    {
        using var response = await SendRebuildAsync(client);
        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(new[] { "corrected", "pairs" }, body.PropertyNames());
        return (body.GetProperty("pairs").GetInt32(), body.GetProperty("corrected").GetInt32());
    }

    // ---- Balanced ----

    /// <summary>
    /// "Balanced" (section 10): for every given article and warehouse, Stock(A, W) == Ledger(A, W) ==
    /// Listed(A, W).quantity, each not negative; and Differences is 0. The ledger, stock on hand and the stock
    /// lists are read to their last page. Returns the quantity per pair.
    /// </summary>
    public static async Task<Dictionary<(Guid Article, Guid Warehouse), decimal>> AssertBalancedAsync(
        HttpClient client, IEnumerable<Guid> articles, IEnumerable<Guid> warehouses, string when = "")
    {
        var (articleIds, warehouseIds) = (articles.ToArray(), warehouses.ToArray());
        var ledger = Sums(await LedgerAsync(client));
        var onHand = (await AllAsync(client, Stock.OnHand))
            .ToDictionary(i => (i.GetProperty("article").Id(), i.GetProperty("warehouse").Id()), i => i.Quantity());
        var result = new Dictionary<(Guid Article, Guid Warehouse), decimal>();
        foreach (var warehouse in warehouseIds)
        {
            var listed = (await StockListAsync(client, warehouse)).ToDictionary(i => i.GetProperty("article").Id());
            foreach (var article in articleIds)
            {
                Assert.True(listed.TryGetValue(article, out var item), $"{when}: article {article} is not on the stock list of warehouse {warehouse}.");
                Assert.Equal(warehouse, item.GetProperty("warehouse").Id());
                var pair = (article, warehouse);
                var (stock, sum, list) = (onHand.GetValueOrDefault(pair), ledger.GetValueOrDefault(pair), item.Quantity());
                Assert.True(stock == sum && sum == list && list >= 0m,
                    $"{when}: not balanced for article {article} in warehouse {warehouse}: stock on hand {stock}, ledger {sum}, stock list {list}.");
                result[pair] = list;
            }
        }
        // No pair outside the ones the test names may hold stock the ledger does not explain either.
        foreach (var (pair, quantity) in onHand)
            Assert.True(quantity == ledger.GetValueOrDefault(pair) && quantity >= 0m,
                $"{when}: stock on hand {quantity} of {pair} is not the ledger sum {ledger.GetValueOrDefault(pair)}.");
        var differences = await DifferenceListAsync(client);
        Assert.True(differences.Total() == 0 && differences.Items().Length == 0, $"{when}: verify reports differences: {differences}");
        return result;
    }

    public static Task<Dictionary<(Guid Article, Guid Warehouse), decimal>> AssertBalancedAsync(StockSetup s, string when = "", params Guid[] moreWarehouses) =>
        AssertBalancedAsync(s.Http, [s.A, s.B], [s.W1, s.W2, .. moreWarehouses], when);

    // ---- request bodies ----

    /// <summary>The body without <c>warehouseId</c>, or with <c>"warehouseId": null</c> (E8: the two are the same on create).</summary>
    public static JsonObject NoWarehouse(this JsonObject body, bool asNull = false) =>
        asNull ? body.With("warehouseId", null) : body.Without("warehouseId");
}
