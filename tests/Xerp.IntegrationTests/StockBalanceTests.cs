using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-50 to AC-54 and AC-59 (R19–R28, E14, E15): the stored balance of every (article, warehouse)
/// pair equals the sum of its ledger entries after every kind of posting and reversal; a refused posting, a
/// draft, an order and a master change no balance; verify reports nothing and rebuild changes nothing in a
/// healthy tenant. The races are in <see cref="StockBalanceRaceTests"/>.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockBalanceTests(XerpFixture app)
{
    private static readonly OrderApi Po = OrderApi.Purchase;
    private static readonly OrderApi So = OrderApi.Sales;

    /// <summary>The tenant of AC-50: the standard setup with orders, a third stock article C and the default warehouse.</summary>
    private sealed record World(OrderSetup S, Guid C, Guid Dw)
    {
        public HttpClient Http => S.Http;
        public Guid[] Articles => [S.A, S.B, C];
        public Guid[] Warehouses => [S.W1, S.W2, Dw];

        /// <summary>Balanced over every article and warehouse of the test; returns the quantity per pair.</summary>
        public Task<Dictionary<(Guid Article, Guid Warehouse), decimal>> BalancedAsync(string when) =>
            Balance.AssertBalancedAsync(Http, Articles, Warehouses, when);
    }

    private async Task<World> WorldAsync()
    {
        var s = await Orders.SetupAsync(app);
        var c = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        return new World(s, c.Id(), await Balance.DefaultIdAsync(s.Http));
    }

    /// <summary>
    /// The steps of AC-50 in order. After each step <paramref name="check"/> gets the step's name and the
    /// quantities the test expects for (A, W1), (B, W1), (C, W1), (A, W2), (A, DW); every other pair is 0.
    /// </summary>
    private static async Task RunSequenceAsync(World w, Func<string, decimal[], Task> check)
    {
        var (s, http) = (w.S, w.Http);

        await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, "receipt", s.W1, (s.A, 100), (s.B, 50), (w.C, 10))).Id());
        await check("a receipt of A, B and C into W1", [100, 50, 10, 0, 0]);

        await Stock.IssueAsync(http, s.W1, s.A, 10);
        await check("an issue", [90, 50, 10, 0, 0]);

        var transfer = await Stock.TransferAsync(http, s.W1, s.W2, s.A, 30);
        await check("a transfer W1 -> W2", [60, 50, 10, 30, 0]);

        var count = await Counts.PostedAsync(http, s.W1, (s.A, 65, null), (s.B, 45, null), (w.C, 10, null));
        await check("a count with a surplus, a shortage and a line without difference", [65, 45, 10, 30, 0]);

        await Units.PostedAsync(http, "receipt", s.W1, (s.A, 2, s.Box));
        await check("a receipt in boxes", [89, 45, 10, 30, 0]);

        await Stock.ReverseAsync(http, transfer.Id());
        await check("the reversal of the transfer", [119, 45, 10, 0, 0]);

        await Stock.ReverseAsync(http, count.Id());
        await check("the reversal of the count", [114, 50, 10, 0, 0]);

        var purchase = await Po.OrderedAsync(s, (s.B, 20, null, 2m));
        await Po.FulfilAsync(http, purchase, 1, 8);
        await check("a goods receipt against a purchase order", [114, 58, 10, 0, 0]);

        var sales = await So.OrderedAsync(s, (s.A, 30, null, 3m));
        var delivery = await So.FulfilAsync(http, sales, 1, 12);
        await check("a delivery against a sales order", [102, 58, 10, 0, 0]);

        await Stock.ReverseAsync(http, delivery.Id(), Stock.NextDay);
        await check("the reversal of the delivery", [114, 58, 10, 0, 0]);

        var intoDefault = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 5)).NoWarehouse());
        Assert.Equal(w.Dw, intoDefault.GetProperty("warehouse").Id());
        await Stock.PostDocumentAsync(http, intoDefault.Id());
        await check("a receipt into DW created without warehouseId", [114, 58, 10, 0, 5]);
    }

    private static Task RunSequenceAsync(World w) => RunSequenceAsync(w, (_, _) => Task.CompletedTask);

    private static Dictionary<(Guid, Guid), decimal> Expected(World w, decimal[] q) => new()
    {
        [(w.S.A, w.S.W1)] = q[0], [(w.S.B, w.S.W1)] = q[1], [(w.C, w.S.W1)] = q[2],
        [(w.S.A, w.S.W2)] = q[3], [(w.S.B, w.S.W2)] = 0, [(w.C, w.S.W2)] = 0,
        [(w.S.A, w.Dw)] = q[4], [(w.S.B, w.Dw)] = 0, [(w.C, w.Dw)] = 0,
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_Balanced_after_every_kind_of_posting()
    {
        var w = await WorldAsync();
        await w.BalancedAsync("before anything was posted");
        var steps = 0;

        await RunSequenceAsync(w, async (step, quantities) =>
        {
            steps++;
            var actual = await w.BalancedAsync($"after {step}");
            var expected = Expected(w, quantities);
            Assert.True(expected.Count == actual.Count && expected.All(e => actual[e.Key] == e.Value),
                $"After {step}: expected [{string.Join(", ", expected.Values)}], the stock lists show [{string.Join(", ", expected.Keys.Select(k => actual[k]))}].");
        });

        Assert.Equal(11, steps);
    }

    [Fact]
    public async Task AC50_Balanced_holds_for_the_incoming_and_reserved_quantities_too()
    {
        // The orders of the sequence stay open: what is still to come and to go is on the list beside the balance.
        var w = await WorldAsync();
        await RunSequenceAsync(w);

        Assert.Equal((58m, 12m, 0m, 58m), (await Balance.ListedAsync(w.Http, w.S.B, w.S.W1)).Quantities());
        Assert.Equal((114m, 0m, 30m, 84m), (await Balance.ListedAsync(w.Http, w.S.A, w.S.W1)).Quantities());
        foreach (var warehouse in w.Warehouses)
        {
            var listed = (await Balance.StockListAsync(w.Http, warehouse)).ToDictionary(i => i.GetProperty("article").Id());
            foreach (var item in await Balance.AllAsync(w.Http, $"{Stock.OnHand}?warehouseId={warehouse}"))
                McpAssert.JsonEqual(item, listed[item.GetProperty("article").Id()], "The stock list item differs from the stock-on-hand item");
        }
    }

    [Fact]
    public async Task AC50_E14_A_posted_count_without_differences_writes_no_entry_and_changes_no_balance()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var entries = (await Stock.LedgerAsync(s.Http)).Total();

        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 10, null), (s.B, 0, null));

        Assert.Empty(await Stock.EntriesAsync(s.Http, count.Id()));
        Assert.Equal(entries, (await Stock.LedgerAsync(s.Http)).Total());
        var balanced = await Balance.AssertBalancedAsync(s, "after a count without differences");
        Assert.Equal(10m, balanced[(s.A, s.W1)]);
        Assert.Equal(0m, balanced[(s.B, s.W1)]);
        // (B, W1) never moved: it is no pair of the ledger, and no difference.
        Assert.Equal((1, 0), await Balance.RebuildAsync(s.Http));

        await Stock.ReverseAsync(s.Http, count.Id());
        await Balance.AssertBalancedAsync(s, "after the reversal of a count without differences");
        Assert.Equal((1, 0), await Balance.RebuildAsync(s.Http));
    }

    // ---- AC-51 ----

    [Fact]
    public async Task AC51_A_refused_posting_changes_no_balance()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var receipt = await Stock.ReceiveAsync(http, s.W1, s.A, 15);
        await Stock.IssueAsync(http, s.W1, s.A, 6);
        var count = await Counts.CountAsync(http, s.W1, s.A, 4);
        await Stock.ReceiveAsync(http, s.W1, s.A, 1);
        var order = await So.OrderedAsync(s, (s.A, 5, null, 1m));
        var entries = (await Stock.LedgerAsync(http)).Total();
        var documents = (await Stock.DocumentsAsync(http, "?status=posted")).Total();

        async Task AssertNothingChangedAsync(string after)
        {
            var listed = await Balance.ListedAsync(http, s.A, s.W1);
            Assert.True(listed.Quantities() == (10m, 0m, 5m, 5m), $"After {after}: {listed}");
            var balanced = await Balance.AssertBalancedAsync(s.U.S, $"after {after}");
            Assert.Equal(10m, balanced[(s.A, s.W1)]);
            Assert.Equal(entries, (await Stock.LedgerAsync(http)).Total());
            Assert.Equal(documents, (await Stock.DocumentsAsync(http, "?status=posted")).Total());
        }

        await AssertNothingChangedAsync("the setup");

        var issue = await Stock.CreateAsync(http, "issue", s.W1, (s.A, 11));
        using (var refused = await Stock.SendPostAsync(http, issue.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(http, issue.Id());
        await AssertNothingChangedAsync("an issue of 11");

        var transfer = await Stock.CreateTransferAsync(http, s.W1, s.W2, (s.A, 11));
        using (var refused = await Stock.SendPostAsync(http, transfer.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        await AssertNothingChangedAsync("a transfer of 11");

        using (var refused = await Stock.SendPostAsync(http, count.Id()))
            await Stock.ConflictAsync(refused, "COUNT_OUTDATED");
        await Stock.AssertDraftAsync(http, count.Id());
        await AssertNothingChangedAsync("an outdated count");

        var (delivery, exceeds) = await So.TryFulfilAsync(http, order, 1, 6);
        using (exceeds)
            await Stock.ConflictAsync(exceeds, "QUANTITY_EXCEEDS_ORDER");
        await Stock.AssertDraftAsync(http, delivery);
        await AssertNothingChangedAsync("a delivery above the order");

        using (var refused = await Stock.SendReverseAsync(http, receipt.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        Assert.Equal("posted", (await Stock.GetAsync(http, receipt.Id())).Str("status"));
        await AssertNothingChangedAsync("the reversal of a receipt whose goods were issued");

        // A document with one good and one refused line posts nothing at all.
        var mixed = await Stock.CreateAsync(http, "issue", s.W1, (s.A, 2), (s.B, 1));
        using (var refused = await Stock.SendPostAsync(http, mixed.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        await AssertNothingChangedAsync("an issue with one refused line");
    }

    // ---- AC-52 ----

    [Fact]
    public async Task AC52_Drafts_orders_and_masters_change_no_balance()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var dw = await Balance.DefaultIdAsync(http);
        await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, "receipt", s.W1, (s.A, 100), (s.B, 40))).Id());
        await Stock.TransferAsync(http, s.W1, s.W2, s.A, 25);
        var entries = (await Stock.LedgerAsync(http)).Total();

        async Task AssertNothingChangedAsync(string after)
        {
            var balanced = await Balance.AssertBalancedAsync(s.U.S, $"after {after}", dw);
            Assert.True(balanced[(s.A, s.W1)] == 75m && balanced[(s.B, s.W1)] == 40m && balanced[(s.A, s.W2)] == 25m
                && balanced[(s.B, s.W2)] == 0m && balanced[(s.A, dw)] == 0m && balanced[(s.B, dw)] == 0m,
                $"After {after} the quantities changed: [{string.Join(", ", balanced)}]");
            Assert.Equal(entries, (await Stock.LedgerAsync(http)).Total());
        }

        var receipt = await Stock.CreateAsync(http, "receipt", s.W1, (s.A, 500));
        var issue = await Stock.CreateAsync(http, "issue", s.W1, (s.A, 70));
        await Stock.CreateTransferAsync(http, s.W1, s.W2, (s.B, 40));
        await Counts.CountAsync(http, s.W1, s.A, 1);
        await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.B, 9)).NoWarehouse());
        await AssertNothingChangedAsync("creating drafts");

        await Stock.ReplaceAsync(http, receipt.Id(), Stock.Replacement(s.W2, (s.B, 77)));
        await Stock.ReplaceAsync(http, issue.Id(), Stock.Replacement(s.W1, (s.A, 75), (s.B, 40)));
        await AssertNothingChangedAsync("replacing drafts");

        using (var deleted = await Stock.DeleteAsync(http, receipt.Id()))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var deleted = await Stock.DeleteAsync(http, issue.Id()))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertNothingChangedAsync("deleting drafts");

        foreach (var o in new[] { Po, So })
        {
            var order = await o.OrderedAsync(s, (s.A, 30, null, 1m), (s.B, 10, null, 1m));
            await AssertNothingChangedAsync($"confirming an order ({o})");
            await o.CloseAsync(http, order.Id());
            await AssertNothingChangedAsync($"closing an order ({o})");
            await o.ReopenAsync(http, order.Id());
            await AssertNothingChangedAsync($"reopening an order ({o})");
            await o.DraftDocumentAsync(http, order, 1, 5);
            await AssertNothingChangedAsync($"a draft document against an order ({o})");
        }

        await Stock.ReplaceArticleAsync(http, s.A, Stock.ArticleBody(s.U.S.ArticleA).With("name", "Renamed A"));
        await Stock.SetArticleActiveAsync(http, s.B, false);
        await AssertNothingChangedAsync("renaming an article and deactivating another");

        await Balance.SetDefaultAsync(http, s.W2);
        await AssertNothingChangedAsync("set-default");
        await Stock.SetWarehouseActiveAsync(http, dw, false);
        await MasterApi.Warehouses.CreateAsync(http, "W3", "Warehouse three");
        await AssertNothingChangedAsync("deactivating the former default and creating a warehouse");

        // Verify and rebuild are no postings either.
        Assert.Equal(0, await Balance.DifferencesAsync(http));
        Assert.Equal(0, (await Balance.RebuildAsync(http)).Corrected);
        await AssertNothingChangedAsync("verify and rebuild");
    }

    // ---- AC-53 ----

    [Fact]
    public async Task AC53_Verify_on_a_new_tenant_is_the_empty_list()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(Balance.Differences);

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        McpAssert.JsonEqual(Json("""{ "items": [], "total": 0, "limit": 50, "offset": 0 }"""), body);
        McpAssert.JsonEqual(Json("""{ "items": [], "total": 0, "limit": 5, "offset": 2 }"""),
            await Balance.DifferenceListAsync(tenant.Client, "?limit=5&offset=2"));
        McpAssert.JsonEqual(Json("""{ "items": [], "total": 0, "limit": 500, "offset": 0 }"""),
            await Balance.DifferenceListAsync(tenant.Client, "?limit=500"));
    }

    [Fact]
    public async Task AC53_Verify_after_every_kind_of_posting_is_the_empty_list()
    {
        var w = await WorldAsync();
        await RunSequenceAsync(w);

        McpAssert.JsonEqual(Json("""{ "items": [], "total": 0, "limit": 50, "offset": 0 }"""), await Balance.DifferenceListAsync(w.Http));
        // Asking twice changes nothing: verify only reads.
        McpAssert.JsonEqual(Json("""{ "items": [], "total": 0, "limit": 50, "offset": 0 }"""), await Balance.DifferenceListAsync(w.Http));
    }

    // ---- AC-54 ----

    [Fact]
    public async Task AC54_E15_Rebuild_on_a_new_tenant_finds_no_pair()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await Balance.SendRebuildAsync(tenant.Client);

        McpAssert.JsonEqual(Json("""{ "pairs": 0, "corrected": 0 }"""), await HttpAssert.JsonAsync(response, HttpStatusCode.OK));

        // Masters and drafts are no pairs.
        var s = await Stock.SetupAsync(app, tenant);
        await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5));
        Assert.Equal((0, 0), await Balance.RebuildAsync(s.Http));
    }

    [Fact]
    public async Task AC54_Rebuild_takes_a_body_of_nothing_only()
    {
        // S2: the stored balance is not an input anywhere.
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        using var response = await s.Http.PostAsJsonAsync(Balance.Rebuild, new { articleId = s.A, warehouseId = s.W1, quantity = 99 });

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.OK, $"Unexpected status {(int)response.StatusCode}.");
        Assert.Equal(10m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W1));
        await Balance.AssertBalancedAsync(s, "after a rebuild that was sent a quantity");
    }

    [Fact]
    public async Task AC54_Rebuild_on_a_healthy_tenant_corrects_nothing_and_changes_nothing()
    {
        var w = await WorldAsync();
        await RunSequenceAsync(w);
        var http = w.Http;

        async Task<JsonElement[][]> SnapshotAsync()
        {
            var lists = new List<JsonElement[]>();
            foreach (var warehouse in w.Warehouses)
                lists.Add(await Balance.StockListAsync(http, warehouse));
            lists.Add(await Balance.AllAsync(http, Stock.OnHand));
            lists.Add(await Balance.AllAsync(http, Stock.Documents));
            lists.Add(await Balance.LedgerAsync(http));
            lists.Add(await Balance.AllAsync(http, Po.Path));
            lists.Add(await Balance.AllAsync(http, So.Path));
            return lists.ToArray();
        }

        var before = await SnapshotAsync();
        var ledger = await Balance.LedgerAsync(http);
        var pairs = Balance.Sums(ledger).Count;
        // (A, W1), (B, W1), (C, W1), (A, W2) — moved in and out to zero —, (A, DW).
        Assert.Equal(5, pairs);

        using var response = await Balance.SendRebuildAsync(http);
        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(new[] { "corrected", "pairs" }, body.PropertyNames());
        Assert.Equal(0, body.GetProperty("corrected").GetInt32());
        Assert.Equal(pairs, body.GetProperty("pairs").GetInt32());

        var after = await SnapshotAsync();
        Assert.Equal(before.Length, after.Length);
        for (var list = 0; list < before.Length; list++)
        {
            Assert.True(before[list].Length == after[list].Length, $"List {list} has {after[list].Length} items after the rebuild, had {before[list].Length}.");
            for (var i = 0; i < before[list].Length; i++)
                McpAssert.JsonEqual(before[list][i], after[list][i], $"Rebuild changed item {i} of list {list}");
        }
        Assert.Equal(ledger.Length, (await Stock.LedgerAsync(http)).Total());
        await w.BalancedAsync("after the rebuild");

        Assert.Equal((pairs, 0), await Balance.RebuildAsync(http));
        Assert.Equal((pairs, 0), await Balance.RebuildAsync(http));

        // The next numbers continue where they were: rebuild took no number and posted nothing.
        foreach (var type in new[] { "receipt", "issue" })
        {
            var numbers = (await Balance.AllAsync(http, $"{Stock.Documents}?type={type}"))
                .Select(d => d.Number()).Where(n => n is not null).Select(n => n!).ToArray();
            var prefix = numbers[0][..(numbers[0].LastIndexOf('-') + 1)];
            var last = numbers.Max(n => int.Parse(n[prefix.Length..]));
            Assert.Equal(numbers.Length, last);
            var next = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, type, w.S.W1, (w.S.A, 1))).Id());
            Assert.Equal($"{prefix}{last + 1:000000}", next.Number());
        }
        Assert.Equal((pairs, 0), await Balance.RebuildAsync(http));
        await w.BalancedAsync("after postings that followed the rebuild");
    }

    // ---- AC-59 ----

    [Fact]
    public async Task AC59_The_stock_check_reads_what_the_list_shows()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 4);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 6);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 50);
        Assert.Equal(10m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W1));

        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        Assert.Equal(0m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W1));

        var one = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1));
        using (var refused = await Stock.SendPostAsync(s.Http, one.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");

        var count = await Counts.CountAsync(s.Http, s.W1, s.A, 3);
        Assert.Equal(0m, count.DocumentLines()[0].Book());
        Assert.Equal(50m, (await Counts.CountAsync(s.Http, s.W2, s.A, 50)).DocumentLines()[0].Book());

        // The counted 3 become the stock; then exactly 3 can be issued.
        await Stock.PostDocumentAsync(s.Http, count.Id());
        Assert.Equal(3m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W1));
        await Stock.PostDocumentAsync(s.Http, one.Id());
        await Stock.IssueAsync(s.Http, s.W1, s.A, 2);
        var four = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 0.000001m));
        using (var refused = await Stock.SendPostAsync(s.Http, four.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        await Balance.AssertBalancedAsync(s, "at the end");
    }
}
