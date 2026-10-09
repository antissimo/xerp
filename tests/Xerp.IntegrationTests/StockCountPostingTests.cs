using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 008, AC-30 to AC-39, AC-40 to AC-46, AC-50 and AC-51: posting a count writes the difference to the
/// ledger so that stock equals the count; a count whose book quantity is no longer the stock is refused and
/// changes nothing; a count is partial; counts have their own numbers.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockCountPostingTests(XerpFixture app)
{
    private static async Task<int> LedgerCountAsync(HttpClient client) => (await Stock.LedgerAsync(client)).Total();

    /// <summary>The standard setup of spec 007 with 100 of A in W1.</summary>
    private async Task<UnitSetup> WithHundredAsync()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        return s;
    }

    private static async Task<JsonElement> AssertOutdatedAsync(HttpResponseMessage response, params string[] exactKeys)
    {
        var problem = await Stock.ConflictAsync(response, "COUNT_OUTDATED");
        Assert.Equal(exactKeys, McpAssert.ErrorKeys(problem));
        return problem;
    }

    // ---- AC-30, AC-31 ----

    [Fact]
    public async Task AC30_Posting_a_count_writes_the_difference_and_stock_equals_the_count()
    {
        var s = await WithHundredAsync();
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 97);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal("count", posted.Str("type"));
        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(s.Tenant.ApiKeyId, posted.GetProperty("postedBy").GetGuid());
        Counts.AssertLine(Assert.Single(posted.DocumentLines()), quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        McpAssert.JsonEqual(posted, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the posted count");
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id()));
        Assert.Equal(-3m, entry.Quantity());
        Assert.Equal(1, entry.GetProperty("lineNo").GetInt32());
        Assert.Equal(s.A, entry.GetProperty("article").Id());
        Assert.Equal(s.W1, entry.GetProperty("warehouse").Id());
        Assert.Equal("pcs", entry.GetProperty("unit").Str("code"));
        Assert.Equal(posted.Str("documentDate"), entry.Str("documentDate"));
        var document = entry.GetProperty("document");
        Assert.Equal(("count", "SC-000001", posted.Id()), (document.Str("type"), document.Str("number"), document.Id()));
        Assert.False(document.Bool("isReversal"));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC31_A_surplus_is_posted_as_a_positive_entry()
    {
        var s = await WithHundredAsync();

        var posted = await Counts.PostedAsync(s.Http, s.W1, (s.A, 104, null));

        Counts.AssertLine(posted.DocumentLines()[0], quantity: 104m, book: 100m);
        Assert.Equal(new[] { (1, 4m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(104m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_A_count_on_empty_stock_enters_the_opening_balance()
    {
        var s = await Units.SetupAsync(app);

        var posted = await Counts.PostedAsync(s.Http, s.W1, (s.A, 50, null), (s.B, 20.5m, null));

        Assert.Equal("SC-000001", posted.Number());
        Counts.AssertLine(posted.DocumentLines()[0], quantity: 50m, book: 0m);
        Counts.AssertLine(posted.DocumentLines()[1], quantity: 20.5m, book: 0m);
        Assert.Equal(new[] { (1, 50m), (2, 20.5m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(20.5m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        // The opening balance is ordinary stock: it can be issued and transferred.
        await Stock.IssueAsync(s.Http, s.W1, s.A, 50);
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.B, 20.5m);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(20.5m, await Stock.QuantityAsync(s.Http, s.B, s.W2));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    // ---- AC-33 ----

    [Fact]
    public async Task AC33_A_line_without_a_difference_writes_no_entry_and_a_count_without_differences_still_posts()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 7);
        var ledgerBefore = await LedgerCountAsync(s.Http);

        var mixed = await Counts.PostedAsync(s.Http, s.W1, (s.A, 100, null), (s.B, 5, null));

        Assert.Equal("SC-000001", mixed.Number());
        Counts.AssertLine(mixed.DocumentLines()[0], quantity: 100m, book: 100m);
        Counts.AssertLine(mixed.DocumentLines()[1], quantity: 5m, book: 7m);
        Assert.Equal(new[] { (2, -2m) }, await Counts.MovementsAsync(s.Http, mixed.Id()));
        Assert.Equal(ledgerBefore + 1, await LedgerCountAsync(s.Http));

        var stock = await Stock.StockMapAsync(s.Http);
        var inOrder = await Counts.PostedAsync(s.Http, s.W1, (s.A, 100, null));

        Assert.Equal("posted", inOrder.Str("status"));
        Assert.Equal("SC-000002", inOrder.Number());
        Assert.Equal(JsonValueKind.String, inOrder.GetProperty("postedAt").ValueKind);
        Counts.AssertLine(inOrder.DocumentLines()[0], quantity: 100m, baseQuantity: 100m, book: 100m, difference: 0m);
        Assert.Empty(await Stock.EntriesAsync(s.Http, inOrder.Id()));
        Assert.Equal(ledgerBefore + 1, await LedgerCountAsync(s.Http));
        Assert.Equal(stock, await Stock.StockMapAsync(s.Http));
        // E5: counted 0 of an article with no stock posts too.
        var nothing = await Counts.PostedAsync(s.Http, s.W2, (s.B, 0, null));
        Assert.Equal("SC-000003", nothing.Number());
        Assert.Empty(await Stock.EntriesAsync(s.Http, nothing.Id()));
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_Counting_to_zero_removes_the_pair_from_stock_on_hand()
    {
        var s = await WithHundredAsync();

        var posted = await Counts.PostedAsync(s.Http, s.W1, (s.A, 0, null));

        Counts.AssertLine(posted.DocumentLines()[0], quantity: 0m, baseQuantity: 0m, book: 100m, difference: -100m);
        Assert.Equal(new[] { (1, -100m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        Assert.Empty((await Stock.OnHandAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}")).Items());
        Assert.Equal(0m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-35 ----

    [Fact]
    public async Task AC35_A_count_is_partial_articles_not_listed_and_other_warehouses_are_untouched()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 7);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 40);
        await Stock.ReceiveAsync(s.Http, s.W2, s.B, 3);

        var posted = await Counts.PostedAsync(s.Http, s.W1, (s.A, 60, null));

        var stock = await Stock.AssertStockEqualsLedgerAsync(s.Http);
        Assert.Equal(60m, stock[(s.A, s.W1)]);
        Assert.Equal(7m, stock[(s.B, s.W1)]);
        Assert.Equal(40m, stock[(s.A, s.W2)]);
        Assert.Equal(3m, stock[(s.B, s.W2)]);
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id()));
        Assert.Equal((s.A, s.W1, -40m), (entry.GetProperty("article").Id(), entry.GetProperty("warehouse").Id(), entry.Quantity()));
    }

    // ---- AC-36 ----

    [Fact]
    public async Task AC36_A_count_in_boxes_posts_the_difference_in_base_units()
    {
        var s = await WithHundredAsync();
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.A, 8, s.Box));

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Units.AssertLine(posted.DocumentLines()[0], "box", 8m, 12m, 96m);
        Counts.AssertLine(posted.DocumentLines()[0], quantity: 8m, baseQuantity: 96m, book: 100m, difference: -4m);
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id()));
        Assert.Equal(-4m, entry.Quantity());
        Assert.Equal("pcs", entry.GetProperty("unit").Str("code"));
        Assert.Equal(96m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // 007/R17: the posted count keeps its factor.
        await Units.SetAsync(s.Http, s.A, s.Box, 10);
        McpAssert.JsonEqual(posted, await Stock.GetAsync(s.Http, posted.Id()), "The posted count changed with the factor");
    }

    // ---- AC-37 ----

    [Fact]
    public async Task AC37_After_posting_stock_of_every_counted_article_equals_the_count()
    {
        var s = await Units.SetupAsync(app);
        var c = (await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs)).Id();
        var d = (await Art.CreateAsync(s.Http, "D", "Article D", s.Pcs)).Id();
        var e = (await Art.CreateAsync(s.Http, "E", "Article E", s.Pcs)).Id();
        var f = (await Art.CreateAsync(s.Http, "F", "Article F", s.Pcs)).Id();
        await Units.SetAsync(s.Http, e, s.Pack, 0.333333m);
        await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 100, null), (s.B, 10, null), (c, 10, null), (d, 10, null), (e, 1, null), (f, 9, null));
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 40);
        var before = await Stock.StockMapAsync(s.Http);

        // In boxes (shortage), surplus, no difference, zero, a rounded conversion (surplus), never received.
        var newcomer = (await Art.CreateAsync(s.Http, "G", "Article G", s.Pcs)).Id();
        var posted = await Counts.PostedAsync(s.Http, s.W1,
            (s.A, 8, s.Box), (s.B, 12.5m, null), (c, 10, null), (d, 0, null), (e, 3.5m, s.Pack), (newcomer, 0.000001m, null));

        var lines = posted.DocumentLines();
        Counts.AssertLine(lines[0], quantity: 8m, baseQuantity: 96m, book: 100m, difference: -4m);
        Counts.AssertLine(lines[1], quantity: 12.5m, baseQuantity: 12.5m, book: 10m, difference: 2.5m);
        Counts.AssertLine(lines[2], quantity: 10m, baseQuantity: 10m, book: 10m, difference: 0m);
        Counts.AssertLine(lines[3], quantity: 0m, baseQuantity: 0m, book: 10m, difference: -10m);
        // 3.5 x 0.333333 = 1.1666655 -> 1.166666 (007/R14).
        Counts.AssertLine(lines[4], quantity: 3.5m, baseQuantity: 1.166666m, book: 1m, difference: 0.166666m);
        Counts.AssertLine(lines[5], quantity: 0.000001m, baseQuantity: 0.000001m, book: 0m, difference: 0.000001m);
        // R12: for every line, Stock(article, W1) equals the line's baseQuantity.
        foreach (var line in lines)
            Assert.Equal(line.BaseQuantity(), await Stock.QuantityAsync(s.Http, line.GetProperty("article").Id(), s.W1));
        // R11: one entry per line with a difference, equal to the difference.
        Assert.Equal(
            lines.Where(l => l.Difference() != 0m).Select(l => (l.GetProperty("lineNo").GetInt32(), l.Difference())).ToArray(),
            await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(new[] { 1, 2, 4, 5, 6 }, (await Counts.MovementsAsync(s.Http, posted.Id())).Select(m => m.LineNo).ToArray());
        // 005/AC-60: stock on hand is exactly the ledger; what was not counted did not move.
        var after = await Stock.AssertStockEqualsLedgerAsync(s.Http);
        Assert.Equal(before[(f, s.W1)], after[(f, s.W1)]);
        Assert.Equal(before[(s.A, s.W2)], after[(s.A, s.W2)]);
        Assert.False(after.ContainsKey((d, s.W1)), "A pair counted to zero must not be listed.");
    }

    // ---- AC-38 ----

    [Fact]
    public async Task AC38_A_posted_count_is_immutable_and_later_movements_do_not_change_its_lines()
    {
        var s = await WithHundredAsync();
        var posted = await Counts.PostedAsync(s.Http, s.W1, (s.A, 97, null));
        var entries = await Stock.LedgerAsync(s.Http, $"?documentId={posted.Id()}");

        using var put = await Stock.PutAsync(s.Http, posted.Id(), Stock.Replacement(s.W1, (s.A, 50)));
        using var delete = await Stock.DeleteAsync(s.Http, posted.Id());
        using var postAgain = await Stock.SendPostAsync(s.Http, posted.Id());

        await Stock.ConflictAsync(put, "INVALID_STATE");
        await Stock.ConflictAsync(delete, "INVALID_STATE");
        await Stock.ConflictAsync(postAgain, "INVALID_STATE");
        await Stock.AssertUnchangedAsync(s.Http, posted);
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 1);

        var later = await Stock.GetAsync(s.Http, posted.Id());
        Counts.AssertLine(later.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        McpAssert.JsonEqual(posted, later, "The posted count changed");
        McpAssert.JsonEqual(entries, await Stock.LedgerAsync(s.Http, $"?documentId={posted.Id()}"), "The count's ledger entries changed");
        // A posted count is not "outdated" by later movements: it holds no claim on stock.
        Assert.Equal(106m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-39 ----

    [Fact]
    public async Task AC39_Posting_a_count_needs_active_masters()
    {
        var s = await WithHundredAsync();
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.B, 1, null), (s.A, 97, null));

        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        using var inactiveArticle = await Stock.SendPostAsync(s.Http, draft.Id());
        var problem = await Stock.ConflictAsync(inactiveArticle, "REFERENCE_INACTIVE");
        Assert.Equal(new[] { "lines[1].articleId" }, McpAssert.ErrorKeys(problem));
        await Stock.AssertDraftAsync(s.Http, draft.Id());

        await Stock.SetArticleActiveAsync(s.Http, s.A, true);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        using var inactiveWarehouse = await Stock.SendPostAsync(s.Http, draft.Id());
        await Stock.ConflictAsync(inactiveWarehouse, "REFERENCE_INACTIVE", "warehouseId");
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        // The refused postings consumed no number.
        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
    }

    [Fact]
    public async Task AC39_R9_An_inactive_warehouse_is_reported_alone_and_inactive_articles_only_when_it_is_active()
    {
        // 005/R13 as amended (008-q T-Q2): header before lines.
        var s = await WithHundredAsync();
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.B, 1, null), (s.A, 97, null));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);

        using var header = await Stock.SendPostAsync(s.Http, draft.Id());
        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(header, "REFERENCE_INACTIVE")));

        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        using var lines = await Stock.SendPostAsync(s.Http, draft.Id());
        Assert.Equal(new[] { "lines[0].articleId", "lines[1].articleId" },
            McpAssert.ErrorKeys(await Stock.ConflictAsync(lines, "REFERENCE_INACTIVE")));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Stock.SetArticleActiveAsync(s.Http, s.A, true);
        await Stock.SetArticleActiveAsync(s.Http, s.B, true);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC39_R9_Inactive_masters_and_conversion_are_checked_before_the_count_is_judged_current()
    {
        var s = await WithHundredAsync();
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.A, 0.000002m, s.Pack));
        // The count is outdated, its line no longer converts, and its article is inactive.
        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.2m);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);

        using var inactive = await Stock.SendPostAsync(s.Http, draft.Id());
        await Stock.ConflictAsync(inactive, "REFERENCE_INACTIVE", "lines[0].articleId");

        await Stock.SetArticleActiveAsync(s.Http, s.A, true);
        using var notConvertible = await Stock.SendPostAsync(s.Http, draft.Id());
        await Stock.ConflictAsync(notConvertible, "QUANTITY_NOT_CONVERTIBLE", "lines[0].quantity");

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        using var outdated = await Stock.SendPostAsync(s.Http, draft.Id());
        await AssertOutdatedAsync(outdated, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(90m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC39_R13_A_count_is_never_refused_for_stock_reasons()
    {
        // A shortage far larger than anything an issue could take, and a surplus up to the maximum quantity.
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 0.000001m);

        var toZero = await Counts.PostedAsync(s.Http, s.W1, (s.A, 0, null), (s.B, 999999999.999999m, null));

        Assert.Equal(new[] { (1, -0.000001m), (2, 999999999.999999m) }, await Counts.MovementsAsync(s.Http, toZero.Id()));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(999999999.999999m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        var back = await Counts.PostedAsync(s.Http, s.W1, (s.B, 0, null));
        Assert.Equal(new[] { (1, -999999999.999999m) }, await Counts.MovementsAsync(s.Http, back.Id()));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
    }

    // ---- AC-40, AC-41 ----

    [Fact]
    public async Task AC40_AC41_An_outdated_count_is_refused_changes_nothing_and_posts_after_it_is_saved_again()
    {
        var s = await WithHundredAsync();
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 97);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        var ledger = await Stock.LedgerAsync(s.Http);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertOutdatedAsync(refused, "lines[0].quantity");
        var still = await Stock.AssertDraftAsync(s.Http, draft.Id());
        Counts.AssertLine(still.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        McpAssert.JsonEqual(draft, still, "The refused posting changed the draft");
        Assert.Equal(90m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "The refused posting wrote to the ledger");
        // Refused again as long as nothing is saved: the refusal is not a one-off.
        using var again = await Stock.SendPostAsync(s.Http, draft.Id());
        await AssertOutdatedAsync(again, "lines[0].quantity");

        // AC-41
        var saved = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 97)));
        Counts.AssertLine(saved.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 90m, difference: 7m);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SC-000001", posted.Number());
        Counts.AssertLine(posted.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 90m, difference: 7m);
        Assert.Equal(new[] { (1, 7m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("transfer out")]
    [InlineData("transfer in")]
    [InlineData("reversal")]
    [InlineData("count")]
    public async Task AC40_R10_Any_movement_of_the_counted_pair_makes_the_count_outdated(string movement)
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 20);
        var earlier = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 105);
        Counts.AssertLine(draft.DocumentLines()[0], quantity: 105m, book: 105m);

        switch (movement)
        {
            case "receipt": await Stock.ReceiveAsync(s.Http, s.W1, s.A, 0.000001m); break;
            case "transfer out": await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 1); break;
            case "transfer in": await Stock.TransferAsync(s.Http, s.W2, s.W1, s.A, 1); break;
            case "reversal": await Stock.ReverseAsync(s.Http, earlier.Id()); break;
            case "count": await Counts.PostedAsync(s.Http, s.W1, (s.A, 104, null)); break;
        }
        var stock = await Stock.StockMapAsync(s.Http);
        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertOutdatedAsync(refused, "lines[0].quantity");
        Assert.Equal(stock, await Stock.StockMapAsync(s.Http));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        // Saved again, it posts and stock equals the count whatever happened in between.
        await Counts.ResaveAsync(s.Http, draft.Id());
        await Stock.PostDocumentAsync(s.Http, draft.Id());
        Assert.Equal(105m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC40_An_outdated_count_whose_counted_quantity_equals_the_new_stock_is_still_refused()
    {
        // R10 compares stock with the book quantity, not with the counted quantity: the difference shown (-3)
        // is not what posting would write (0), so the count must be saved again.
        var s = await WithHundredAsync();
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 97);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 3);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertOutdatedAsync(refused, "lines[0].quantity");
        var saved = await Counts.ResaveAsync(s.Http, draft.Id());
        Counts.AssertLine(saved.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 97m, difference: 0m);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        Assert.Empty(await Stock.EntriesAsync(s.Http, posted.Id()));
    }

    // ---- AC-42 ----

    [Fact]
    public async Task AC42_Only_the_outdated_lines_are_reported_and_nothing_is_posted()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 7);
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.A, 97, null), (s.B, 5, null));
        await Stock.IssueAsync(s.Http, s.W1, s.B, 1);
        var ledgerCount = await LedgerCountAsync(s.Http);

        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertOutdatedAsync(refused, "lines[1].quantity");
        // E7: not even the current line was posted.
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(ledgerCount, await LedgerCountAsync(s.Http));
        await Stock.AssertDraftAsync(s.Http, draft.Id());

        // Both lines outdated: both keys.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);
        using var both = await Stock.SendPostAsync(s.Http, draft.Id());
        await AssertOutdatedAsync(both, "lines[0].quantity", "lines[1].quantity");

        var saved = await Counts.ResaveAsync(s.Http, draft.Id());
        Counts.AssertLine(saved.DocumentLines()[0], quantity: 97m, book: 101m);
        Counts.AssertLine(saved.DocumentLines()[1], quantity: 5m, book: 6m);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(new[] { (1, -4m), (2, -1m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
    }

    // ---- AC-43 ----

    [Fact]
    public async Task AC43_Only_the_quantity_matters_and_only_in_the_counted_warehouse()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 40);
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 97);

        // Stock of the pair moves and comes back; other pairs move for good.
        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.IssueAsync(s.Http, s.W2, s.A, 15);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 3);
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.B, 1);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Counts.AssertLine(posted.DocumentLines()[0], quantity: 97m, book: 100m);
        Assert.Equal(new[] { (1, -3m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(25m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(2m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_Of_two_draft_counts_of_one_pair_the_second_is_outdated_once_the_first_changed_stock()
    {
        var s = await WithHundredAsync();
        var first = await Counts.CountAsync(s.Http, s.W1, s.A, 97);
        var second = await Counts.CountAsync(s.Http, s.W1, s.A, 95);

        await Stock.PostDocumentAsync(s.Http, first.Id());
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        using var refused = await Stock.SendPostAsync(s.Http, second.Id());

        await AssertOutdatedAsync(refused, "lines[0].quantity");
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        var saved = await Counts.ResaveAsync(s.Http, second.Id());
        Counts.AssertLine(saved.DocumentLines()[0], quantity: 95m, book: 97m);
        var posted = await Stock.PostDocumentAsync(s.Http, second.Id());

        Assert.Equal("SC-000002", posted.Number());
        Assert.Equal(new[] { (1, -2m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(95m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC44_E8_A_first_count_without_a_difference_leaves_the_second_current()
    {
        var s = await WithHundredAsync();
        var first = await Counts.CountAsync(s.Http, s.W1, s.A, 100);
        var second = await Counts.CountAsync(s.Http, s.W1, s.A, 95);

        await Stock.PostDocumentAsync(s.Http, first.Id());
        var posted = await Stock.PostDocumentAsync(s.Http, second.Id());

        Assert.Equal("SC-000002", posted.Number());
        Assert.Equal(95m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-45, AC-46 ----

    [Fact]
    public async Task AC45_A_count_racing_with_an_issue_has_only_two_outcomes()
    {
        var outcomes = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var s = await Units.SetupAsync(app);
            await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
            var count = await Counts.CountAsync(s.Http, s.W1, s.A, 4);
            var issue = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 8));

            var postCount = Task.Run(() => Stock.SendPostAsync(s.Http, count.Id()));
            var postIssue = Task.Run(() => Stock.SendPostAsync(s.Http, issue.Id()));
            using var countResponse = await postCount;
            using var issueResponse = await postIssue;

            var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);
            if (countResponse.StatusCode == HttpStatusCode.OK)
            {
                await Stock.ConflictAsync(issueResponse, "INSUFFICIENT_STOCK", "lines[0].quantity");
                Assert.Equal(4m, stock);
                Assert.Equal(new[] { (1, -6m) }, await Counts.MovementsAsync(s.Http, count.Id()));
                await Stock.AssertDraftAsync(s.Http, issue.Id());
                outcomes.Add("count");
            }
            else
            {
                await HttpAssert.JsonAsync(issueResponse, HttpStatusCode.OK);
                await AssertOutdatedAsync(countResponse, "lines[0].quantity");
                Assert.Equal(2m, stock);
                Assert.Empty(await Stock.EntriesAsync(s.Http, count.Id()));
                var draft = await Stock.AssertDraftAsync(s.Http, count.Id());
                Counts.AssertLine(draft.DocumentLines()[0], quantity: 4m, book: 10m);
                outcomes.Add("issue");
            }
            await Stock.AssertStockEqualsLedgerAsync(s.Http);
        }
        Assert.Equal(10, outcomes.Count);
    }

    [Fact]
    public async Task AC45_R16_A_count_racing_with_a_receipt_is_either_posted_first_or_outdated()
    {
        for (var round = 0; round < 5; round++)
        {
            var s = await Units.SetupAsync(app);
            await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
            var count = await Counts.CountAsync(s.Http, s.W1, s.A, 4);
            var receipt = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5));

            var postCount = Task.Run(() => Stock.SendPostAsync(s.Http, count.Id()));
            var postReceipt = Task.Run(() => Stock.SendPostAsync(s.Http, receipt.Id()));
            using var countResponse = await postCount;
            using var receiptResponse = await postReceipt;

            // A receipt is never refused.
            await HttpAssert.JsonAsync(receiptResponse, HttpStatusCode.OK);
            var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);
            if (countResponse.StatusCode == HttpStatusCode.OK)
            {
                // The count came first (10 -> 4), then the receipt (+5).
                Assert.Equal(9m, stock);
                Assert.Equal(new[] { (1, -6m) }, await Counts.MovementsAsync(s.Http, count.Id()));
            }
            else
            {
                await AssertOutdatedAsync(countResponse, "lines[0].quantity");
                Assert.Equal(15m, stock);
                await Stock.AssertDraftAsync(s.Http, count.Id());
            }
            await Stock.AssertStockEqualsLedgerAsync(s.Http);
        }
    }

    [Fact]
    public async Task AC45_R16_Two_counts_of_one_pair_posted_in_parallel_post_exactly_one()
    {
        for (var round = 0; round < 5; round++)
        {
            var s = await WithHundredAsync();
            var first = await Counts.CountAsync(s.Http, s.W1, s.A, 97);
            var second = await Counts.CountAsync(s.Http, s.W1, s.A, 95);

            var responses = await Task.WhenAll(new[] { first, second }.Select(d => Task.Run(() => Stock.SendPostAsync(s.Http, d.Id()))));

            Assert.Equal(new[] { 200, 409 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
            var winner = responses[0].StatusCode == HttpStatusCode.OK ? 0 : 1;
            await AssertOutdatedAsync(responses[1 - winner], "lines[0].quantity");
            Assert.Equal("SC-000001", (await HttpAssert.JsonAsync(responses[winner], HttpStatusCode.OK)).Number());
            Assert.Equal(winner == 0 ? 97m : 95m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
            Assert.Equal(2, await LedgerCountAsync(s.Http));
            await Stock.AssertStockEqualsLedgerAsync(s.Http);
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task AC46_The_same_count_posted_five_times_in_parallel_is_posted_once()
    {
        var s = await WithHundredAsync();
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 97);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => Stock.SendPostAsync(s.Http, draft.Id()))));

        Assert.Equal(new[] { 200, 409, 409, 409, 409 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await Stock.ConflictAsync(refused, "INVALID_STATE");
        var posted = await HttpAssert.JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK);
        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(new[] { (1, -3m) }, await Counts.MovementsAsync(s.Http, draft.Id()));
        Assert.Equal(97m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // One number: the next count gets the second.
        Assert.Equal("SC-000002", (await Counts.PostedAsync(s.Http, s.W1, (s.A, 97, null))).Number());
        foreach (var response in responses)
            response.Dispose();
    }

    // ---- AC-50, AC-51 ----

    [Fact]
    public async Task AC50_Counts_have_their_own_gapless_numbers()
    {
        var s = await WithHundredAsync();                                             // SR-000001
        var firstCount = await Counts.PostedAsync(s.Http, s.W1, (s.A, 90, null));
        var issue = await Stock.IssueAsync(s.Http, s.W1, s.A, 1);
        var transfer = await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 1);
        var outdated = await Counts.CountAsync(s.Http, s.W1, s.A, 80);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);
        using (var refused = await Stock.SendPostAsync(s.Http, outdated.Id()))
            await AssertOutdatedAsync(refused, "lines[0].quantity");
        var secondCount = await Counts.PostedAsync(s.Http, s.W2, (s.A, 1, null));

        Assert.Equal(("SC-000001", "SC-000002"), (firstCount.Number(), secondCount.Number()));
        Assert.Equal(("SI-000001", "ST-000001", "SR-000002"), (issue.Number(), transfer.Number(), receipt.Number()));
        using var byNumber = await Stock.ByNumberAsync(s.Http, "SC-000001");
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, firstCount.Id()), await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK));
        using var unused = await Stock.ByNumberAsync(s.Http, "SC-000003");
        await HttpAssert.NotFoundAsync(unused);
    }

    [Fact]
    public async Task AC51_The_list_filters_counts_and_the_ledger_shows_only_lines_with_a_difference_in_line_order()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 7);
        var c = (await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs)).Id();
        var posted = await Counts.PostedAsync(s.Http, s.W1, (c, 3, null), (s.B, 7, null), (s.A, 99, null));
        var draft = await Counts.CountAsync(s.Http, s.W2, s.A, 1);
        await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1));

        var counts = await Stock.DocumentsAsync(s.Http, "?type=count");
        var postedCounts = await Stock.DocumentsAsync(s.Http, "?type=count&status=posted");
        var inW2 = await Stock.DocumentsAsync(s.Http, $"?type=count&warehouseId={s.W2}");
        var entries = await Stock.LedgerAsync(s.Http, $"?documentId={posted.Id()}");

        Assert.Equal(2, counts.Total());
        Assert.All(counts.Items(), item => Assert.Equal("count", item.Str("type")));
        Assert.Equal(new[] { posted.Id(), draft.Id() }.Order().ToArray(), counts.Items().Select(i => i.Id()).Order().ToArray());
        Assert.Equal(posted.Id(), Assert.Single(postedCounts.Items()).Id());
        Assert.Equal(draft.Id(), Assert.Single(inW2.Items()).Id());
        Assert.Equal(2, entries.Total());
        Assert.Equal(new[] { (1, 3m), (3, -1m) }, entries.Items().Select(e => (e.GetProperty("lineNo").GetInt32(), e.Quantity())).ToArray());
        Assert.All(entries.Items(), e => Assert.Equal("count", e.GetProperty("document").Str("type")));
        // The ledger of the article shows the count among its other movements.
        var ofA = await Stock.LedgerAsync(s.Http, $"?articleId={s.A}&warehouseId={s.W1}");
        Assert.Equal(new[] { 100m, -1m }, ofA.Items().Select(e => e.Quantity()).ToArray());
    }
}
