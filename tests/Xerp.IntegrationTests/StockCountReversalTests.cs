using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 008, AC-60 to AC-63 and AC-70: a posted count is reversed like any stock document — its entries are
/// negated, never below zero, and stock is what it would be had the count never been posted — and a count
/// uses its masters like any stock document.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockCountReversalTests(XerpFixture app)
{
    private async Task<UnitSetup> WithHundredAsync()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        return s;
    }

    private static async Task AssertNoContentAsync(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    // ---- AC-60 ----

    [Fact]
    public async Task AC60_Reversing_a_count_negates_its_entry_and_copies_its_lines()
    {
        var s = await WithHundredAsync();
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 97, null));

        var reversing = await Stock.ReverseAsync(s.Http, count.Id(), Stock.NextDay, "counted the wrong shelf");

        Assert.Equal("count", reversing.Str("type"));
        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal("SC-000002", reversing.Number());
        Assert.Equal("SC-000001", reversing.GetProperty("reversalOf").Str("number"));
        Stock.AssertLink(reversing, "reversalOf", count);
        Assert.Equal(s.W1, reversing.GetProperty("warehouse").Id());
        JsonBody.AssertNull(reversing, "toWarehouse", "reversedBy");
        Counts.AssertLine(Assert.Single(reversing.DocumentLines()), quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        McpAssert.JsonEqual(count.GetProperty("lines"), reversing.GetProperty("lines"), "The reversing count's lines differ from the original's");
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, reversing.Id()));
        Assert.Equal(3m, entry.Quantity());
        Assert.Equal(1, entry.GetProperty("lineNo").GetInt32());
        Assert.True(entry.GetProperty("document").Bool("isReversal"));
        Assert.Equal("count", entry.GetProperty("document").Str("type"));
        Assert.Equal(Stock.NextDay, entry.Str("documentDate"));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var original = await Stock.GetAsync(s.Http, count.Id());
        Assert.Equal("reversed", original.Str("status"));
        Stock.AssertLink(original, "reversedBy", reversing);
        McpAssert.JsonEqual(count.GetProperty("lines"), original.GetProperty("lines"), "The original's lines changed");
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC60_R19_A_reversal_undoes_the_entries_and_does_not_restore_the_book_quantity()
    {
        // Stock is what it would be had the count never been posted: 100 + 10, not the book quantity 100.
        var s = await WithHundredAsync();
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 97, null));
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        var reversing = await Stock.ReverseAsync(s.Http, count.Id());

        Assert.Equal(new[] { (1, 3m) }, await Counts.MovementsAsync(s.Http, reversing.Id()));
        Assert.Equal(110m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // The reversing count shows the copied values, not the stock of now.
        Counts.AssertLine(reversing.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
    }

    [Fact]
    public async Task AC60_A_reversal_is_never_refused_as_outdated_and_a_reversed_or_reversing_count_is_final()
    {
        var s = await WithHundredAsync();
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 97, null));
        await Stock.IssueAsync(s.Http, s.W1, s.A, 50);
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 1);

        var reversing = await Stock.ReverseAsync(s.Http, count.Id());

        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        using var again = await Stock.SendReverseAsync(s.Http, count.Id());
        using var reverseReversal = await Stock.SendReverseAsync(s.Http, reversing.Id());
        using var reverseDraft = await Stock.SendReverseAsync(s.Http, draft.Id());
        using var putReversal = await Stock.PutAsync(s.Http, reversing.Id(), Stock.Replacement(s.W1, (s.A, 1)));
        await Stock.ConflictAsync(again, "INVALID_STATE");
        await Stock.ConflictAsync(reverseReversal, "INVALID_STATE");
        await Stock.ConflictAsync(reverseDraft, "INVALID_STATE");
        await Stock.ConflictAsync(putReversal, "INVALID_STATE");
        // The reversal moved stock, so the open draft count is now outdated like after any movement.
        using var outdated = await Stock.SendPostAsync(s.Http, draft.Id());
        await Stock.ConflictAsync(outdated, "COUNT_OUTDATED", "lines[0].quantity");
    }

    // ---- AC-61 ----

    [Fact]
    public async Task AC61_Reversing_a_count_never_takes_stock_below_zero()
    {
        var s = await Units.SetupAsync(app);
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 50, null));
        await Stock.IssueAsync(s.Http, s.W1, s.A, 30);
        var ledger = await Stock.LedgerAsync(s.Http);

        using var refused = await Stock.SendReverseAsync(s.Http, count.Id());

        var problem = await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(problem));
        await Stock.AssertUnchangedAsync(s.Http, count);
        Assert.Equal(20m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "The refused reversal wrote to the ledger");

        // Once the goods are back the reversal goes through and consumes the next number.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 30);
        var reversing = await Stock.ReverseAsync(s.Http, count.Id());
        Assert.Equal("SC-000002", reversing.Number());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC61_R18_Only_the_lines_that_added_stock_can_block_a_reversal_and_nothing_is_reversed_in_part()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 5);
        // A: shortage of 40 (reversal adds 40 back); B: surplus of 15 (reversal takes 15).
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 60, null), (s.B, 20, null));
        await Stock.IssueAsync(s.Http, s.W1, s.B, 10);

        using var refused = await Stock.SendReverseAsync(s.Http, count.Id());

        var problem = await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { "lines[1].quantity" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal("posted", (await Stock.GetAsync(s.Http, count.Id())).Str("status"));

        // A count of A to zero is a pure shortage: its reversal is never refused, whatever happened since.
        var toZero = await Counts.PostedAsync(s.Http, s.W1, (s.A, 0, null));
        var reversing = await Stock.ReverseAsync(s.Http, toZero.Id());
        Assert.Equal(new[] { (1, 60m) }, await Counts.MovementsAsync(s.Http, reversing.Id()));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-62 ----

    [Fact]
    public async Task AC62_Reversing_a_count_that_wrote_no_entries()
    {
        var s = await WithHundredAsync();
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 100, null));
        var stock = await Stock.StockMapAsync(s.Http);
        var ledgerTotal = (await Stock.LedgerAsync(s.Http)).Total();

        var reversing = await Stock.ReverseAsync(s.Http, count.Id());

        Assert.Equal("SC-000002", reversing.Number());
        Stock.AssertLink(reversing, "reversalOf", count);
        Counts.AssertLine(reversing.DocumentLines()[0], quantity: 100m, baseQuantity: 100m, book: 100m, difference: 0m);
        Assert.Empty(await Stock.EntriesAsync(s.Http, reversing.Id()));
        Assert.Equal(ledgerTotal, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Equal(stock, await Stock.StockMapAsync(s.Http));
        Assert.Equal("reversed", (await Stock.GetAsync(s.Http, count.Id())).Str("status"));
    }

    // ---- AC-63 ----

    [Fact]
    public async Task AC63_A_reversed_count_with_several_lines_nets_to_zero_per_article()
    {
        var s = await WithHundredAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 7);
        var c = (await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs)).Id();
        var d = (await Art.CreateAsync(s.Http, "D", "Article D", s.Pcs)).Id();
        await Stock.ReceiveAsync(s.Http, s.W1, d, 4);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 40);
        var before = await Stock.StockMapAsync(s.Http);
        // In boxes (shortage 4), surplus 2.5, opening 3, no difference.
        var count = await Counts.PostedAsync(s.Http, s.W1, (s.A, 8, s.Box), (s.B, 9.5m, null), (c, 3, null), (d, 4, null));
        // The conversion changes after the count: the reversal uses the posted values (007/R20).
        await Units.SetAsync(s.Http, s.A, s.Box, 10);

        var reversing = await Stock.ReverseAsync(s.Http, count.Id());

        McpAssert.JsonEqual(count.GetProperty("lines"), reversing.GetProperty("lines"), "The reversing count's lines differ from the original's");
        var originals = await Stock.EntriesAsync(s.Http, count.Id());
        var reversals = await Stock.EntriesAsync(s.Http, reversing.Id());
        Assert.Equal(new[] { (1, -4m), (2, 2.5m), (3, 3m) }, await Counts.MovementsAsync(s.Http, count.Id()));
        Assert.Equal(new[] { (1, 4m), (2, -2.5m), (3, -3m) },
            reversals.Select(e => (e.GetProperty("lineNo").GetInt32(), e.Quantity())).Order().ToArray());
        Assert.Empty(Stock.PairSums(originals.Concat(reversals)));
        Assert.All(reversals, e => Assert.True(e.GetProperty("document").Bool("isReversal")));
        // 006/R15: stock is back where it was before the count.
        Assert.Equal(before, await Stock.AssertStockEqualsLedgerAsync(s.Http));
    }

    [Fact]
    public async Task AC63_Count_reverse_count_again_always_ends_at_the_last_count()
    {
        var s = await WithHundredAsync();
        var first = await Counts.PostedAsync(s.Http, s.W1, (s.A, 97, null));
        await Stock.ReverseAsync(s.Http, first.Id());
        var second = await Counts.PostedAsync(s.Http, s.W1, (s.A, 98, null));

        Assert.Equal("SC-000003", second.Number());
        Counts.AssertLine(second.DocumentLines()[0], quantity: 98m, book: 100m);
        Assert.Equal(98m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(98m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        Assert.Equal(1, (await Stock.DocumentsAsync(s.Http, "?type=count&status=reversed")).Total());
        Assert.Equal(2, (await Stock.DocumentsAsync(s.Http, "?type=count&status=posted")).Total());
    }

    // ---- AC-70 ----

    [Fact]
    public async Task AC70_An_article_and_a_warehouse_on_a_draft_count_are_used_until_the_draft_is_deleted()
    {
        var s = await Units.SetupAsync(app);
        var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        var warehouse = await MasterApi.Warehouses.CreateAsync(s.Http, "W3", "Warehouse three");
        var draft = await Counts.CountAsync(s.Http, warehouse.Id(), article.Id(), 0);

        using var articleUsed = await s.Http.DeleteAsync($"{Art.Path}/{article.Id()}");
        using var warehouseUsed = await MasterApi.Warehouses.DeleteAsync(s.Http, warehouse.Id());
        // 005/R26: type and base unit of an article on a stock document are frozen.
        using var typeFrozen = await Stock.PutArticleAsync(s.Http, article.Id(), Stock.ArticleBody(article).With("type", "service"));

        await HttpAssert.InUseAsync(articleUsed);
        await HttpAssert.InUseAsync(warehouseUsed);
        await Stock.ConflictAsync(typeFrozen, "IN_USE");

        using (var deleteDraft = await Stock.DeleteAsync(s.Http, draft.Id()))
            await AssertNoContentAsync(deleteDraft);
        using var articleFree = await s.Http.DeleteAsync($"{Art.Path}/{article.Id()}");
        using var warehouseFree = await MasterApi.Warehouses.DeleteAsync(s.Http, warehouse.Id());
        await AssertNoContentAsync(articleFree);
        await AssertNoContentAsync(warehouseFree);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public async Task AC70_Masters_on_a_posted_count_stay_used_even_when_it_wrote_no_entry(int counted)
    {
        var s = await Units.SetupAsync(app);
        var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        var warehouse = await MasterApi.Warehouses.CreateAsync(s.Http, "W3", "Warehouse three");
        var posted = await Counts.PostedAsync(s.Http, warehouse.Id(), (article.Id(), counted, null));
        Assert.Equal(counted == 0 ? 0 : 1, (await Stock.EntriesAsync(s.Http, posted.Id())).Length);

        using var deleteArticle = await s.Http.DeleteAsync($"{Art.Path}/{article.Id()}");
        using var deleteWarehouse = await MasterApi.Warehouses.DeleteAsync(s.Http, warehouse.Id());
        using var deleteDocument = await Stock.DeleteAsync(s.Http, posted.Id());

        await HttpAssert.InUseAsync(deleteArticle);
        await HttpAssert.InUseAsync(deleteWarehouse);
        await Stock.ConflictAsync(deleteDocument, "INVALID_STATE");
        // They can still be renamed and deactivated (005/R25).
        await Stock.SetArticleActiveAsync(s.Http, article.Id(), false);
        await Stock.SetWarehouseActiveAsync(s.Http, warehouse.Id(), false);
        // 006/R17: inactive masters do not block the reversal.
        var reversing = await Stock.ReverseAsync(s.Http, posted.Id());
        Assert.Equal("SC-000002", reversing.Number());
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
    }
}
