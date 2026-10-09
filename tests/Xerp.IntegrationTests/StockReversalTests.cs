using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 006, AC-10, AC-50 to AC-56 and AC-60 to AC-71: reversal — the only correction of a posted document. It
/// posts a linked reversing document whose ledger entries cancel the original's (net zero, R15), is whole and
/// final (R11, R18), never takes stock below zero (R16) and leaves the original otherwise untouched (R13).
/// </summary>
[Collection(XerpCollection.Name)]
public class StockReversalTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    private static string ReversePath(Guid id) => $"{Stock.Documents}/{id}/reverse";

    private static async Task<int> DocumentCountAsync(HttpClient client) => (await Stock.DocumentsAsync(client)).Total();

    private static async Task<int> LedgerCountAsync(HttpClient client) => (await Stock.LedgerAsync(client)).Total();

    private static (int LineNo, Guid Article, Guid Warehouse, decimal Quantity) Movement(JsonElement entry) =>
        (entry.GetProperty("lineNo").GetInt32(), entry.GetProperty("article").Id(), entry.GetProperty("warehouse").Id(), entry.Quantity());

    /// <summary>
    /// R13: the reversal changed <c>status</c> and <c>reversedBy</c> of the original and nothing else — not its
    /// lines, number, dates, <c>postedAt</c>, <c>postedBy</c> or <c>updatedAt</c>.
    /// </summary>
    private static async Task<JsonElement> AssertOnlyReversedAsync(HttpClient client, JsonElement before, JsonElement reversing)
    {
        var after = await Stock.GetAsync(client, before.Id());
        Assert.Equal("reversed", after.Str("status"));
        Stock.AssertLink(after, "reversedBy", reversing);
        Assert.Equal(before.PropertyNames(), after.PropertyNames());
        foreach (var property in before.EnumerateObject().Where(p => p.Name is not ("status" or "reversedBy")))
            McpAssert.JsonEqual(property.Value, after.GetProperty(property.Name), $"The reversal changed '{property.Name}' of the original");
        return after;
    }

    /// <summary>R14 and R15: the reversing entries are the original's with the opposite sign, and the pair sums to zero.</summary>
    private static async Task AssertCancelsAsync(HttpClient client, JsonElement original, JsonElement reversing)
    {
        var originals = await Stock.EntriesAsync(client, original.Id());
        var reversals = await Stock.EntriesAsync(client, reversing.Id());
        Assert.NotEmpty(originals);
        Assert.Equal(
            originals.Select(Movement).Select(m => (m.LineNo, m.Article, m.Warehouse, Quantity: -m.Quantity)).Order().ToArray(),
            reversals.Select(Movement).Order().ToArray());
        Assert.Empty(Stock.PairSums(originals.Concat(reversals)));
        Assert.All(originals, e => Assert.False(e.GetProperty("document").Bool("isReversal")));
        Assert.All(reversals, e =>
        {
            var document = e.GetProperty("document");
            Assert.True(document.Bool("isReversal"));
            Assert.Equal(reversing.Id(), document.Id());
            Assert.Equal(reversing.Number(), document.Str("number"));
            Assert.Equal(original.Str("type"), document.Str("type"));
            Assert.Equal(reversing.Str("documentDate"), e.Str("documentDate"));
            Assert.Equal(reversing.Str("postedAt"), e.Str("postedAt"));
            Assert.Equal(reversing.GetProperty("postedBy").GetGuid(), e.GetProperty("postedBy").GetGuid());
        });
    }

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_Reverse_needs_a_tenant_key()
    {
        using var anonymous = app.Anonymous();
        using var admin = app.Admin();
        var path = ReversePath(Guid.NewGuid());

        using var noCredential = await anonymous.PostAsync(path, HttpAssert.Raw("""{ "documentDate": "2026-10-09" }"""));
        using var adminKey = await admin.PostAsync(path, HttpAssert.Raw("""{ "documentDate": "2026-10-09" }"""));

        await HttpAssert.UnauthenticatedAsync(noCredential);
        await HttpAssert.ForbiddenAsync(adminKey);
    }

    [Fact]
    public async Task AC10_Reverse_rejects_an_unknown_query_parameter_and_an_unknown_body_property()
    {
        var s = await Stock.SetupAsync(app);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        using var query = await s.Http.PostAsync(ReversePath(receipt.Id()) + "?x=1", HttpAssert.Raw("""{ "documentDate": "2026-10-09" }"""));
        using var property = await Stock.SendReverseAsync(s.Http, receipt.Id(),
            Stock.ReverseBody().With("lines", Stock.Lines((s.A, 1))));

        await HttpAssert.ValidationAsync(query, "x");
        // The unknown property is reported under its own name (001/E4; 006-q T-Q5).
        await HttpAssert.ValidationAsync(property, "lines");
        await Stock.AssertUnchangedAsync(s.Http, receipt);
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- receipt and issue ----

    [Fact]
    public async Task AC50_Reversing_a_receipt_posts_a_linked_reversing_receipt()
    {
        var s = await Stock.SetupAsync(app);
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        Assert.Equal("SR-000001", original.Number());

        using var response = await Stock.SendReverseAsync(s.Http, original.Id(), Stock.NextDay, "wrong article");
        var reversing = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.NotEqual(original.Id(), reversing.Id());
        Assert.Equal("receipt", reversing.Str("type"));
        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal("SR-000002", reversing.Number());
        Assert.Equal("2026-10-10", reversing.Str("documentDate"));
        Assert.Equal("wrong article", reversing.Str("note"));
        Assert.Equal(s.W1, reversing.GetProperty("warehouse").Id());
        JsonBody.AssertNull(reversing, "toWarehouse", "reversedBy");
        var line = Assert.Single(reversing.DocumentLines());
        Assert.Equal(s.A, line.GetProperty("article").Id());
        Assert.Equal(100m, line.Quantity());
        Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
        Stock.AssertLink(reversing, "reversalOf", original);
        Assert.Equal("SR-000001", reversing.GetProperty("reversalOf").Str("number"));
        Assert.Equal(s.Tenant.ApiKeyId, reversing.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, reversing.GetProperty("postedBy").GetGuid());
        Assert.EndsWith($"{Stock.Documents}/{reversing.Id()}", response.Headers.Location?.ToString());
        // The reversing document is an ordinary posted document for every read.
        McpAssert.JsonEqual(reversing, await Stock.GetAsync(s.Http, reversing.Id()), "GET differs from the reverse response");
        using var byNumber = await Stock.ByNumberAsync(s.Http, "SR-000002");
        McpAssert.JsonEqual(reversing, await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK));
    }

    [Fact]
    public async Task AC51_The_original_becomes_reversed_and_is_otherwise_untouched()
    {
        var s = await Stock.SetupAsync(app);
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        var reversing = await Stock.ReverseAsync(s.Http, original.Id(), Stock.NextDay, "wrong article");

        var after = await AssertOnlyReversedAsync(s.Http, original, reversing);
        Assert.Equal(reversing.Id(), after.GetProperty("reversedBy").Id());
        Assert.Equal("SR-000002", after.GetProperty("reversedBy").Str("number"));
        Assert.Equal("SR-000001", after.Number());
        Assert.Equal(original.Str("updatedAt"), after.Str("updatedAt"));
        Assert.Equal(original.Str("postedAt"), after.Str("postedAt"));
        Assert.Equal(Stock.Date, after.Str("documentDate"));
        JsonBody.AssertNull(after, "reversalOf");
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC52_The_ledger_shows_the_original_entry_and_its_cancelling_entry()
    {
        var s = await Stock.SetupAsync(app);
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var entryBefore = Assert.Single(await Stock.EntriesAsync(s.Http, original.Id()));
        var k2 = await Keys.CreateAsync(app, s.Http, "corrector", "human");

        var reversing = await Stock.ReverseAsync(k2.Client, original.Id(), Stock.NextDay, "wrong article");

        var entries = (await Stock.LedgerAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}")).Items();
        Assert.Equal(2, entries.Length);
        var (first, second) = (entries[0], entries[1]);
        Assert.Equal(100m, first.Quantity());
        Assert.Equal("SR-000001", first.GetProperty("document").Str("number"));
        Assert.False(first.GetProperty("document").Bool("isReversal"));
        Assert.Equal("2026-10-09", first.Str("documentDate"));
        // S2: the original's entry is the same entry, not rewritten.
        McpAssert.JsonEqual(entryBefore, first, "The reversal changed the original's ledger entry");
        Assert.Equal(-100m, second.Quantity());
        var document = second.GetProperty("document");
        Assert.Equal("SR-000002", document.Str("number"));
        Assert.Equal("receipt", document.Str("type"));
        Assert.Equal(reversing.Id(), document.Id());
        Assert.True(document.Bool("isReversal"));
        Assert.Equal("2026-10-10", second.Str("documentDate"));
        Assert.Equal(1, second.GetProperty("lineNo").GetInt32());
        Assert.Equal(k2.Id, second.GetProperty("postedBy").GetGuid());
        Assert.Equal(reversing.Str("postedAt"), second.Str("postedAt"));
        Assert.Equal(0m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC53_Reversing_an_issue_puts_the_stock_back()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 40);
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var reversing = await Stock.ReverseAsync(s.Http, issue.Id());

        Assert.Equal("issue", reversing.Str("type"));
        Assert.Equal("SI-000002", reversing.Number());
        Stock.AssertLink(reversing, "reversalOf", issue);
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, reversing.Id()));
        Assert.Equal(40m, entry.Quantity());
        Assert.Equal(s.W1, entry.GetProperty("warehouse").Id());
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await AssertOnlyReversedAsync(s.Http, issue, reversing);
        await AssertCancelsAsync(s.Http, issue, reversing);
    }

    [Fact]
    public async Task AC54_A_reversal_is_attributed_to_the_key_that_reversed()
    {
        var s = await Stock.SetupAsync(app);
        var k1 = await Keys.CreateAsync(app, s.Http, "poster", "human");
        var k2 = await Keys.CreateAsync(app, s.Http, "corrector", "agent");
        var draft = await Stock.CreateAsync(k1.Client, "receipt", s.W1, (s.A, 7));
        var original = await Stock.PostDocumentAsync(k1.Client, draft.Id());

        var reversing = await Stock.ReverseAsync(k2.Client, original.Id());

        Assert.Equal(k2.Id, reversing.GetProperty("createdBy").GetGuid());
        Assert.Equal(k2.Id, reversing.GetProperty("postedBy").GetGuid());
        var after = await AssertOnlyReversedAsync(s.Http, original, reversing);
        Assert.Equal(k1.Id, after.GetProperty("postedBy").GetGuid());
        Assert.Equal(k1.Id, after.GetProperty("createdBy").GetGuid());
    }

    [Fact]
    public async Task AC55_Reference_is_copied_and_an_omitted_note_is_null()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http,
            Stock.Draft("receipt", s.W1, (s.A, 7)).With("reference", "DN-4711").With("note", "the original's note"));
        var original = await Stock.PostDocumentAsync(s.Http, draft.Id());
        var second = await Stock.ReceiveAsync(s.Http, s.W1, s.B, 1);

        var reversing = await Stock.ReverseAsync(s.Http, original.Id());
        using var explicitNull = await Stock.SendReverseAsync(s.Http, second.Id(), Stock.ReverseBody().With("note", null));
        var reversingSecond = await HttpAssert.JsonAsync(explicitNull, HttpStatusCode.Created);

        Assert.Equal("DN-4711", reversing.Str("reference"));
        JsonBody.AssertNull(reversing, "note");
        Assert.Equal(Stock.Date, reversing.Str("documentDate"));
        JsonBody.AssertNull(reversingSecond, "note", "reference");
        // The original keeps its own note.
        Assert.Equal("the original's note", (await Stock.GetAsync(s.Http, original.Id())).Str("note"));
    }

    [Fact]
    public async Task AC55_R13_The_reversing_document_has_the_originals_lines_in_order_and_cancels_each()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10), (s.B, 5), (s.A, 2.5m));
        var original = await Stock.PostDocumentAsync(s.Http, draft.Id());

        var reversing = await Stock.ReverseAsync(s.Http, original.Id());

        McpAssert.JsonEqual(original.GetProperty("lines"), reversing.GetProperty("lines"), "The reversing document's lines differ from the original's");
        var entries = await Stock.EntriesAsync(s.Http, reversing.Id());
        Assert.Equal(new[] { (1, s.A, s.W1, -10m), (2, s.B, s.W1, -5m), (3, s.A, s.W1, -2.5m) }, entries.Select(Movement).ToArray());
        await AssertCancelsAsync(s.Http, original, reversing);
        await AssertOnlyReversedAsync(s.Http, original, reversing);
        Assert.Empty(await Stock.StockMapAsync(s.Http));
    }

    [Fact]
    public async Task AC50_R13_A_reversal_takes_the_next_number_of_the_series_which_stays_gapless()
    {
        var s = await Stock.SetupAsync(app);
        var first = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);
        var second = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 2);
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 1);

        var reversingFirst = await Stock.ReverseAsync(s.Http, first.Id());
        var third = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 3);
        var reversingIssue = await Stock.ReverseAsync(s.Http, issue.Id());
        var nextIssue = await Stock.IssueAsync(s.Http, s.W1, s.A, 1);

        Assert.Equal(new[] { "SR-000001", "SR-000002", "SR-000003", "SR-000004" },
            new[] { first.Number(), second.Number(), reversingFirst.Number(), third.Number() });
        Assert.Equal(new[] { "SI-000001", "SI-000002", "SI-000003" },
            new[] { issue.Number(), reversingIssue.Number(), nextIssue.Number() });
    }

    // ---- transfer ----

    [Fact]
    public async Task AC56_Reversing_a_transfer_moves_the_stock_back()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 30);

        var reversing = await Stock.ReverseAsync(s.Http, transfer.Id(), Stock.NextDay);

        Assert.Equal("transfer", reversing.Str("type"));
        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal("ST-000002", reversing.Number());
        // Same source and destination as the original; only the signs of the entries are opposite.
        Assert.Equal(s.W1, reversing.GetProperty("warehouse").Id());
        Assert.Equal(s.W2, reversing.GetProperty("toWarehouse").Id());
        Stock.AssertLink(reversing, "reversalOf", transfer);
        var entries = await Stock.EntriesAsync(s.Http, reversing.Id());
        Assert.Equal(new[] { (1, s.A, s.W1, 30m), (1, s.A, s.W2, -30m) }.Order().ToArray(), entries.Select(Movement).Order().ToArray());
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http, $"?warehouseId={s.W2}")).Total());
        await AssertCancelsAsync(s.Http, transfer, reversing);
        await AssertOnlyReversedAsync(s.Http, transfer, reversing);
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    // ---- rules ----

    [Fact]
    public async Task AC60_A_receipt_whose_goods_were_issued_cannot_be_reversed_until_the_issue_is()
    {
        var s = await Stock.SetupAsync(app);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 40);
        var documentsBefore = await DocumentCountAsync(s.Http);
        var entriesBefore = await LedgerCountAsync(s.Http);

        using var refused = await Stock.SendReverseAsync(s.Http, receipt.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        var after = await Stock.GetAsync(s.Http, receipt.Id());
        Assert.Equal("posted", after.Str("status"));
        JsonBody.AssertNull(after, "reversedBy");
        McpAssert.JsonEqual(receipt, after, "A refused reversal changed the original");
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(documentsBefore, await DocumentCountAsync(s.Http));
        Assert.Equal(entriesBefore, await LedgerCountAsync(s.Http));
        Assert.Equal(1, (await Stock.DocumentsAsync(s.Http, "?type=receipt")).Total());
        // No number was consumed: the next posted receipt (elsewhere, so that Stock(A, W1) stays 60) is SR-000002.
        var next = await Stock.ReceiveAsync(s.Http, s.W2, s.B, 1);
        Assert.Equal("SR-000002", next.Number());

        var reversingIssue = await Stock.ReverseAsync(s.Http, issue.Id());
        var reversingReceipt = await Stock.ReverseAsync(s.Http, receipt.Id());

        Assert.Equal("SI-000002", reversingIssue.Number());
        Assert.Equal("SR-000003", reversingReceipt.Number());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await AssertCancelsAsync(s.Http, receipt, reversingReceipt);
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC60_R16_Every_line_of_a_short_article_is_reported_and_nothing_is_reversed_in_part()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10), (s.B, 5), (s.A, 5));
        var receipt = await Stock.PostDocumentAsync(s.Http, draft.Id());
        await Stock.IssueAsync(s.Http, s.W1, s.A, 0.000001m);
        var stockBefore = await Stock.StockMapAsync(s.Http);

        using var refused = await Stock.SendReverseAsync(s.Http, receipt.Id());

        var problem = await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity", "lines[2].quantity");
        Assert.Equal(new[] { "lines[0].quantity", "lines[2].quantity" }, McpAssert.ErrorKeys(problem));
        // Whole or not at all: B, which is fully on hand, was not taken out either.
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        await Stock.AssertUnchangedAsync(s.Http, receipt);
    }

    [Fact]
    public async Task AC61_A_transfer_whose_goods_left_the_destination_cannot_be_reversed()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 30);
        var issue = await Stock.IssueAsync(s.Http, s.W2, s.A, 10);
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var documentsBefore = await DocumentCountAsync(s.Http);

        using var refused = await Stock.SendReverseAsync(s.Http, transfer.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(70m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(20m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Equal(documentsBefore, await DocumentCountAsync(s.Http));
        await Stock.AssertUnchangedAsync(s.Http, transfer);

        // Reverse the later document first (E6, E7), then the transfer goes back.
        await Stock.ReverseAsync(s.Http, issue.Id());
        var reversing = await Stock.ReverseAsync(s.Http, transfer.Id());

        Assert.Equal("ST-000002", reversing.Number());
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
    }

    [Fact]
    public async Task AC62_A_draft_a_reversed_document_and_a_reversing_document_cannot_be_reversed()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var reversing = await Stock.ReverseAsync(s.Http, original.Id());
        // Stock is there again, so only the state can refuse what follows.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 50);
        var reversed = await Stock.GetAsync(s.Http, original.Id());
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var documentsBefore = await DocumentCountAsync(s.Http);
        var entriesBefore = await LedgerCountAsync(s.Http);

        using var ofDraft = await Stock.SendReverseAsync(s.Http, draft.Id());
        using var second = await Stock.SendReverseAsync(s.Http, original.Id(), Stock.NextDay);
        using var ofReversing = await Stock.SendReverseAsync(s.Http, reversing.Id());

        await Stock.ConflictAsync(ofDraft, "INVALID_STATE");
        await Stock.ConflictAsync(second, "INVALID_STATE");
        await Stock.ConflictAsync(ofReversing, "INVALID_STATE");
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await Stock.AssertUnchangedAsync(s.Http, reversed);
        await Stock.AssertUnchangedAsync(s.Http, reversing);
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Equal(documentsBefore, await DocumentCountAsync(s.Http));
        Assert.Equal(entriesBefore, await LedgerCountAsync(s.Http));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    [InlineData("transfer")]
    public async Task AC63_Reversed_and_reversing_documents_are_immutable(string type)
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = type == "transfer"
            ? await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 10))
            : await Stock.CreateAsync(s.Http, type, s.W1, (s.A, 10));
        var original = await Stock.PostDocumentAsync(s.Http, draft.Id());
        var reversing = await Stock.ReverseAsync(s.Http, original.Id());
        var reversed = await Stock.GetAsync(s.Http, original.Id());
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var entriesBefore = await LedgerCountAsync(s.Http);
        JsonObject Body() => type == "transfer"
            ? Stock.TransferReplacement(s.W1, s.W2, (s.A, 1))
            : Stock.Replacement(s.W1, (s.A, 1));

        foreach (var document in new[] { reversed, reversing })
        {
            using var put = await Stock.PutAsync(s.Http, document.Id(), Body());
            using var delete = await Stock.DeleteAsync(s.Http, document.Id());
            using var post = await Stock.SendPostAsync(s.Http, document.Id());

            await Stock.ConflictAsync(put, "INVALID_STATE");
            await Stock.ConflictAsync(delete, "INVALID_STATE");
            await Stock.ConflictAsync(post, "INVALID_STATE");
            await Stock.AssertUnchangedAsync(s.Http, document);
        }
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Equal(entriesBefore, await LedgerCountAsync(s.Http));
    }

    [Fact]
    public async Task AC64_The_reversal_date_must_not_be_earlier_than_the_originals()
    {
        var s = await Stock.SetupAsync(app);
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        Assert.Equal("2026-10-09", original.Str("documentDate"));

        using var earlier = await Stock.SendReverseAsync(s.Http, original.Id(), "2026-10-08");
        await HttpAssert.ValidationAsync(earlier, "documentDate");
        await Stock.AssertUnchangedAsync(s.Http, original);
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var reversing = await Stock.ReverseAsync(s.Http, original.Id(), "2026-10-09");
        Assert.Equal("2026-10-09", reversing.Str("documentDate"));
        Assert.Equal("SR-000002", reversing.Number());
    }

    public static TheoryData<string, string?> InvalidReverseBodies() => new()
    {
        { "no body", null },
        { "empty object", "{}" },
        { "impossible date", """{ "documentDate": "2026-02-30" }""" },
        { "null date", """{ "documentDate": null }""" },
        { "date with a time", """{ "documentDate": "2026-10-09T10:00:00Z" }""" },
        { "local format", """{ "documentDate": "09.10.2026" }""" },
    };

    [Theory]
    [MemberData(nameof(InvalidReverseBodies))]
    public async Task AC64_Reverse_without_a_real_date_is_rejected(string what, string? body)
    {
        var s = await Stock.SetupAsync(app);
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        using var response = await s.Http.PostAsync(ReversePath(original.Id()), body is null ? null : HttpAssert.Raw(body));

        await HttpAssert.ValidationAsync(response, "documentDate");
        McpAssert.JsonEqual(original, await Stock.GetAsync(s.Http, original.Id()), $"Reverse with {what} changed the original");
        Assert.Equal(1, await DocumentCountAsync(s.Http));
    }

    [Fact]
    public async Task AC64_R12_Order_of_checks_is_form_then_404_then_state_then_date_then_stock()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);

        // Form before 404: an unknown document with an invalid body is a 400.
        using var formFirst = await Stock.SendReverseAsync(s.Http, Guid.NewGuid(), new JsonObject());
        using var noteTooLong = await Stock.SendReverseAsync(s.Http, receipt.Id(), Stock.Date, new string('n', 2001));
        // State before date: a draft with a date in the past is INVALID_STATE.
        using var stateFirst = await Stock.SendReverseAsync(s.Http, draft.Id(), "2000-01-01");
        // Date before stock: the goods are gone and the date is too early -> the date is reported.
        using var dateFirst = await Stock.SendReverseAsync(s.Http, receipt.Id(), "2026-10-08");
        using var stock = await Stock.SendReverseAsync(s.Http, receipt.Id());

        await HttpAssert.ValidationAsync(formFirst, "documentDate");
        await HttpAssert.ValidationAsync(noteTooLong, "note");
        await Stock.ConflictAsync(stateFirst, "INVALID_STATE");
        await HttpAssert.ValidationAsync(dateFirst, "documentDate");
        await Stock.ConflictAsync(stock, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertUnchangedAsync(s.Http, receipt);
    }

    [Fact]
    public async Task AC65_Inactive_masters_do_not_block_a_reversal()
    {
        var s = await Stock.SetupAsync(app);
        var original = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.ReplaceArticleAsync(s.Http, s.A,
            Stock.ArticleBody(s.ArticleA).With("code", "A-OLD").With("name", "Retired article").With("isActive", false));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);

        var reversing = await Stock.ReverseAsync(s.Http, original.Id());

        Assert.Equal("posted", reversing.Str("status"));
        // E11: summaries show the current code and name.
        var article = Assert.Single(reversing.DocumentLines()).GetProperty("article");
        Assert.Equal(("A-OLD", "Retired article"), (article.Str("code"), article.Str("name")));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // A posting, unlike a reversal, still needs active masters (005/R13).
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W2, (s.B, 1));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);
        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());
        await Stock.ConflictAsync(refused, "REFERENCE_INACTIVE", "warehouseId");
    }

    [Fact]
    public async Task AC65_R17_A_transfer_is_reversed_with_both_warehouses_inactive()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 4);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);

        var reversing = await Stock.ReverseAsync(s.Http, transfer.Id());

        Assert.Equal("ST-000002", reversing.Number());
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
    }

    [Fact]
    public async Task AC66_After_every_posting_is_reversed_stock_is_empty_and_each_pair_of_documents_sums_to_zero()
    {
        var s = await Stock.SetupAsync(app);
        var receipt = await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 100), (s.B, 50))).Id());
        var transfer = await Stock.PostDocumentAsync(s.Http,
            (await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 40), (s.B, 0.5m), (s.A, 0.000001m))).Id());
        var issueW2 = await Stock.IssueAsync(s.Http, s.W2, s.A, 10);
        var receiptW2 = await Stock.ReceiveAsync(s.Http, s.W2, s.A, 5);
        var issueW1 = await Stock.IssueAsync(s.Http, s.W1, s.B, 20);
        await Stock.AssertStockEqualsLedgerAsync(s.Http);

        // An order stock allows: the issues first, then what brought the goods in.
        var pairs = new List<(JsonElement Original, JsonElement Reversing)>();
        foreach (var original in new[] { issueW2, issueW1, receiptW2, transfer, receipt })
        {
            var reversing = await Stock.ReverseAsync(s.Http, original.Id(), Stock.NextDay);
            pairs.Add((original, reversing));
            // AC-67 on the way: stock equals the ledger after every reversal, and is never negative.
            await Stock.AssertStockEqualsLedgerAsync(s.Http);
        }

        var onHand = await Stock.OnHandAsync(s.Http);
        Assert.Empty(onHand.Items());
        Assert.Equal(0, onHand.Total());
        foreach (var (original, reversing) in pairs)
        {
            await AssertCancelsAsync(s.Http, original, reversing);
            await AssertOnlyReversedAsync(s.Http, original, reversing);
        }
        Assert.Empty(await Stock.LedgerMapAsync(s.Http));
        // History shows both: ten documents, five reversed and five reversing, and every entry is still there.
        Assert.Equal(5, (await Stock.DocumentsAsync(s.Http, "?status=reversed")).Total());
        Assert.Equal(5, (await Stock.DocumentsAsync(s.Http, "?status=posted")).Total());
        Assert.Equal(2 * (2 + 6 + 1 + 1 + 1), await LedgerCountAsync(s.Http));
    }

    [Fact]
    public async Task AC67_Stock_on_hand_equals_the_ledger_after_every_step_of_a_mix_with_transfers_and_reversals()
    {
        var s = await Stock.SetupAsync(app);
        var expected = new Dictionary<(Guid, Guid), decimal>();
        async Task CheckAsync(params ((Guid, Guid) Pair, decimal Quantity)[] pairs)
        {
            foreach (var (pair, quantity) in pairs)
                expected[pair] = quantity;
            var stock = await Stock.AssertStockEqualsLedgerAsync(s.Http);
            var nonZero = expected.Where(p => p.Value != 0m).ToDictionary(p => p.Key, p => p.Value);
            Assert.True(nonZero.Count == stock.Count && nonZero.All(p => stock.TryGetValue(p.Key, out var q) && q == p.Value),
                $"Stock on hand is [{string.Join(", ", stock)}], expected [{string.Join(", ", nonZero)}]");
        }

        var receipt = await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10.5m), (s.B, 4))).Id());
        await CheckAsync(((s.A, s.W1), 10.5m), ((s.B, s.W1), 4m));
        var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 0.3m);
        await CheckAsync(((s.A, s.W1), 10.2m), ((s.A, s.W2), 0.3m));
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 0.2m);
        await CheckAsync(((s.A, s.W1), 10m));
        await Stock.ReverseAsync(s.Http, transfer.Id());
        await CheckAsync(((s.A, s.W1), 10.3m), ((s.A, s.W2), 0m));
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.B, 4);
        await CheckAsync(((s.B, s.W1), 0m), ((s.B, s.W2), 4m));
        // Refused operations change nothing: the receipt cannot be reversed (B has left W1), a draft is only a draft.
        using (var refused = await Stock.SendReverseAsync(s.Http, receipt.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity", "lines[1].quantity");
        await Stock.CreateTransferAsync(s.Http, s.W2, s.W1, (s.B, 4));
        await CheckAsync();
        await Stock.ReverseAsync(s.Http, issue.Id());
        await CheckAsync(((s.A, s.W1), 10.5m));
        await Stock.TransferAsync(s.Http, s.W2, s.W1, s.B, 1.5m);
        await CheckAsync(((s.B, s.W1), 1.5m), ((s.B, s.W2), 2.5m));
        Assert.Equal(10.5m, await Stock.TotalAsync(s.Http, s.A));
        Assert.Equal(4m, await Stock.TotalAsync(s.Http, s.B));
    }

    [Fact]
    public async Task AC68_Of_parallel_reversals_of_one_document_exactly_one_succeeds()
    {
        var s = await Stock.SetupAsync(app);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => Stock.SendReverseAsync(s.Http, receipt.Id()))));

        var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal(new[] { 201, 409, 409, 409, 409 }, statuses);
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await Stock.ConflictAsync(refused, "INVALID_STATE");
        var reversing = await HttpAssert.JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
        Assert.Equal("SR-000002", reversing.Number());
        var receipts = await Stock.DocumentsAsync(s.Http, "?type=receipt");
        Assert.Equal(2, receipts.Total());
        Assert.Equal(2, await DocumentCountAsync(s.Http));
        Assert.Equal(2, await LedgerCountAsync(s.Http));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        await AssertOnlyReversedAsync(s.Http, receipt, reversing);
        // No number was burnt by the four refused requests.
        Assert.Equal("SR-000003", (await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1)).Number());
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task AC69_A_reversal_racing_with_an_issue_of_the_same_stock_never_overdraws_it()
    {
        // Three rounds, each with its own tenant, so that either request has a chance to win.
        for (var round = 0; round < 3; round++)
        {
            var s = await Stock.SetupAsync(app);
            var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
            var issue = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 10));

            var reverse = Task.Run(() => Stock.SendReverseAsync(s.Http, receipt.Id()));
            var post = Task.Run(() => Stock.SendPostAsync(s.Http, issue.Id()));
            using var reversed = await reverse;
            using var posted = await post;

            var reversalWon = reversed.StatusCode == HttpStatusCode.Created;
            var issueWon = posted.StatusCode == HttpStatusCode.OK;
            Assert.True(reversalWon ^ issueWon, $"Exactly one must succeed: reverse {(int)reversed.StatusCode}, post {(int)posted.StatusCode}");
            await Stock.ConflictAsync(reversalWon ? posted : reversed, "INSUFFICIENT_STOCK", "lines[0].quantity");
            Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
            Assert.Equal(0m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
            Assert.Equal(2, await LedgerCountAsync(s.Http));
            Assert.Equal(reversalWon ? "reversed" : "posted", (await Stock.GetAsync(s.Http, receipt.Id())).Str("status"));
            Assert.Equal(issueWon ? "posted" : "draft", (await Stock.GetAsync(s.Http, issue.Id())).Str("status"));
        }
    }

    [Fact]
    public async Task AC69_R19_A_transfer_reversal_racing_with_an_issue_from_the_destination_never_overdraws_it()
    {
        for (var round = 0; round < 3; round++)
        {
            var s = await Stock.SetupAsync(app);
            await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
            var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 10);
            var issue = await Stock.CreateAsync(s.Http, "issue", s.W2, (s.A, 10));

            var reverse = Task.Run(() => Stock.SendReverseAsync(s.Http, transfer.Id()));
            var post = Task.Run(() => Stock.SendPostAsync(s.Http, issue.Id()));
            using var reversed = await reverse;
            using var posted = await post;

            var reversalWon = reversed.StatusCode == HttpStatusCode.Created;
            var issueWon = posted.StatusCode == HttpStatusCode.OK;
            Assert.True(reversalWon ^ issueWon, $"Exactly one must succeed: reverse {(int)reversed.StatusCode}, post {(int)posted.StatusCode}");
            await Stock.ConflictAsync(reversalWon ? posted : reversed, "INSUFFICIENT_STOCK", "lines[0].quantity");
            Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
            // Either the goods went back to W1 or they were issued; they are never in both places or in neither.
            Assert.Equal(reversalWon ? 10m : 0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
            await Stock.AssertStockEqualsLedgerAsync(s.Http);
        }
    }

    [Fact]
    public async Task AC70_Lists_tell_reversed_originals_from_posted_documents_and_masters_stay_used()
    {
        var s = await Stock.SetupAsync(app);
        var reversedReceipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var keptReceipt = await Stock.ReceiveAsync(s.Http, s.W1, s.B, 10);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.B, 1));
        var reversing = await Stock.ReverseAsync(s.Http, reversedReceipt.Id());

        async Task<Guid[]> IdsAsync(string query) =>
            (await Stock.DocumentsAsync(s.Http, query)).Items().Select(d => d.Id()).Order().ToArray();
        static Guid[] Set(params JsonElement[] documents) => documents.Select(d => d.Id()).Order().ToArray();

        Assert.Equal(Set(reversedReceipt), await IdsAsync("?status=reversed"));
        Assert.Equal(Set(keptReceipt, reversing), await IdsAsync("?status=posted"));
        Assert.Equal(Set(draft), await IdsAsync("?status=draft"));
        Assert.Equal(Set(reversedReceipt), await IdsAsync("?type=receipt&status=reversed"));
        Assert.Empty(await IdsAsync("?type=issue&status=reversed"));
        var summaries = (await Stock.DocumentsAsync(s.Http)).Items().ToDictionary(d => d.Id());
        Assert.Equal("reversed", summaries[reversedReceipt.Id()].Str("status"));
        Stock.AssertLink(summaries[reversedReceipt.Id()], "reversedBy", reversing);
        // R21: a reversing document's status is posted; it is recognised by reversalOf.
        Assert.Equal("posted", summaries[reversing.Id()].Str("status"));
        Stock.AssertLink(summaries[reversing.Id()], "reversalOf", reversedReceipt);
        JsonBody.AssertNull(summaries[keptReceipt.Id()], "reversalOf", "reversedBy");

        // R20: A is only on the reversed pair, with no stock left, and is still used.
        using var article = await s.Http.DeleteAsync($"{Art.Path}/{s.A}");
        using var frozen = await Stock.PutArticleAsync(s.Http, s.A, Stock.ArticleBody(s.ArticleA).With("type", "service"));
        await Stock.ConflictAsync(article, "IN_USE");
        await Stock.ConflictAsync(frozen, "IN_USE", "type");
    }

    [Fact]
    public async Task AC70_R20_Warehouses_of_a_reversed_transfer_stay_used()
    {
        var s = await Stock.SetupAsync(app);
        var w3 = (await W.CreateAsync(s.Http, "W3", "Warehouse three")).Id();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        var transfer = await Stock.TransferAsync(s.Http, s.W1, w3, s.A, 5);
        await Stock.ReverseAsync(s.Http, transfer.Id());

        using var destination = await W.DeleteAsync(s.Http, w3);
        using var unused = await W.DeleteAsync(s.Http, s.W2);

        await Stock.ConflictAsync(destination, "IN_USE");
        Assert.Equal(HttpStatusCode.NoContent, unused.StatusCode);
    }

    [Fact]
    public async Task AC71_Reverse_of_an_unknown_document_is_not_found()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        using var random = await Stock.SendReverseAsync(s.Http, Guid.NewGuid());
        using var malformed = await s.Http.PostAsync($"{Stock.Documents}/abc/reverse", HttpAssert.Raw("""{ "documentDate": "2026-10-09" }"""));

        await HttpAssert.NotFoundAsync(random);
        await HttpAssert.NotFoundAsync(malformed);
        Assert.Equal(1, await DocumentCountAsync(s.Http));
    }
}
