using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 006, AC-20 to AC-41: the transfer — a document with a source and a destination warehouse whose posting
/// moves stock between them in one step. The total of an article over all warehouses never changes (R8), the
/// source never goes below zero (R7) and nothing is left half done.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockTransferTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    private static async Task<int> DocumentCountAsync(HttpClient client) => (await Stock.DocumentsAsync(client)).Total();

    private static async Task<int> LedgerCountAsync(HttpClient client) => (await Stock.LedgerAsync(client)).Total();

    private static Guid Warehouse(JsonElement entry) => entry.GetProperty("warehouse").Id();

    private static Guid Article(JsonElement entry) => entry.GetProperty("article").Id();

    // ---- draft ----

    [Fact]
    public async Task AC20_A_transfer_is_created_as_a_draft_with_source_and_destination_and_moves_nothing()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var entriesBefore = await LedgerCountAsync(s.Http);

        using var response = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.A, 30)));
        var draft = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.Equal("transfer", draft.Str("type"));
        Assert.Equal("draft", draft.Str("status"));
        JsonBody.AssertNull(draft, "number", "postedAt", "postedBy", "reversalOf", "reversedBy");
        Assert.Equal(s.W1, draft.GetProperty("warehouse").Id());
        var destination = draft.GetProperty("toWarehouse");
        Assert.Equal(s.W2, destination.Id());
        Assert.Equal("W2", destination.Str("code"));
        Assert.Equal("Warehouse two", destination.Str("name"));
        var line = Assert.Single(draft.DocumentLines());
        Assert.Equal(s.A, line.GetProperty("article").Id());
        Assert.Equal(30m, line.Quantity());
        Assert.EndsWith($"{Stock.Documents}/{draft.Id()}", response.Headers.Location?.ToString());
        McpAssert.JsonEqual(draft, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the create response");
        // A draft has no effect on stock or on the ledger (005/R9).
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Equal(entriesBefore, await LedgerCountAsync(s.Http));
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft.Id()));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    public async Task AC21_A_receipt_and_an_issue_have_null_destination_and_reversal_links(string type)
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        var draft = await Stock.CreateAsync(s.Http, type, s.W1, (s.A, 1));
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        var summary = (await Stock.DocumentsAsync(s.Http)).Items().Single(d => d.Id() == draft.Id());

        JsonBody.AssertNull(draft, "toWarehouse", "reversalOf", "reversedBy");
        JsonBody.AssertNull(posted, "toWarehouse", "reversalOf", "reversedBy");
        JsonBody.AssertNull(summary, "toWarehouse", "reversalOf", "reversedBy");
    }

    public static TheoryData<string, string?> InvalidDestinations() => new()
    {
        { "missing", null },
        { "null", "null" },
        { "not a uuid", "\"abc\"" },
        { "a number", "7" },
    };

    [Theory]
    [MemberData(nameof(InvalidDestinations))]
    public async Task AC22_A_transfer_without_a_valid_destination_is_rejected(string what, string? json)
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Transfer(s.W1, s.W2, (s.A, 1)).Without("toWarehouseId");
        if (json is not null)
            body["toWarehouseId"] = JsonNode.Parse(json);

        using var response = await Stock.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response, "toWarehouseId");
        Assert.True(await DocumentCountAsync(s.Http) == 0, $"toWarehouseId {what}: a document was created");
    }

    [Fact]
    public async Task AC22_Source_and_destination_must_differ()
    {
        var s = await Stock.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W1, (s.A, 1)));

        var problem = await HttpAssert.ValidationAsync(response, "toWarehouseId");
        Assert.DoesNotContain("warehouseId", McpAssert.ErrorKeys(problem));
        Assert.Equal(0, await DocumentCountAsync(s.Http));
    }

    [Fact]
    public async Task AC22_Unknown_or_inactive_destination_is_a_reference_error_on_toWarehouseId()
    {
        var s = await Stock.SetupAsync(app);
        var closed = await W.CreateAsync(s.Http, "W3", "Closed warehouse");
        await Stock.SetWarehouseActiveAsync(s.Http, closed.Id(), false);

        using var unknown = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, Guid.NewGuid(), (s.A, 1)));
        using var inactive = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, closed.Id(), (s.A, 1)));

        var notFound = await Stock.ConflictAsync(unknown, "REFERENCE_NOT_FOUND", "toWarehouseId");
        Assert.DoesNotContain("warehouseId", McpAssert.ErrorKeys(notFound));
        var notActive = await Stock.ConflictAsync(inactive, "REFERENCE_INACTIVE", "toWarehouseId");
        Assert.DoesNotContain("warehouseId", McpAssert.ErrorKeys(notActive));
        Assert.Equal(0, await DocumentCountAsync(s.Http));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    public async Task AC22_R3_A_receipt_or_issue_takes_no_destination_but_accepts_null(string type)
    {
        var s = await Stock.SetupAsync(app);

        using var withDestination = await Stock.PostAsync(s.Http, Stock.Draft(type, s.W1, (s.A, 1)).With("toWarehouseId", s.W2.ToString()));
        using var withNull = await Stock.PostAsync(s.Http, Stock.Draft(type, s.W1, (s.A, 1)).With("toWarehouseId", null));

        await HttpAssert.ValidationAsync(withDestination, "toWarehouseId");
        var created = await HttpAssert.JsonAsync(withNull, HttpStatusCode.Created);
        JsonBody.AssertNull(created, "toWarehouse");
        Assert.Equal(1, await DocumentCountAsync(s.Http));
    }

    [Fact]
    public async Task AC22_R4_Validation_precedes_references_and_the_destination_precedes_the_lines()
    {
        var s = await Stock.SetupAsync(app);

        // An unknown destination with an invalid quantity is a validation error first (005/R8).
        using var invalid = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, Guid.NewGuid(), (s.A, 0)));
        // An unknown destination is reported before the service article on the line.
        using var destinationFirst = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, Guid.NewGuid(), (s.S, 1)));
        // The source is checked before the destination.
        using var sourceFirst = await Stock.PostAsync(s.Http, Stock.Transfer(Guid.NewGuid(), Guid.NewGuid(), (s.A, 1)));
        using var service = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.S, 1)));

        await HttpAssert.ValidationAsync(invalid, "lines[0].quantity");
        var destination = await Stock.ConflictAsync(destinationFirst, "REFERENCE_NOT_FOUND", "toWarehouseId");
        Assert.DoesNotContain("lines[0].articleId", McpAssert.ErrorKeys(destination));
        await Stock.ConflictAsync(sourceFirst, "REFERENCE_NOT_FOUND", "warehouseId");
        await Stock.ConflictAsync(service, "ARTICLE_NOT_STOCKED", "lines[0].articleId");
        Assert.Equal(0, await DocumentCountAsync(s.Http));
    }

    [Fact]
    public async Task AC23_A_draft_transfer_can_swap_its_warehouses_and_change_its_lines()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 30));

        var replaced = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.TransferReplacement(s.W2, s.W1, (s.B, 4), (s.A, 1.5m)));

        Assert.Equal("transfer", replaced.Str("type"));
        Assert.Equal("draft", replaced.Str("status"));
        Assert.Equal(s.W2, replaced.GetProperty("warehouse").Id());
        Assert.Equal(s.W1, replaced.GetProperty("toWarehouse").Id());
        Assert.Equal("W1", replaced.GetProperty("toWarehouse").Str("code"));
        Assert.Equal(new[] { (s.B, 4m), (s.A, 1.5m) },
            replaced.DocumentLines().Select(l => (l.GetProperty("article").Id(), l.Quantity())).ToArray());
        McpAssert.JsonEqual(replaced, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the replace response");
    }

    [Fact]
    public async Task AC23_Replacing_a_draft_transfer_needs_a_destination_different_from_the_source()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 30));

        using var missing = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 30)));
        using var isNull = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 30)).With("toWarehouseId", null));
        using var same = await Stock.PutAsync(s.Http, draft.Id(), Stock.TransferReplacement(s.W2, s.W2, (s.A, 30)));
        using var unknown = await Stock.PutAsync(s.Http, draft.Id(), Stock.TransferReplacement(s.W1, Guid.NewGuid(), (s.A, 30)));

        await HttpAssert.ValidationAsync(missing, "toWarehouseId");
        await HttpAssert.ValidationAsync(isNull, "toWarehouseId");
        await HttpAssert.ValidationAsync(same, "toWarehouseId");
        await Stock.ConflictAsync(unknown, "REFERENCE_NOT_FOUND", "toWarehouseId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
    }

    [Fact]
    public async Task AC23_A_draft_receipt_is_replaced_without_a_destination_and_cannot_take_one()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));

        var without = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 2)));
        var withNull = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 3)).With("toWarehouseId", null));
        using var withDestination = await Stock.PutAsync(s.Http, draft.Id(), Stock.TransferReplacement(s.W1, s.W2, (s.A, 4)));

        Assert.Equal(2m, Assert.Single(without.DocumentLines()).Quantity());
        Assert.Equal(3m, Assert.Single(withNull.DocumentLines()).Quantity());
        JsonBody.AssertNull(withNull, "toWarehouse");
        await HttpAssert.ValidationAsync(withDestination, "toWarehouseId");
        await Stock.AssertUnchangedAsync(s.Http, withNull);
    }

    // ---- posting ----

    [Fact]
    public async Task AC30_AC31_Posting_a_transfer_moves_the_stock_and_writes_an_outgoing_and_an_incoming_entry()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 30));

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal("ST-000001", posted.Number());
        Assert.Equal("transfer", posted.Str("type"));
        Assert.Equal(s.W2, posted.GetProperty("toWarehouse").Id());
        Assert.Equal(s.Tenant.ApiKeyId, posted.GetProperty("postedBy").GetGuid());
        JsonBody.AssertNull(posted, "reversalOf", "reversedBy");
        Assert.Equal(70m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.A, s.W2));

        // AC-31: exactly two entries, the outgoing one first.
        var entries = await Stock.EntriesAsync(s.Http, draft.Id());
        Assert.Equal(2, entries.Length);
        Assert.Equal(new[] { (s.W1, -30m), (s.W2, 30m) }, entries.Select(e => (Warehouse(e), e.Quantity())).ToArray());
        Assert.All(entries, e =>
        {
            Assert.Equal(1, e.GetProperty("lineNo").GetInt32());
            Assert.Equal(s.A, Article(e));
            Assert.Equal(Stock.Date, e.Str("documentDate"));
            Assert.Equal(posted.Str("postedAt"), e.Str("postedAt"));
            Assert.Equal(s.Tenant.ApiKeyId, e.GetProperty("postedBy").GetGuid());
            var document = e.GetProperty("document");
            Assert.Equal(draft.Id(), document.Id());
            Assert.Equal("transfer", document.Str("type"));
            Assert.Equal("ST-000001", document.Str("number"));
            Assert.False(document.Bool("isReversal"));
        });
        Assert.NotEqual(entries[0].Id(), entries[1].Id());
        using var byNumber = await Stock.ByNumberAsync(s.Http, "ST-000001");
        McpAssert.JsonEqual(posted, await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK));
    }

    [Fact]
    public async Task AC32_A_transfer_conserves_the_total_of_every_article()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 20), (s.B, 10))).Id());
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 3);
        var totalA = await Stock.TotalAsync(s.Http, s.A);
        var totalB = await Stock.TotalAsync(s.Http, s.B);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 10), (s.B, 5), (s.A, 2.5m));

        await Stock.PostDocumentAsync(s.Http, draft.Id());

        var entries = await Stock.EntriesAsync(s.Http, draft.Id());
        // R6: per line the outgoing entry (source) precedes the incoming entry (destination).
        Assert.Equal(
            new[]
            {
                (1, s.A, s.W1, -10m), (1, s.A, s.W2, 10m),
                (2, s.B, s.W1, -5m), (2, s.B, s.W2, 5m),
                (3, s.A, s.W1, -2.5m), (3, s.A, s.W2, 2.5m),
            },
            entries.Select(e => (e.GetProperty("lineNo").GetInt32(), Article(e), Warehouse(e), e.Quantity())).ToArray());
        Assert.Single(entries.Select(e => e.Str("postedAt")).Distinct());
        // R8: the entries of the document sum to zero per article.
        Assert.All(entries.GroupBy(Article), article => Assert.Equal(0m, article.Sum(e => e.Quantity())));
        Assert.Equal((23m, 10m), (totalA, totalB));
        Assert.Equal(totalA, await Stock.TotalAsync(s.Http, s.A));
        Assert.Equal(totalB, await Stock.TotalAsync(s.Http, s.B));
        Assert.Equal(7.5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(15.5m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.B, s.W2));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC33_A_transfer_of_more_than_the_source_holds_is_refused_and_changes_nothing()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 70);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 5);
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var entriesBefore = await LedgerCountAsync(s.Http);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 71));

        using var response = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(response, "INSUFFICIENT_STOCK", "lines[0].quantity");
        var after = await Stock.AssertDraftAsync(s.Http, draft.Id());
        McpAssert.JsonEqual(draft, after, "A refused posting changed the draft");
        Assert.Equal(70m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft.Id()));
        Assert.Equal(entriesBefore, await LedgerCountAsync(s.Http));
    }

    [Fact]
    public async Task AC33_Lines_of_one_article_are_summed_and_every_line_of_the_short_article_is_reported()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 70), (s.B, 1))).Id());
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 40), (s.B, 1), (s.A, 40));

        using var response = await Stock.SendPostAsync(s.Http, draft.Id());

        var problem = await Stock.ConflictAsync(response, "INSUFFICIENT_STOCK", "lines[0].quantity", "lines[2].quantity");
        Assert.Equal(new[] { "lines[0].quantity", "lines[2].quantity" }, McpAssert.ErrorKeys(problem));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        // Atomic (R8): the covered line of B did not move either.
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft.Id()));
    }

    [Fact]
    public async Task AC34_Stock_in_the_destination_does_not_cover_a_transfer()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 50);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1));

        using var response = await Stock.SendPostAsync(s.Http, draft.Id());

        await Stock.ConflictAsync(response, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
    }

    [Fact]
    public async Task AC35_A_transfer_of_exactly_the_source_stock_empties_the_source()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 12.345678m);

        var posted = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 12.345678m);

        Assert.Equal("posted", posted.Str("status"));
        var item = Assert.Single((await Stock.OnHandAsync(s.Http)).Items());
        Assert.Equal(s.W2, item.GetProperty("warehouse").Id());
        Assert.Equal(12.345678m, item.Quantity());
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http, $"?warehouseId={s.W1}")).Total());
        // One more unit of the smallest size is not there any more.
        var next = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 0.000001m));
        using var refused = await Stock.SendPostAsync(s.Http, next.Id());
        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
    }

    [Fact]
    public async Task AC36_Posting_a_transfer_needs_an_active_destination()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 4));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        var problem = await Stock.ConflictAsync(refused, "REFERENCE_INACTIVE", "toWarehouseId");
        Assert.DoesNotContain("warehouseId", McpAssert.ErrorKeys(problem));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Empty(await Stock.EntriesAsync(s.Http, draft.Id()));

        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, true);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("ST-000001", posted.Number());
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
    }

    [Fact]
    public async Task AC36_R5_An_inactive_source_is_reported_too_and_inactive_masters_before_insufficient_stock()
    {
        var s = await Stock.SetupAsync(app);
        // No stock at all: the inactive warehouses must be reported, not the missing stock (005/R13).
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 4));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);

        using var both = await Stock.SendPostAsync(s.Http, draft.Id());
        await Stock.ConflictAsync(both, "REFERENCE_INACTIVE", "warehouseId", "toWarehouseId");

        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, true);
        using var sourceOnly = await Stock.SendPostAsync(s.Http, draft.Id());
        var problem = await Stock.ConflictAsync(sourceOnly, "REFERENCE_INACTIVE", "warehouseId");
        Assert.DoesNotContain("toWarehouseId", McpAssert.ErrorKeys(problem));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
    }

    [Fact]
    public async Task AC37_Transfers_have_their_own_gapless_numbers_and_a_failed_posting_consumes_none()
    {
        var s = await Stock.SetupAsync(app);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var tooMuch = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 11));

        var first = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 2);
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 1);
        using var refused = await Stock.SendPostAsync(s.Http, tooMuch.Id());
        var secondReceipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);
        var second = await Stock.TransferAsync(s.Http, s.W2, s.W1, s.A, 1);
        using var refusedAgain = await Stock.SendPostAsync(s.Http, tooMuch.Id());
        var third = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 1);

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        await Stock.ConflictAsync(refusedAgain, "INSUFFICIENT_STOCK");
        Assert.Equal("SR-000001", receipt.Number());
        Assert.Equal("SR-000002", secondReceipt.Number());
        Assert.Equal("SI-000001", issue.Number());
        Assert.Equal(new[] { "ST-000001", "ST-000002", "ST-000003" }, new[] { first.Number(), second.Number(), third.Number() });
    }

    [Fact]
    public async Task AC38_Parallel_transfers_from_one_source_never_overdraw_it()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var drafts = new List<Guid>();
        for (var i = 0; i < 10; i++)
            drafts.Add((await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 3))).Id());

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal(Enumerable.Repeat(200, 3).Concat(Enumerable.Repeat(409, 7)).ToArray(), statuses);
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        var numbers = new List<string?>();
        foreach (var accepted in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
            numbers.Add((await HttpAssert.JsonAsync(accepted, HttpStatusCode.OK)).Number());
        Assert.Equal(new[] { "ST-000001", "ST-000002", "ST-000003" }, numbers.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(9m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(10m, await Stock.TotalAsync(s.Http, s.A));
        // Three transfers wrote six entries; nothing was left half done by the seven refused ones.
        var entries = (await Stock.LedgerAsync(s.Http)).Items().Where(e => e.GetProperty("document").Str("type") == "transfer").ToArray();
        Assert.Equal(6, entries.Length);
        Assert.Equal(0m, entries.Sum(e => e.Quantity()));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
        Assert.Equal(7, (await Stock.DocumentsAsync(s.Http, "?type=transfer&status=draft")).Total());
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task AC39_Parallel_transfers_in_opposite_directions_all_succeed()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 10);
        var drafts = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            drafts.Add((await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 2))).Id());
            drafts.Add((await Stock.CreateTransferAsync(s.Http, s.W2, s.W1, (s.A, 2))).Id());
        }

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        // Never a deadlock surfaced as 500 (E5): every request is 200.
        Assert.Equal(Enumerable.Repeat(200, 10).ToArray(), responses.Select(r => (int)r.StatusCode).ToArray());
        var numbers = new List<string?>();
        foreach (var response in responses)
            numbers.Add((await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Number());
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"ST-{n:D6}").ToArray(), numbers.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task AC39_E5_Parallel_transfers_of_two_articles_in_opposite_directions_and_line_order_conserve_stock()
    {
        // Each transfer moves A and B; half go W1 -> W2 listing A first, half W2 -> W1 listing B first.
        var s = await Stock.SetupAsync(app);
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 8), (s.B, 8))).Id());
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W2, (s.A, 8), (s.B, 8))).Id());
        var drafts = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            drafts.Add((await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 2), (s.B, 2))).Id());
            drafts.Add((await Stock.CreateTransferAsync(s.Http, s.W2, s.W1, (s.B, 2), (s.A, 2))).Id());
        }

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        Assert.Equal(Enumerable.Repeat(200, 8).ToArray(), responses.Select(r => (int)r.StatusCode).ToArray());
        var stock = await Stock.AssertStockEqualsLedgerAsync(s.Http);
        Assert.All(new[] { (s.A, s.W1), (s.A, s.W2), (s.B, s.W1), (s.B, s.W2) }, pair => Assert.Equal(8m, stock[pair]));
        foreach (var response in responses)
            response.Dispose();
    }

    // ---- masters and lists ----

    [Fact]
    public async Task AC40_A_warehouse_that_is_only_a_destination_of_a_draft_transfer_is_in_use()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1));

        using var refused = await W.DeleteAsync(s.Http, s.W2);
        await Stock.ConflictAsync(refused, "IN_USE");
        await W.AssertUnchangedAsync(s.Http, s.Warehouse2);

        using var deletedDraft = await Stock.DeleteAsync(s.Http, draft.Id());
        using var deleted = await W.DeleteAsync(s.Http, s.W2);

        Assert.Equal(HttpStatusCode.NoContent, deletedDraft.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task AC40_R10_A_destination_is_freed_by_a_replace_and_kept_for_good_by_a_posting()
    {
        var s = await Stock.SetupAsync(app);
        var w3 = (await W.CreateAsync(s.Http, "W3", "Warehouse three")).Id();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1));

        // The draft now goes to W3: W2 is unused again, W3 is used.
        await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.TransferReplacement(s.W1, w3, (s.A, 1)));
        using var w3Refused = await W.DeleteAsync(s.Http, w3);
        using var w2Deleted = await W.DeleteAsync(s.Http, s.W2);
        await Stock.PostDocumentAsync(s.Http, draft.Id());
        using var w3StillRefused = await W.DeleteAsync(s.Http, w3);
        using var w1Refused = await W.DeleteAsync(s.Http, s.W1);

        await Stock.ConflictAsync(w3Refused, "IN_USE");
        Assert.Equal(HttpStatusCode.NoContent, w2Deleted.StatusCode);
        await Stock.ConflictAsync(w3StillRefused, "IN_USE");
        await Stock.ConflictAsync(w1Refused, "IN_USE");
    }

    [Fact]
    public async Task AC41_Lists_filter_transfers_and_match_a_warehouse_as_source_or_destination()
    {
        var s = await Stock.SetupAsync(app);
        var w3 = (await W.CreateAsync(s.Http, "W3", "Warehouse three")).Id();
        var receiptW1 = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var receiptW2 = await Stock.CreateAsync(s.Http, "receipt", s.W2, (s.A, 1));
        var intoW2 = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 3);
        var intoW3 = await Stock.CreateTransferAsync(s.Http, s.W1, w3, (s.A, 1));
        var fromW2 = await Stock.CreateTransferAsync(s.Http, s.W2, w3, (s.A, 1), (s.B, 1));

        async Task<Guid[]> IdsAsync(string query) =>
            (await Stock.DocumentsAsync(s.Http, query)).Items().Select(d => d.Id()).Order().ToArray();
        static Guid[] Set(params JsonElement[] documents) => documents.Select(d => d.Id()).Order().ToArray();

        var transfers = await Stock.DocumentsAsync(s.Http, "?type=transfer");
        Assert.Equal(3, transfers.Total());
        Assert.All(transfers.Items(), d => Assert.Equal("transfer", d.Str("type")));
        Assert.Equal(Set(intoW2, intoW3, fromW2), await IdsAsync("?type=transfer"));
        Assert.Equal(Set(receiptW2, intoW2, fromW2), await IdsAsync($"?warehouseId={s.W2}"));
        Assert.Equal(Set(intoW3, fromW2), await IdsAsync($"?warehouseId={w3}"));
        Assert.Equal(Set(receiptW1, intoW2, intoW3), await IdsAsync($"?warehouseId={s.W1}"));
        Assert.Equal(Set(intoW2), await IdsAsync($"?type=transfer&status=posted&warehouseId={s.W2}"));
        Assert.Equal(Set(receiptW2), await IdsAsync($"?type=receipt&warehouseId={s.W2}"));
        Assert.Equal(Set(intoW2), await IdsAsync("?search=st-000001"));

        // Every summary carries the three new properties with the values of the document itself.
        foreach (var summary in (await Stock.DocumentsAsync(s.Http)).Items())
        {
            var document = await Stock.GetAsync(s.Http, summary.Id());
            foreach (var property in new[] { "toWarehouse", "reversalOf", "reversedBy" })
            {
                Assert.True(summary.TryGetProperty(property, out var value), $"The list summary has no '{property}': {summary}");
                McpAssert.JsonEqual(document.GetProperty(property), value, $"Summary and document differ in '{property}'");
            }
            Assert.Equal(summary.Str("type") == "transfer", summary.GetProperty("toWarehouse").ValueKind == JsonValueKind.Object);
        }
    }
}
