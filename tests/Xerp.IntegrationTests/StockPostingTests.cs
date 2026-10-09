using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-30 to AC-37 (posting a receipt) and AC-40 to AC-48 (posting an issue): posting assigns the
/// number, writes one ledger entry per line, makes the document immutable and never lets stock go negative.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockPostingTests(XerpFixture app)
{
    private static async Task<JsonElement[]> EntriesOfAsync(HttpClient client, Guid document) =>
        (await Stock.LedgerAsync(client, $"?documentId={document}")).Items();

    // ---- receipts ----

    [Fact]
    public async Task AC30_Posting_a_receipt_assigns_number_and_posting_data_and_keeps_the_lines()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 100));
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal(draft.Id(), posted.Id());
        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal("receipt", posted.Str("type"));
        Assert.Equal("SR-000001", posted.Number());
        Assert.InRange(posted.GetProperty("postedAt").GetDateTimeOffset(), before, DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(s.Tenant.ApiKeyId, posted.GetProperty("postedBy").GetGuid());
        McpAssert.JsonEqual(draft.GetProperty("lines"), posted.GetProperty("lines"), "Posting changed the lines");
        McpAssert.JsonEqual(draft.GetProperty("warehouse"), posted.GetProperty("warehouse"));
        Assert.Equal(draft.Str("documentDate"), posted.Str("documentDate"));
        Assert.Equal(draft.GetProperty("createdBy").GetGuid(), posted.GetProperty("createdBy").GetGuid());

        McpAssert.JsonEqual(posted, await Stock.GetAsync(s.Http, draft.Id()));
        foreach (var number in new[] { "SR-000001", "sr-000001" })
        {
            using var byNumber = await Stock.ByNumberAsync(s.Http, number);
            McpAssert.JsonEqual(posted, await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task AC31_A_posted_receipt_is_one_ledger_entry_and_one_item_of_stock_on_hand()
    {
        var s = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var onHand = await Stock.OnHandAsync(s.Http);
        var item = Assert.Single(onHand.Items());
        Assert.Equal(1, onHand.Total());
        Assert.Equal(["A", "Article A"], new[] { item.GetProperty("article").Str("code"), item.GetProperty("article").Str("name") });
        Assert.Equal(s.A, item.GetProperty("article").Id());
        Assert.Equal(s.W1, item.GetProperty("warehouse").Id());
        Assert.Equal("W1", item.GetProperty("warehouse").Str("code"));
        Assert.Equal(s.UnitId, item.GetProperty("unit").Id());
        Assert.Equal("pcs", item.GetProperty("unit").Str("code"));
        Assert.Equal(100m, item.Quantity());

        var ledger = await Stock.LedgerAsync(s.Http);
        var entry = Assert.Single(ledger.Items());
        Assert.Equal(1, ledger.Total());
        Assert.NotEqual(Guid.Empty, entry.Id());
        Assert.Equal(100m, entry.Quantity());
        Assert.Equal(s.A, entry.GetProperty("article").Id());
        Assert.Equal(s.W1, entry.GetProperty("warehouse").Id());
        Assert.Equal("pcs", entry.GetProperty("unit").Str("code"));
        Assert.Equal(Stock.Date, entry.Str("documentDate"));
        Assert.Equal(posted.Id(), entry.GetProperty("document").Id());
        Assert.Equal("SR-000001", entry.GetProperty("document").Str("number"));
        Assert.Equal("receipt", entry.GetProperty("document").Str("type"));
        Assert.Equal(1, entry.GetProperty("lineNo").GetInt32());
        Assert.Equal(s.Tenant.ApiKeyId, entry.GetProperty("postedBy").GetGuid());
        Assert.Equal(posted.GetProperty("postedAt").GetDateTimeOffset(), entry.GetProperty("postedAt").GetDateTimeOffset());
        Assert.False(entry.TryGetProperty("tenantId", out _));
    }

    [Fact]
    public async Task AC32_One_ledger_entry_per_line_with_the_same_posting_time()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1), (s.B, 2), (s.A, 3));

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        var entries = await EntriesOfAsync(s.Http, posted.Id());
        Assert.Equal(3, entries.Length);
        Assert.Equal(3, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(e => e.GetProperty("lineNo").GetInt32()).ToArray());
        Assert.Equal(new[] { 1m, 2m, 3m }, entries.Select(e => e.Quantity()).ToArray());
        Assert.Equal(new[] { s.A, s.B, s.A }, entries.Select(e => e.GetProperty("article").Id()).ToArray());
        Assert.Single(entries.Select(e => e.GetProperty("postedAt").GetDateTimeOffset()).Distinct());
        Assert.Equal(3, entries.Select(e => e.Id()).Distinct().Count());
        Assert.All(entries, e => Assert.Equal(s.W1, e.GetProperty("warehouse").Id()));
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(2m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
    }

    [Fact]
    public async Task AC33_Created_by_one_key_and_posted_by_another()
    {
        var s = await Stock.SetupAsync(app);
        var k1 = await Keys.CreateAsync(app, s.Http, "clerk", "human");
        var k2 = await Keys.CreateAsync(app, s.Http, "poster", "agent");
        var draft = await Stock.CreateAsync(k1.Client, "receipt", s.W1, (s.A, 5));

        var posted = await Stock.PostDocumentAsync(k2.Client, draft.Id());

        Assert.Equal(k1.Id, posted.GetProperty("createdBy").GetGuid());
        Assert.Equal(k2.Id, posted.GetProperty("postedBy").GetGuid());
        var entry = Assert.Single(await EntriesOfAsync(s.Http, posted.Id()));
        Assert.Equal(k2.Id, entry.GetProperty("postedBy").GetGuid());
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    public async Task AC34_A_posted_document_is_immutable(string type)
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 50);
        var posted = await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, type, s.W1, (s.A, 20))).Id());
        var stock = await Stock.OnHandAsync(s.Http);
        var ledger = await Stock.LedgerAsync(s.Http);

        using var put = await Stock.PutAsync(s.Http, posted.Id(), Stock.Replacement(s.W2, (s.B, 1)));
        using var putSame = await Stock.PutAsync(s.Http, posted.Id(), Stock.Replacement(s.W1, (s.A, 20)));
        using var delete = await Stock.DeleteAsync(s.Http, posted.Id());
        using var postAgain = await Stock.SendPostAsync(s.Http, posted.Id());

        foreach (var response in new[] { put, putSame, delete, postAgain })
            await Stock.ConflictAsync(response, "INVALID_STATE");
        await Stock.AssertUnchangedAsync(s.Http, posted);
        McpAssert.JsonEqual(stock, await Stock.OnHandAsync(s.Http), "Stock on hand changed");
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "The ledger changed");
    }

    [Fact]
    public async Task AC34_R8_Validation_precedes_the_state_check_on_a_posted_document()
    {
        var s = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        using var invalid = await Stock.PutAsync(s.Http, posted.Id(), Stock.Replacement(s.W1, (s.A, 0)));
        using var badReference = await Stock.PutAsync(s.Http, posted.Id(), Stock.Replacement(Guid.NewGuid(), (s.S, 1)));

        await HttpAssert.ValidationAsync(invalid, "lines[0].quantity");
        // The state is checked before the references.
        await Stock.ConflictAsync(badReference, "INVALID_STATE");
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("PUT", "/{id}")]
    [InlineData("PATCH", "/{id}")]
    [InlineData("DELETE", "/{id}")]
    [InlineData("DELETE", "")]
    public async Task AC34_S3_No_operation_creates_changes_or_deletes_a_ledger_entry(string method, string path)
    {
        // Section 4.3; an unsupported method is 404 NOT_FOUND (spec 001 as amended).
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var ledger = await Stock.LedgerAsync(s.Http);
        var entry = Assert.Single(ledger.Items());
        var request = new HttpRequestMessage(new HttpMethod(method), Stock.Ledger + path.Replace("{id}", entry.Id().ToString()));
        if (method != "DELETE")
            request.Content = HttpAssert.Raw($$"""{ "articleId": "{{s.A}}", "warehouseId": "{{s.W1}}", "quantity": 1000 }""");

        using var response = await s.Http.SendAsync(request);

        await HttpAssert.NotFoundAsync(response);
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "The ledger changed");
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC35_Warehouses_keep_separate_stock()
    {
        var s = await Stock.SetupAsync(app);

        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 7);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(7m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        var list = await Stock.OnHandAsync(s.Http, $"?articleId={s.A}");
        Assert.Equal(2, list.Total());
        Assert.Equal(new[] { "W1", "W2" }, list.Items().Select(i => i.GetProperty("warehouse").Str("code")).ToArray());
        Assert.Equal(new[] { 5m, 7m }, list.Items().Select(i => i.Quantity()).ToArray());
    }

    [Fact]
    public async Task AC36_Quantities_are_exact_decimals()
    {
        var s = await Stock.SetupAsync(app);

        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 0.1m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 0.2m);

        Assert.Equal(0.3m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.IssueAsync(s.Http, s.W1, s.A, 0.3m);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Empty((await Stock.OnHandAsync(s.Http)).Items());
        Assert.Equal(0m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC36_R6_The_smallest_quantity_is_not_rounded_away()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1m);

        using var tooMuch = await Stock.SendPostAsync(s.Http,
            (await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1.000001m))).Id());
        await Stock.IssueAsync(s.Http, s.W1, s.A, 0.999999m);

        await Stock.ConflictAsync(tooMuch, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(0.000001m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC37_Any_document_date_posts_and_is_carried_by_the_ledger_entry()
    {
        var s = await Stock.SetupAsync(app);
        var past = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("documentDate", "1999-12-31"));
        var future = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 2)).With("documentDate", "2099-01-01"));

        // The future-dated receipt is posted first: the ledger is ordered by posting time, not by date.
        var postedFuture = await Stock.PostDocumentAsync(s.Http, future.Id());
        var postedPast = await Stock.PostDocumentAsync(s.Http, past.Id());

        Assert.Equal("2099-01-01", postedFuture.Str("documentDate"));
        Assert.Equal("1999-12-31", postedPast.Str("documentDate"));
        Assert.Equal("2099-01-01", Assert.Single(await EntriesOfAsync(s.Http, future.Id())).Str("documentDate"));
        Assert.Equal("1999-12-31", Assert.Single(await EntriesOfAsync(s.Http, past.Id())).Str("documentDate"));
        Assert.Equal(3m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- issues ----

    [Fact]
    public async Task AC40_An_issue_within_stock_posts_with_a_negative_ledger_entry()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 40);

        Assert.Equal("SI-000001", issue.Number());
        Assert.Equal("posted", issue.Str("status"));
        Assert.Equal(40m, Assert.Single(issue.DocumentLines()).Quantity());
        var entry = Assert.Single(await EntriesOfAsync(s.Http, issue.Id()));
        Assert.Equal(-40m, entry.Quantity());
        Assert.Equal("issue", entry.GetProperty("document").Str("type"));
        Assert.Equal("SI-000001", entry.GetProperty("document").Str("number"));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC41_An_issue_beyond_stock_is_refused_and_the_corrected_draft_posts()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 40);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 61));
        var ledger = await Stock.LedgerAsync(s.Http);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        await Stock.AssertUnchangedAsync(s.Http, draft);
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "A refused posting wrote to the ledger");

        await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 60)));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SI-000002", posted.Number());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Empty((await Stock.OnHandAsync(s.Http)).Items()); // E8
    }

    [Fact]
    public async Task AC42_Stock_in_another_warehouse_does_not_count()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W2, (s.A, 1));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
    }

    [Fact]
    public async Task AC42_R15_Stock_of_another_article_does_not_count()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.B, 1));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(1, (await Stock.LedgerAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC43_Lines_of_the_same_article_are_summed_against_stock()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var tooMuch = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 6), (s.A, 6));
        var exact = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 6), (s.A, 4));

        using var refused = await Stock.SendPostAsync(s.Http, tooMuch.Id());
        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity", "lines[1].quantity");
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var posted = await Stock.PostDocumentAsync(s.Http, exact.Id());

        Assert.Equal(new[] { -6m, -4m }, (await EntriesOfAsync(s.Http, posted.Id())).Select(e => e.Quantity()).ToArray());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC44_Only_the_short_lines_are_reported_and_nothing_is_posted()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 5), (s.B, 1));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        var problem = await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[1].quantity");
        Assert.DoesNotContain("lines[0].quantity", McpAssert.ErrorKeys(problem));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Empty(await EntriesOfAsync(s.Http, draft.Id()));
        Assert.Equal(1, (await Stock.LedgerAsync(s.Http)).Total());
        await Stock.AssertDraftAsync(s.Http, draft.Id());
    }

    [Fact]
    public async Task AC44_R15_Every_line_of_every_short_article_is_reported()
    {
        // Lines: A 4 (covered), B 3, A 3 (covered together: 7 of 10), B 3 (B has 5: short on both B lines).
        var s = await Stock.SetupAsync(app);
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10), (s.B, 5))).Id());
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 4), (s.B, 3), (s.A, 3), (s.B, 3));

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        var problem = await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[1].quantity", "lines[3].quantity");
        Assert.Equal(new[] { "lines[1].quantity", "lines[3].quantity" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
    }

    [Fact]
    public async Task AC45_A_draft_is_checked_against_stock_at_the_moment_of_posting()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var first = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 10));
        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);

        using var refused = await Stock.SendPostAsync(s.Http, first.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertUnchangedAsync(s.Http, first);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var posted = await Stock.PostDocumentAsync(s.Http, first.Id());

        Assert.Equal("SI-000002", posted.Number());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC46_Parallel_issues_never_overdraw_the_stock()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var drafts = new List<Guid>();
        for (var i = 0; i < 10; i++)
            drafts.Add((await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 3))).Id());

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal(Enumerable.Repeat(200, 3).Concat(Enumerable.Repeat(409, 7)).ToArray(), statuses);
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        var numbers = new List<string?>();
        foreach (var accepted in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
            numbers.Add((await HttpAssert.JsonAsync(accepted, HttpStatusCode.OK)).Number());
        Assert.Equal(new[] { "SI-000001", "SI-000002", "SI-000003" }, numbers.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var issues = (await Stock.LedgerAsync(s.Http)).Items().Where(e => e.GetProperty("document").Str("type") == "issue").ToArray();
        Assert.Equal(3, issues.Length);
        Assert.All(issues, e => Assert.Equal(-3m, e.Quantity()));
        Assert.Equal(1m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        Assert.Equal(7, (await Stock.DocumentsAsync(s.Http, "?type=issue&status=draft")).Total());
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task AC46_R16_Parallel_issues_with_several_articles_in_opposite_order_never_overdraw()
    {
        // Each document takes 4 of A and 4 of B, half of them listing B first; stock covers two documents.
        var s = await Stock.SetupAsync(app);
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10), (s.B, 10))).Id());
        var drafts = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var (first, second) = i % 2 == 0 ? (s.A, s.B) : (s.B, s.A);
            drafts.Add((await Stock.CreateAsync(s.Http, "issue", s.W1, (first, 4), (second, 4))).Id());
        }

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        Assert.Equal(2m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(2m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(2m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        Assert.Equal(2m, await Stock.LedgerSumAsync(s.Http, s.B, s.W1));
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task AC47_The_document_date_plays_no_part_in_the_stock_check()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        var backDated = await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 5)).With("documentDate", "2000-01-01"));
        var futureDated = await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 1)).With("documentDate", "2099-01-01"));

        var posted = await Stock.PostDocumentAsync(s.Http, backDated.Id());
        using var refused = await Stock.SendPostAsync(s.Http, futureDated.Id());

        Assert.Equal("2000-01-01", posted.Str("documentDate"));
        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC48_Posting_needs_an_active_warehouse()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(refused, "REFERENCE_INACTIVE", "warehouseId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());

        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SR-000001", posted.Number());
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC48_Posting_needs_active_articles()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5), (s.B, 6));
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        var problem = await Stock.ConflictAsync(refused, "REFERENCE_INACTIVE", "lines[1].articleId");
        Assert.DoesNotContain("lines[0].articleId", McpAssert.ErrorKeys(problem));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());

        await Stock.SetArticleActiveAsync(s.Http, s.B, true);
        await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
    }

    [Fact]
    public async Task AC48_R13_Inactive_masters_are_reported_before_insufficient_stock()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 5));
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(refused, "REFERENCE_INACTIVE", "lines[0].articleId");
    }

    [Fact]
    public async Task AC48_R25_Existing_stock_of_a_deactivated_article_stays_on_hand()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);

        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(5m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }
}
