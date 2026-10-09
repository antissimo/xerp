using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-50 to AC-53 (numbering: per tenant and type, gapless, in posting order) and AC-60 to AC-64
/// (stock on hand equals the sum of the ledger; ledger, stock and document queries).
/// </summary>
[Collection(XerpCollection.Name)]
public class StockNumberingAndQueryTests(XerpFixture app)
{
    private static string[] Numbers(IEnumerable<JsonElement> documents) =>
        documents.Select(d => d.Number()!).Order(StringComparer.Ordinal).ToArray();

    private static string[] Sequence(string prefix, int count) =>
        Enumerable.Range(1, count).Select(n => $"{prefix}-{n:000000}").ToArray();

    // ---- numbering ----

    [Fact]
    public async Task AC50_Each_type_has_its_own_gapless_sequence()
    {
        var s = await Stock.SetupAsync(app);

        var receipts = new List<JsonElement>();
        for (var i = 0; i < 3; i++)
            receipts.Add(await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10));
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 1);
        var nextReceipt = await Stock.ReceiveAsync(s.Http, s.W2, s.B, 1);

        Assert.Equal(new[] { "SR-000001", "SR-000002", "SR-000003" }, receipts.Select(r => r.Number()).ToArray());
        Assert.Equal("SI-000001", issue.Number());
        Assert.Equal("SR-000004", nextReceipt.Number());
    }

    [Fact]
    public async Task AC50_R17_Numbers_follow_posting_order_not_creation_order_or_document_date()
    {
        var s = await Stock.SetupAsync(app);
        var createdFirst = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("documentDate", "2020-01-01"));
        var createdSecond = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("documentDate", "2030-01-01"));

        var postedFirst = await Stock.PostDocumentAsync(s.Http, createdSecond.Id());
        var postedSecond = await Stock.PostDocumentAsync(s.Http, createdFirst.Id());

        Assert.Equal("SR-000001", postedFirst.Number());
        Assert.Equal("SR-000002", postedSecond.Number());
    }

    [Fact]
    public async Task AC51_Failed_postings_and_drafts_consume_no_number()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        Assert.Equal("SI-000001", (await Stock.IssueAsync(s.Http, s.W1, s.A, 1)).Number());

        var tooMuch = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1000));
        using (var refused = await Stock.SendPostAsync(s.Http, tooMuch.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        var deleted = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1));
        using (var response = await Stock.DeleteAsync(s.Http, deleted.Id()))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1)); // left unposted
        var inactive = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.B, 1));
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);
        using (var refused = await Stock.SendPostAsync(s.Http, inactive.Id()))
            await Stock.ConflictAsync(refused, "REFERENCE_INACTIVE");
        using (var again = await Stock.SendPostAsync(s.Http, (await Stock.DocumentsAsync(s.Http, "?status=posted&type=issue")).Items()[0].Id()))
            await Stock.ConflictAsync(again, "INVALID_STATE");

        var next = await Stock.IssueAsync(s.Http, s.W1, s.A, 1);

        Assert.Equal("SI-000002", next.Number());
        Assert.Equal("SR-000002", (await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1)).Number());
    }

    [Fact]
    public async Task AC52_Parallel_receipts_get_unique_gapless_numbers()
    {
        var s = await Stock.SetupAsync(app);
        var drafts = new List<Guid>();
        for (var i = 0; i < 10; i++)
            drafts.Add((await Stock.CreateAsync(s.Http, "receipt", i % 2 == 0 ? s.W1 : s.W2, (i % 3 == 0 ? s.B : s.A, 1))).Id());

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        var posted = new List<JsonElement>();
        foreach (var response in responses)
        {
            posted.Add(await HttpAssert.JsonAsync(response, HttpStatusCode.OK));
            response.Dispose();
        }
        Assert.Equal(Sequence("SR", 10), Numbers(posted));
        Assert.Equal(10, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Equal("SR-000011", (await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1)).Number());
    }

    [Fact]
    public async Task AC52_R17_Parallel_receipts_and_issues_keep_both_sequences_gapless()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100); // SR-000001
        var drafts = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            drafts.Add((await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1))).Id());
            drafts.Add((await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1))).Id());
        }

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        var posted = new List<JsonElement>();
        foreach (var response in responses)
        {
            posted.Add(await HttpAssert.JsonAsync(response, HttpStatusCode.OK));
            response.Dispose();
        }
        Assert.Equal(Sequence("SR", 7).Skip(1).ToArray(), Numbers(posted.Where(d => d.Str("type") == "receipt")));
        Assert.Equal(Sequence("SI", 6), Numbers(posted.Where(d => d.Str("type") == "issue")));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(100m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC53_Posting_the_same_draft_twice_in_parallel_posts_it_once()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5), (s.B, 6));

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Stock.SendPostAsync(s.Http, draft.Id()))));

        Assert.Equal(new[] { 200, 409 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
        await Stock.ConflictAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), "INVALID_STATE");
        var posted = await HttpAssert.JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK);
        Assert.Equal("SR-000001", posted.Number());
        var entries = (await Stock.LedgerAsync(s.Http, $"?documentId={draft.Id()}")).Items();
        Assert.Equal(new[] { 1, 2 }, entries.Select(e => e.GetProperty("lineNo").GetInt32()).ToArray());
        Assert.Equal(2, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal("SR-000002", (await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1)).Number());
        foreach (var response in responses)
            response.Dispose();
    }

    // ---- ledger equals stock ----

    private static async Task AssertLedgerEqualsStockAsync(StockSetup s)
    {
        var onHand = (await Stock.OnHandAsync(s.Http, "?limit=500")).Items();
        var listedPairs = 0;
        foreach (var article in new[] { s.A, s.B })
            foreach (var warehouse in new[] { s.W1, s.W2 })
            {
                var sum = await Stock.LedgerSumAsync(s.Http, article, warehouse);
                Assert.True(sum >= 0, $"The ledger of a pair sums to {sum}: stock is negative.");
                Assert.Equal(sum, await Stock.QuantityAsync(s.Http, article, warehouse));
                var listed = onHand.Any(i => i.GetProperty("article").Id() == article && i.GetProperty("warehouse").Id() == warehouse);
                Assert.Equal(sum != 0, listed);
                if (listed)
                    listedPairs++;
            }
        Assert.Equal(listedPairs, onHand.Length);
    }

    [Fact]
    public async Task AC60_Stock_on_hand_equals_the_sum_of_the_ledger_after_every_posting()
    {
        var s = await Stock.SetupAsync(app);
        // (type, warehouse, lines, expected to post)
        var steps = new (string Type, Guid Warehouse, (Guid, decimal)[] Lines, bool Posts)[]
        {
            ("receipt", s.W1, [(s.A, 10m), (s.B, 4.5m)], true),
            ("receipt", s.W2, [(s.A, 3m)], true),
            ("issue", s.W1, [(s.A, 2.25m), (s.A, 0.75m)], true),
            ("issue", s.W2, [(s.B, 1m)], false), // B was never received into W2
            ("issue", s.W1, [(s.B, 4.5m)], true), // B in W1 back to zero
            ("issue", s.W1, [(s.A, 7m), (s.B, 0.000001m)], false), // A is covered, B is not: nothing is posted
            ("receipt", s.W2, [(s.B, 0.000001m)], true),
            ("issue", s.W2, [(s.A, 3m), (s.B, 0.000001m)], true), // W2 back to zero for both
            ("issue", s.W1, [(s.A, 7.000001m)], false),
            ("issue", s.W1, [(s.A, 6.999999m)], true),
        };

        foreach (var step in steps)
        {
            var draft = await Stock.CreateAsync(s.Http, step.Type, step.Warehouse, step.Lines);
            await AssertLedgerEqualsStockAsync(s);
            using var response = await Stock.SendPostAsync(s.Http, draft.Id());
            if (step.Posts)
                await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
            else
                await Stock.ConflictAsync(response, "INSUFFICIENT_STOCK");
            await AssertLedgerEqualsStockAsync(s);
        }

        Assert.Equal(0.000001m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var onHand = await Stock.OnHandAsync(s.Http);
        Assert.Equal(1, onHand.Total());
        // Seven postings: 2 + 1 + 2 + 1 + 1 + 2 + 1 lines.
        Assert.Equal(10, (await Stock.LedgerAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC60_R19_Drafts_replacements_and_deletions_never_change_stock_or_the_ledger()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var stock = await Stock.OnHandAsync(s.Http);
        var ledger = await Stock.LedgerAsync(s.Http);

        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 4));
        await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W2, (s.B, 9)));
        using var deleted = await Stock.DeleteAsync(s.Http, draft.Id());
        await Stock.ReplaceArticleAsync(s.Http, s.A, Stock.ArticleBody(s.ArticleA).With("name", "Renamed"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(1, (await Stock.OnHandAsync(s.Http)).Total());
        Assert.Equal(ledger.Total(), (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Equal(stock.Items()[0].Quantity(), (await Stock.OnHandAsync(s.Http)).Items()[0].Quantity());
    }

    // ---- ledger query ----

    [Fact]
    public async Task AC61_Ledger_is_ordered_oldest_first_and_filters_combine()
    {
        var s = await Stock.SetupAsync(app);
        // The later document is created first, so that creation order and posting order differ.
        var second = await Stock.CreateAsync(s.Http, "receipt", s.W2, (s.B, 2), (s.A, 3));
        var first = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10), (s.B, 20), (s.A, 30));
        await Stock.PostDocumentAsync(s.Http, first.Id());
        await Stock.PostDocumentAsync(s.Http, second.Id());
        var third = await Stock.IssueAsync(s.Http, s.W1, s.A, 5);

        var all = await Stock.LedgerAsync(s.Http);
        var ofFirst = await Stock.LedgerAsync(s.Http, $"?documentId={first.Id()}");
        var aInW1 = await Stock.LedgerAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}");
        var aInW2OfFirst = await Stock.LedgerAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W2}&documentId={first.Id()}");
        var paged = await Stock.LedgerAsync(s.Http, "?limit=2&offset=3");

        Assert.Equal(6, all.Total());
        Assert.Equal(
            new[] { (first.Id(), 1), (first.Id(), 2), (first.Id(), 3), (second.Id(), 1), (second.Id(), 2), (third.Id(), 1) },
            all.Items().Select(e => (e.GetProperty("document").Id(), e.GetProperty("lineNo").GetInt32())).ToArray());
        Assert.Equal(new[] { 10m, 20m, 30m, 2m, 3m, -5m }, all.Items().Select(e => e.Quantity()).ToArray());
        var postedAt = all.Items().Select(e => e.GetProperty("postedAt").GetDateTimeOffset()).ToArray();
        Assert.Equal(postedAt.Order().ToArray(), postedAt);

        Assert.Equal(3, ofFirst.Total());
        Assert.Equal(new[] { 1, 2, 3 }, ofFirst.Items().Select(e => e.GetProperty("lineNo").GetInt32()).ToArray());
        Assert.All(ofFirst.Items(), e => Assert.Equal(first.Id(), e.GetProperty("document").Id()));
        Assert.Equal(new[] { 10m, 30m, -5m }, aInW1.Items().Select(e => e.Quantity()).ToArray());
        Assert.Equal(3, aInW1.Total());
        Assert.Equal(0, aInW2OfFirst.Total());
        Assert.Equal(new[] { 2m, 3m }, paged.Items().Select(e => e.Quantity()).ToArray());
        Assert.Equal(6, paged.Total());
    }

    [Theory]
    [InlineData("articleId")]
    [InlineData("warehouseId")]
    [InlineData("documentId")]
    public async Task AC61_A_random_id_in_a_ledger_filter_gives_an_empty_list_and_a_malformed_one_is_rejected(string filter)
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        var unknown = await Stock.LedgerAsync(s.Http, $"?{filter}={Guid.NewGuid()}");
        using var malformed = await s.Http.GetAsync($"{Stock.Ledger}?{filter}=abc");

        Assert.Empty(unknown.Items());
        Assert.Equal(0, unknown.Total());
        await HttpAssert.ValidationAsync(malformed, filter);
    }

    // ---- stock on hand query ----

    [Fact]
    public async Task AC62_Stock_on_hand_is_ordered_by_article_code_then_warehouse_code_and_filters_and_pages()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W2, s.B, 4);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 2);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 3);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);

        var all = await Stock.OnHandAsync(s.Http);
        var w2 = await Stock.OnHandAsync(s.Http, $"?warehouseId={s.W2}");
        var b = await Stock.OnHandAsync(s.Http, $"?articleId={s.B}");
        var page = await Stock.OnHandAsync(s.Http, "?limit=1&offset=1");
        var unknown = await Stock.OnHandAsync(s.Http, $"?warehouseId={Guid.NewGuid()}");
        using var malformed = await s.Http.GetAsync($"{Stock.OnHand}?warehouseId=abc");

        static (string, string, decimal)[] Rows(JsonElement list) => list.Items()
            .Select(i => (i.GetProperty("article").Str("code"), i.GetProperty("warehouse").Str("code"), i.Quantity())).ToArray();
        Assert.Equal(new[] { ("A", "W1", 1m), ("A", "W2", 2m), ("B", "W1", 3m), ("B", "W2", 4m) }, Rows(all));
        Assert.Equal(4, all.Total());
        Assert.Equal(new[] { ("A", "W2", 2m), ("B", "W2", 4m) }, Rows(w2));
        Assert.Equal(2, w2.Total());
        Assert.Equal(new[] { ("B", "W1", 3m), ("B", "W2", 4m) }, Rows(b));
        Assert.Equal(new[] { ("A", "W2", 2m) }, Rows(page));
        Assert.Equal(4, page.Total());
        Assert.Equal(1, page.GetProperty("limit").GetInt32());
        Assert.Equal(1, page.GetProperty("offset").GetInt32());
        Assert.Equal(0, unknown.Total());
        await HttpAssert.ValidationAsync(malformed, "warehouseId");
    }

    [Fact]
    public async Task AC62_Stock_on_hand_orders_by_code_case_insensitively()
    {
        var s = await Stock.SetupAsync(app);
        var lower = await Art.CreateAsync(s.Http, "aa", "Lower-case code", s.UnitId);
        var upper = await Art.CreateAsync(s.Http, "AB", "Upper-case code", s.UnitId);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 1);
        await Stock.ReceiveAsync(s.Http, s.W1, upper.Id(), 1);
        await Stock.ReceiveAsync(s.Http, s.W1, lower.Id(), 1);

        var list = await Stock.OnHandAsync(s.Http);

        Assert.Equal(new[] { "aa", "AB", "B" }, list.Items().Select(i => i.GetProperty("article").Str("code")).ToArray());
    }

    // ---- document list ----

    [Fact]
    public async Task AC63_Document_list_is_newest_first_with_line_counts_and_filters_combine()
    {
        var s = await Stock.SetupAsync(app);
        var r1 = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10); // SR-000001
        var r2 = await Stock.PostDocumentAsync(s.Http,
            (await Stock.CreateAsync(s.Http, "receipt", s.W2, (s.A, 5), (s.B, 5), (s.A, 1))).Id()); // SR-000002
        var i1 = await Stock.IssueAsync(s.Http, s.W2, s.A, 1); // SI-000001
        var d1 = await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W2, (s.A, 1), (s.B, 1)).With("reference", "Delivery DN-77"));
        var d2 = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.B, 1)).With("reference", "sr-0000 lookalike"));

        async Task<Guid[]> IdsAsync(string query) => (await Stock.DocumentsAsync(s.Http, query)).Ids();
        var all = await Stock.DocumentsAsync(s.Http);

        Assert.Equal(new[] { d2.Id(), d1.Id(), i1.Id(), r2.Id(), r1.Id() }, all.Ids());
        Assert.Equal(5, all.Total());
        Assert.Equal(new[] { 1, 2, 1, 3, 1 }, all.Items().Select(d => d.GetProperty("lineCount").GetInt32()).ToArray());
        foreach (var item in all.Items())
        {
            Assert.False(item.TryGetProperty("lines", out _), $"A list item must not have 'lines': {item}");
            var full = await Stock.GetAsync(s.Http, item.Id());
            foreach (var property in full.EnumerateObject().Where(p => p.Name != "lines"))
                McpAssert.JsonEqual(property.Value, item.GetProperty(property.Name), $"List item differs from GET in '{property.Name}'");
        }

        Assert.Equal(new[] { d1.Id(), i1.Id() }, await IdsAsync("?type=issue"));
        Assert.Equal(new[] { d2.Id(), d1.Id() }, await IdsAsync("?status=draft"));
        Assert.Equal(new[] { i1.Id(), r2.Id(), r1.Id() }, await IdsAsync("?status=posted"));
        Assert.Equal(new[] { d1.Id(), i1.Id(), r2.Id() }, await IdsAsync($"?warehouseId={s.W2}"));
        Assert.Equal(new[] { d1.Id() }, await IdsAsync($"?type=issue&status=draft&warehouseId={s.W2}"));
        Assert.Empty(await IdsAsync($"?type=receipt&status=draft&warehouseId={s.W2}"));
        Assert.Empty(await IdsAsync($"?warehouseId={Guid.NewGuid()}"));
        // search: a posted document's number and a draft's reference, case-insensitively.
        Assert.Equal(new[] { d2.Id(), r2.Id(), r1.Id() }, await IdsAsync("?search=sr-0000"));
        Assert.Equal(new[] { r2.Id() }, await IdsAsync("?search=SR-000002"));
        Assert.Equal(new[] { d1.Id() }, await IdsAsync("?search=dn-77"));
        Assert.Equal(new[] { r2.Id() }, await IdsAsync("?search=sr-0000&status=posted&limit=1&offset=0"));
        Assert.Equal(3, (await Stock.DocumentsAsync(s.Http, "?search=sr-0000&limit=1")).Total());
    }

    [Theory]
    [InlineData("?type=transfer", "type")]
    [InlineData("?type=Receipt", "type")]
    [InlineData("?status=Posted", "status")]
    [InlineData("?warehouseId=abc", "warehouseId")]
    public async Task AC63_Invalid_document_filter_is_rejected_with_its_key(string query, string errorKey)
    {
        var s = await Stock.SetupAsync(app);

        using var response = await s.Http.GetAsync(Stock.Documents + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    // ---- current summaries ----

    [Fact]
    public async Task AC64_Posted_documents_ledger_and_stock_show_the_masters_current_code_and_name()
    {
        var s = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        await Stock.ReplaceArticleAsync(s.Http, s.A, Stock.ArticleBody(s.ArticleA).With("code", "A-NEW").With("name", "Renamed article"));
        await MasterApi.Warehouses.ReplaceAsync(s.Http, s.W1,
            Stock.WarehouseBody(s.Warehouse1).With("code", "W1-NEW").With("name", "Renamed warehouse"));

        var document = await Stock.GetAsync(s.Http, posted.Id());
        var entry = Assert.Single((await Stock.LedgerAsync(s.Http)).Items());
        var item = Assert.Single((await Stock.OnHandAsync(s.Http)).Items());
        var line = Assert.Single(document.DocumentLines());
        foreach (var article in new[] { line.GetProperty("article"), entry.GetProperty("article"), item.GetProperty("article") })
        {
            Assert.Equal(s.A, article.Id());
            Assert.Equal("A-NEW", article.Str("code"));
            Assert.Equal("Renamed article", article.Str("name"));
        }
        foreach (var warehouse in new[] { document.GetProperty("warehouse"), entry.GetProperty("warehouse"), item.GetProperty("warehouse") })
        {
            Assert.Equal(s.W1, warehouse.Id());
            Assert.Equal("W1-NEW", warehouse.Str("code"));
            Assert.Equal("Renamed warehouse", warehouse.Str("name"));
        }
        Assert.Equal(new[] { 100m, 100m, 100m }, new[] { line.Quantity(), entry.Quantity(), item.Quantity() });
        Assert.Equal(posted.Number(), document.Number());
        Assert.Equal(posted.Str("postedAt"), document.Str("postedAt"));
    }
}
