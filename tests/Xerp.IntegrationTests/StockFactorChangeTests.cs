using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 007, AC-60 to AC-64: a draft follows the article's current factor, a posted line keeps its own; the
/// ledger never moves when a factor does; a reversal cancels exactly whatever the conversion is now.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockFactorChangeTests(XerpFixture app)
{
    private static async Task<int> LedgerCountAsync(HttpClient client) => (await Stock.LedgerAsync(client)).Total();

    /// <summary>AC-60: a draft receipt of 5 box at factor 12, the factor set to 10, the draft posted.</summary>
    private async Task<(UnitSetup S, JsonElement Posted)> PostedAtFactorTenAsync()
    {
        var s = await Units.SetupAsync(app);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));
        await Units.SetAsync(s.Http, s.A, s.Box, 10);
        return (s, await Stock.PostDocumentAsync(s.Http, draft.Id()));
    }

    // ---- AC-60 ----

    [Fact]
    public async Task AC60_A_draft_follows_the_current_factor_and_posts_it()
    {
        var s = await Units.SetupAsync(app);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));
        Units.AssertLine(draft.DocumentLines()[0], "box", 5m, 12m, 60m);

        await Units.SetAsync(s.Http, s.A, s.Box, 10);

        var read = await Stock.GetAsync(s.Http, draft.Id());
        Units.AssertLine(read.DocumentLines()[0], "box", 5m, 10m, 50m);
        Assert.Equal("draft", read.Str("status"));

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Units.AssertLine(posted.DocumentLines()[0], "box", 5m, 10m, 50m);
        Assert.Equal(50m, Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())).Quantity());
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC60_E7_Every_draft_of_the_article_follows_the_factor_and_other_articles_do_not()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.B, s.Box, 50);
        var receipt = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box), (s.A, 5, null), (s.B, 1, s.Box));
        var transfer = await Stock.CreateAsync(s.Http, Units.Transfer(s.W1, s.W2, (s.A, 2, s.Box)));

        await Units.SetAsync(s.Http, s.A, s.Box, 10);

        var lines = (await Stock.GetAsync(s.Http, receipt.Id())).DocumentLines();
        Units.AssertLine(lines[0], "box", 5m, 10m, 50m);
        Units.AssertLine(lines[1], "pcs", 5m, 1m, 5m);
        Units.AssertLine(lines[2], "box", 1m, 50m, 50m);
        Units.AssertLine((await Stock.GetAsync(s.Http, transfer.Id())).DocumentLines()[0], "box", 2m, 10m, 20m);
        // The change of factor is not a change of the draft: no write happened to it (R17).
        foreach (var draft in new[] { receipt, transfer })
        {
            var now = await Stock.GetAsync(s.Http, draft.Id());
            Assert.Equal(draft.Str("updatedAt"), now.Str("updatedAt"));
            McpAssert.JsonEqual(draft.GetProperty("updatedBy"), now.GetProperty("updatedBy"), "A change of factor changed updatedBy of a draft");
        }
    }

    [Fact]
    public async Task AC60_The_stock_check_at_posting_uses_the_factor_of_that_moment()
    {
        // R17, R19: a draft issue of 2 box was covered at factor 12 (24 of 30) and is not at factor 20.
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 30);
        var issue = await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 2, s.Box));

        await Units.SetAsync(s.Http, s.A, s.Box, 20);
        using var refused = await Stock.SendPostAsync(s.Http, issue.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, issue.Id());
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        await Units.SetAsync(s.Http, s.A, s.Box, 15);
        var posted = await Stock.PostDocumentAsync(s.Http, issue.Id());

        Units.AssertLine(posted.DocumentLines()[0], "box", 2m, 15m, 30m);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-61 ----

    [Fact]
    public async Task AC61_A_posted_line_and_the_ledger_never_move_when_the_factor_does()
    {
        var (s, posted) = await PostedAtFactorTenAsync();
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id()));
        var stock = await Stock.OnHandAsync(s.Http);

        await Units.SetAsync(s.Http, s.A, s.Box, 20);

        var after = await Stock.GetAsync(s.Http, posted.Id());
        Units.AssertLine(after.DocumentLines()[0], "box", 5m, 10m, 50m);
        // S2: nothing of the posted document or its ledger entry was updated.
        McpAssert.JsonEqual(posted, after, "The posted document changed");
        McpAssert.JsonEqual(entry, Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())), "The ledger entry changed");
        McpAssert.JsonEqual(stock, await Stock.OnHandAsync(s.Http), "Stock on hand changed");
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(1, await LedgerCountAsync(s.Http));

        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1, s.Box));
        Units.AssertLine(draft.DocumentLines()[0], "box", 1m, 20m, 20m);
        // And the list shows the posted document as before.
        var listed = (await Stock.DocumentsAsync(s.Http, "?status=posted")).Items().Single();
        Assert.Equal(posted.Id(), listed.Id());
    }

    [Fact]
    public async Task AC61_Documents_posted_at_different_factors_each_keep_their_own()
    {
        var s = await Units.SetupAsync(app);
        var posted = new List<(JsonElement Document, decimal Factor)>();
        foreach (var factor in new[] { 12m, 10m, 20m, 0.5m })
        {
            await Units.SetAsync(s.Http, s.A, s.Box, factor);
            posted.Add((await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 3, s.Box)), factor));
        }
        await Units.RemoveAsync(s.Http, s.A, s.Box);

        foreach (var (document, factor) in posted)
        {
            var now = await Stock.GetAsync(s.Http, document.Id());
            Units.AssertLine(now.DocumentLines()[0], "box", 3m, factor, 3m * factor);
            await Units.AssertEntriesAreBaseQuantitiesAsync(s.Http, now);
        }
        Assert.Equal(3m * (12m + 10m + 20m + 0.5m), await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    // ---- AC-62 ----

    [Theory]
    [InlineData("changed")]
    [InlineData("deleted")]
    [InlineData("unit inactive")]
    public async Task AC62_A_reversal_copies_the_posted_line_whatever_the_conversion_is_now(string conversion)
    {
        var (s, posted) = await PostedAtFactorTenAsync();
        if (conversion == "deleted")
            await Units.RemoveAsync(s.Http, s.A, s.Box);
        else
            await Units.SetAsync(s.Http, s.A, s.Box, 20);
        if (conversion == "unit inactive")
            await Units.SetUnitActiveAsync(s.Http, s.BoxUnit, false);

        var reversing = await Stock.ReverseAsync(s.Http, posted.Id());

        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal("receipt", reversing.Str("type"));
        Stock.AssertLink(reversing, "reversalOf", posted);
        Units.AssertLine(Assert.Single(reversing.DocumentLines()), "box", 5m, 10m, 50m);
        McpAssert.JsonEqual(posted.GetProperty("lines"), reversing.GetProperty("lines"), "The reversing document's lines differ from the original's");
        Assert.Equal(-50m, Assert.Single(await Stock.EntriesAsync(s.Http, reversing.Id())).Quantity());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        var original = await Stock.GetAsync(s.Http, posted.Id());
        Assert.Equal("reversed", original.Str("status"));
        Units.AssertLine(original.DocumentLines()[0], "box", 5m, 10m, 50m);
        // E9: the unit of measure is still named by posted lines.
        using var deleteUnit = await Units.DeleteUnitAsync(s.Http, s.Box);
        await HttpAssert.InUseAsync(deleteUnit);
    }

    [Fact]
    public async Task AC62_R20_A_reversed_transfer_and_issue_cancel_exactly_after_a_change_of_factor()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.333333m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var transfer = await Units.PostedTransferAsync(s.Http, s.W1, s.W2, (s.A, 2, s.Box), (s.A, 0.5m, s.Pack));
        var issue = await Units.PostedAsync(s.Http, "issue", s.W2, (s.A, 1, s.Box), (s.A, 0.5m, s.Pack));
        var before = await Stock.StockMapAsync(s.Http);
        Assert.Equal(100m - 24.166667m, before[(s.A, s.W1)]);
        Assert.Equal(12m, before[(s.A, s.W2)]);

        await Units.SetAsync(s.Http, s.A, s.Box, 7);
        await Units.SetAsync(s.Http, s.A, s.Pack, 3);
        var issueReversal = await Stock.ReverseAsync(s.Http, issue.Id());
        var transferReversal = await Stock.ReverseAsync(s.Http, transfer.Id());

        McpAssert.JsonEqual(issue.GetProperty("lines"), issueReversal.GetProperty("lines"), "Issue: reversing lines differ");
        McpAssert.JsonEqual(transfer.GetProperty("lines"), transferReversal.GetProperty("lines"), "Transfer: reversing lines differ");
        foreach (var (original, reversing) in new[] { (issue, issueReversal), (transfer, transferReversal) })
        {
            var entries = (await Stock.EntriesAsync(s.Http, original.Id())).Concat(await Units.AssertEntriesAreBaseQuantitiesAsync(s.Http, reversing));
            Assert.Empty(Stock.PairSums(entries));
        }
        // Net zero (006/R15): only the first receipt is left.
        var after = await Stock.AssertStockEqualsLedgerAsync(s.Http);
        Assert.Equal(100m, Assert.Single(after).Value);
        Assert.Equal((s.A, s.W1), after.Keys.Single());
    }

    [Fact]
    public async Task AC62_R20_A_reversal_is_refused_on_base_quantities_when_the_goods_have_left()
    {
        // 006/R16 with the posted base quantity: 5 box at factor 10 is 50; 45 are left, whatever the factor is now.
        var (s, posted) = await PostedAtFactorTenAsync();
        await Stock.IssueAsync(s.Http, s.W1, s.A, 5);
        await Units.SetAsync(s.Http, s.A, s.Box, 9);

        using var refused = await Stock.SendReverseAsync(s.Http, posted.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal("posted", (await Stock.GetAsync(s.Http, posted.Id())).Str("status"));
        Assert.Equal(45m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-63 ----

    [Fact]
    public async Task AC63_A_draft_that_no_longer_converts_cannot_be_posted_until_the_factor_allows_it()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.B, 1, null), (s.A, 0.000002m, s.Pack));
        Units.AssertLine(draft.DocumentLines()[1], "pack", 0.000002m, 0.5m, 0.000001m);

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.2m);
        using var refused = await Stock.SendPostAsync(s.Http, draft.Id());

        var problem = await Stock.ConflictAsync(refused, "QUANTITY_NOT_CONVERTIBLE");
        Assert.Equal(new[] { "lines[1].quantity" }, McpAssert.ErrorKeys(problem));
        // Reading never fails on R15: the line shows the current factor and what R14 gives, here 0 (007-q T-Q7).
        var still = await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(2, still.DocumentLines().Length);
        Units.AssertLine(still.DocumentLines()[0], "pcs", 1m, 1m, 1m);
        Units.AssertLine(still.DocumentLines()[1], "pack", 0.000002m, 0.2m, 0m);
        Assert.Equal(draft.Str("updatedAt"), still.Str("updatedAt"));
        Assert.Equal(draft.Id(), Assert.Single((await Stock.DocumentsAsync(s.Http)).Items()).Id());
        Assert.Equal(0, await LedgerCountAsync(s.Http));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        // The refused posting burnt no number (005/R17).
        Assert.Equal("SR-000001", posted.Number());
        Units.AssertLine(posted.DocumentLines()[1], "pack", 0.000002m, 0.5m, 0.000001m);
        Assert.Equal(0.000001m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
    }

    [Fact]
    public async Task AC63_R17_A_draft_line_above_the_maximum_is_readable_and_refused_on_saving_and_posting()
    {
        // The other direction of R15: 999999999 pack was 499999999.5 pcs at factor 0.5 and is 11999999988 at 12.
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 999999999m, s.Pack), (s.B, 1, null));
        Units.AssertLine(draft.DocumentLines()[0], "pack", 999999999m, 0.5m, 499999999.5m);

        await Units.SetAsync(s.Http, s.A, s.Pack, 12);

        // Reading shows the current factor and the base quantity R14 gives, although it is above the maximum.
        var read = await Stock.AssertDraftAsync(s.Http, draft.Id());
        Units.AssertLine(read.DocumentLines()[0], "pack", 999999999m, 12m, 11999999988m);
        Units.AssertLine(read.DocumentLines()[1], "pcs", 1m, 1m, 1m);
        Assert.Equal(draft.Str("updatedAt"), read.Str("updatedAt"));
        Assert.Equal(draft.Id(), Assert.Single((await Stock.DocumentsAsync(s.Http)).Items()).Id());

        // Saving and posting refuse it.
        using var posting = await Stock.SendPostAsync(s.Http, draft.Id());
        using var saving = await Stock.PutAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 999999999m, s.Pack), (s.B, 1, null)));
        var notPosted = await Stock.ConflictAsync(posting, "QUANTITY_NOT_CONVERTIBLE");
        var notSaved = await Stock.ConflictAsync(saving, "QUANTITY_NOT_CONVERTIBLE");
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(notPosted));
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(notSaved));
        McpAssert.JsonEqual(read, await Stock.GetAsync(s.Http, draft.Id()), "A refused save or posting changed the draft");
        Assert.Equal(0, await LedgerCountAsync(s.Http));

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SR-000001", posted.Number());
        Units.AssertLine(posted.DocumentLines()[0], "pack", 999999999m, 0.5m, 499999999.5m);
        Assert.Equal(499999999.5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC63_R18_An_inactive_warehouse_is_reported_alone_before_an_inactive_article_and_the_conversion()
    {
        // 005/R13 as amended, 006/R5, R18: header masters alone -> articles -> conversion -> stock.
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var transfer = await Stock.CreateAsync(s.Http, Units.Transfer(s.W1, s.W2, (s.A, 0.000002m, s.Pack), (s.B, 1, null)));
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.2m);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);

        async Task RefusedAsync(string code, params string[] keys)
        {
            using var refused = await Stock.SendPostAsync(s.Http, transfer.Id());
            var problem = await Stock.ConflictAsync(refused, code, keys);
            Assert.Equal(keys.Order(StringComparer.Ordinal).ToArray(), McpAssert.ErrorKeys(problem));
            await Stock.AssertDraftAsync(s.Http, transfer.Id());
        }

        await RefusedAsync("REFERENCE_INACTIVE", "toWarehouseId");

        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, true);
        await RefusedAsync("REFERENCE_INACTIVE", "lines[0].articleId", "lines[1].articleId");

        await Stock.SetArticleActiveAsync(s.Http, s.A, true);
        await Stock.SetArticleActiveAsync(s.Http, s.B, true);
        await RefusedAsync("QUANTITY_NOT_CONVERTIBLE", "lines[0].quantity");

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        await RefusedAsync("INSUFFICIENT_STOCK", "lines[0].quantity", "lines[1].quantity");
        Assert.Equal(0, await LedgerCountAsync(s.Http));
    }

    [Fact]
    public async Task AC63_R18_Conversion_is_checked_after_active_masters_and_before_stock()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        // An issue with no stock at all: the stock check would refuse it too.
        var issue = await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 0.000002m, s.Pack));
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.2m);

        using var notConvertible = await Stock.SendPostAsync(s.Http, issue.Id());
        await Stock.ConflictAsync(notConvertible, "QUANTITY_NOT_CONVERTIBLE", "lines[0].quantity");

        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        using var inactive = await Stock.SendPostAsync(s.Http, issue.Id());
        await Stock.ConflictAsync(inactive, "REFERENCE_INACTIVE", "lines[0].articleId");

        await Stock.SetArticleActiveAsync(s.Http, s.A, true);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        using var noStock = await Stock.SendPostAsync(s.Http, issue.Id());
        await Stock.ConflictAsync(noStock, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, issue.Id());
        Assert.Equal(0, await LedgerCountAsync(s.Http));
    }

    [Fact]
    public async Task AC63_A_factor_change_racing_with_a_posting_posts_one_of_the_two_factors_whole()
    {
        // Section 11: posting reads the factor inside its transaction and takes the ledger quantity from the
        // stored baseQuantity. Whichever factor wins, line, entry and stock agree.
        for (var round = 0; round < 3; round++)
        {
            var s = await Units.SetupAsync(app);
            var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));

            var post = Task.Run(() => Stock.SendPostAsync(s.Http, draft.Id()));
            var set = Task.Run(() => Units.PutAsync(s.Http, s.A, s.Box, 10));
            using var posting = await post;
            using var setting = await set;

            await HttpAssert.JsonAsync(setting, HttpStatusCode.OK);
            var posted = await HttpAssert.JsonAsync(posting, HttpStatusCode.OK);
            var line = Assert.Single(posted.DocumentLines());
            Assert.Contains(line.Factor(), new[] { 12m, 10m });
            Units.AssertLine(line, "box", 5m, line.Factor(), 5m * line.Factor());
            McpAssert.JsonEqual(posted, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the posted document");
            Assert.Equal(line.BaseQuantity(), Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())).Quantity());
            Assert.Equal(line.BaseQuantity(), await Stock.QuantityAsync(s.Http, s.A, s.W1));
        }
    }

    // ---- AC-64 ----

    [Fact]
    public async Task AC64_The_ledger_is_in_base_units_and_equals_stock_after_every_posting()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 6);
        await Units.SetAsync(s.Http, s.B, s.Box, 50);
        var posted = new List<JsonElement>();

        async Task StepAsync(Task<JsonElement> posting)
        {
            posted.Add(await posting);
            await Stock.AssertStockEqualsLedgerAsync(s.Http);
        }

        // A: 10 box (120) + 3 pack (18) + 2 pcs = 140 in W1; B: 1 box (50) in W1.
        await StepAsync(Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 10, s.Box), (s.A, 3, s.Pack), (s.A, 2, null), (s.B, 1, s.Box)));
        // A: -1 box (12) - 0.5 pack (3) = 125.
        await StepAsync(Units.PostedAsync(s.Http, "issue", s.W1, (s.A, 1, s.Box), (s.A, 0.5m, s.Pack)));
        // A: 2 box (24) + 5 pcs to W2 -> W1 96, W2 29; B: 0.2 box (10) to W2 -> W1 40, W2 10.
        await StepAsync(Units.PostedTransferAsync(s.Http, s.W1, s.W2, (s.A, 2, s.Box), (s.A, 5, null), (s.B, 0.2m, s.Box)));
        var transfer = posted[^1];

        await Units.SetAsync(s.Http, s.A, s.Box, 10);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.333333m);
        await Units.SetAsync(s.Http, s.B, s.Box, 48);

        // A in W2: + 1 box (10) + 0.5 pack (0.166667) = 39.166667; B in W2: + 1 box (48) = 58.
        await StepAsync(Units.PostedAsync(s.Http, "receipt", s.W2, (s.A, 1, s.Box), (s.A, 0.5m, s.Pack), (s.B, 1, s.Box)));
        // A in W1: - 9 box (90) = 6.
        await StepAsync(Units.PostedAsync(s.Http, "issue", s.W1, (s.A, 9, s.Box)));
        // The transfer goes back with its own factors (12 and 50): A W1 35, W2 10.166667; B W1 50, W2 48.
        await StepAsync(Stock.ReverseAsync(s.Http, transfer.Id()));
        var reversal = posted[^1];
        // A in W2: - 1 box (10) - 0.5 pack (0.166667) = 0.
        await StepAsync(Units.PostedAsync(s.Http, "issue", s.W2, (s.A, 1, s.Box), (s.A, 0.5m, s.Pack)));

        // Every posted line's entries are plus or minus its baseQuantity, in the base unit.
        var all = new List<JsonElement>();
        foreach (var document in posted)
            all.AddRange(await Units.AssertEntriesAreBaseQuantitiesAsync(s.Http, await Stock.GetAsync(s.Http, document.Id())));
        Assert.Equal(all.Count, await LedgerCountAsync(s.Http));
        // The reversed pair sums to zero per (article, warehouse).
        Assert.Empty(Stock.PairSums((await Stock.EntriesAsync(s.Http, transfer.Id())).Concat(await Stock.EntriesAsync(s.Http, reversal.Id()))));
        McpAssert.JsonEqual(transfer.GetProperty("lines"), reversal.GetProperty("lines"), "The reversing transfer's lines differ");
        Units.AssertLine(reversal.DocumentLines()[0], "box", 2m, 12m, 24m);
        Units.AssertLine(reversal.DocumentLines()[2], "box", 0.2m, 50m, 10m);

        var stock = await Stock.AssertStockEqualsLedgerAsync(s.Http);
        Assert.Equal(35m, stock[(s.A, s.W1)]);
        Assert.False(stock.ContainsKey((s.A, s.W2)), "Stock(A, W2) must be zero and not listed.");
        Assert.Equal(50m, stock[(s.B, s.W1)]);
        Assert.Equal(48m, stock[(s.B, s.W2)]);
        Assert.Equal(3, stock.Count);
        // Stock on hand shows base units only (R21).
        Assert.All((await Stock.OnHandAsync(s.Http)).Items(), item => Assert.Equal("pcs", item.GetProperty("unit").Str("code")));
    }
}
