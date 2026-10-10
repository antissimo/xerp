using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-50 to AC-54 and AC-120: <c>stock.negativeStockAllowed</c> (R17–R21, E9, E10). With <c>true</c>
/// stock on hand goes below zero and the invariant "stored balance == sum of the ledger" still holds; back at
/// <c>false</c> the negative pairs stay and only movements that lower a pair below zero are refused.
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleNegativeStockTests(XerpFixture app)
{
    private static readonly OrderApi SO = OrderApi.Sales;

    private async Task<OrderSetup> AllowedAsync()
    {
        var s = await Orders.SetupAsync(app);
        await Rules.SetAsync(s.Http, Rules.NegativeStock, true);
        return s;
    }

    /// <summary>Negative stock allowed and A in W1 at −5.</summary>
    private async Task<OrderSetup> MinusFiveAsync()
    {
        var s = await AllowedAsync();
        await Stock.IssueAsync(s.Http, s.W1, s.A, 5);
        return s;
    }

    [Fact]
    public async Task AC50_With_negative_stock_allowed_an_issue_from_an_empty_pair_posts_and_stock_is_below_zero()
    {
        var s = await AllowedAsync();

        var posted = await Stock.IssueAsync(s.Http, s.W1, s.A, 5);

        Assert.Equal(("posted", "SI-000001"), (posted.Str("status"), posted.Number()));
        Assert.Equal(-5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var item = await Orders.OnHandItemAsync(s.Http, s.A, s.W1);
        Assert.True(item is not null, "Stock on hand does not list the negative pair (R20).");
        Assert.Equal((-5m, 0m, 0m, -5m), item!.Value.Quantities());
        Assert.Equal(-5m, Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())).Quantity());
        Assert.Equal(-5m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
        var balanced = await Rules.AssertBalancedAsync(s.Http);
        Assert.Equal(-5m, balanced[(s.A, s.W1)]);
        Assert.Equal(0, await Balance.DifferencesAsync(s.Http));
    }

    [Fact]
    public async Task AC50_R18_With_negative_stock_allowed_a_pair_with_stock_goes_below_zero_too()
    {
        var s = await AllowedAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 3);

        await Stock.IssueAsync(s.Http, s.W1, s.A, 3.5m);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 0.000001m);

        Assert.Equal(-0.500001m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC51_With_negative_stock_allowed_a_transfer_a_reversal_and_a_delivery_post_without_stock()
    {
        var s = await AllowedAsync();
        var http = s.Http;

        // A transfer out of an empty warehouse (E9).
        var transfer = await Stock.TransferAsync(http, s.W1, s.W2, s.A, 5);

        Assert.Equal("posted", transfer.Str("status"));
        Assert.Equal((-5m, 5m), (await Stock.QuantityAsync(http, s.A, s.W1), await Stock.QuantityAsync(http, s.A, s.W2)));
        Assert.Equal(0m, await Stock.TotalAsync(http, s.A));

        // Reversing a receipt whose goods were issued since.
        var receipt = await Stock.ReceiveAsync(http, s.W1, s.B, 10);
        await Stock.IssueAsync(http, s.W1, s.B, 10);

        var reversing = await Stock.ReverseAsync(http, receipt.Id());

        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal(-10m, await Stock.QuantityAsync(http, s.B, s.W1));

        // A delivery above stock, within its order.
        var order = await SO.OrderedAsync(s, s.W2, (s.B, 4, null, 1m));

        var delivery = await SO.FulfilAsync(http, order, 1, 4);

        Assert.Equal("posted", delivery.Str("status"));
        Assert.Equal(-4m, await Stock.QuantityAsync(http, s.B, s.W2));
        Assert.Equal("full", SO.ProgressOf(await SO.GetAsync(http, order.Id())));
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC51_R18_With_negative_stock_allowed_the_order_checks_still_apply()
    {
        // "Everything else about posting is unchanged": the rule allows less than zero, not more than ordered.
        var s = await AllowedAsync();
        var order = await SO.OrderedAsync(s, (s.A, 4, null, 1m));

        var (draft, response) = await SO.TryFulfilAsync(s.Http, order, 1, 5);

        using (response)
            await Rules.ExceedsAsync(response, SO, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC52_With_negative_pairs_rebuild_corrects_nothing_and_the_stock_list_filters_by_sign()
    {
        var s = await MinusFiveAsync();
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.B, 2);

        var (pairs, corrected) = await Balance.RebuildAsync(http);

        Assert.Equal(0, corrected);
        Assert.True(pairs >= 2, $"Rebuild reports {pairs} pairs; (A, W1) and (B, W1) have ledger entries.");
        var differences = await Balance.DifferenceListAsync(http);
        Assert.Equal((0, 0), (differences.Total(), differences.Items().Length));
        Assert.Equal(-5m, await Stock.QuantityAsync(http, s.A, s.W1));
        Assert.Equal(-5m, await Balance.ListedQuantityAsync(http, s.A, s.W1));
        // R20: hasStock=true keeps quantity > 0, hasStock=false keeps quantity <= 0.
        Assert.Equal(new[] { "A" }, (await Balance.StockListAsync(http, s.W1, "?hasStock=false")).ArticleCodes());
        Assert.Equal(new[] { "B" }, (await Balance.StockListAsync(http, s.W1, "?hasStock=true")).ArticleCodes());
        Assert.Equal(new[] { "A", "B" }, (await Balance.StockListAsync(http, s.W2, "?hasStock=false")).ArticleCodes());
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC53_A_count_of_a_negative_pair_shows_the_negative_book_quantity_and_brings_it_to_the_counted()
    {
        var s = await MinusFiveAsync();

        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 2);

        Counts.AssertLine(draft.DocumentLines()[0], quantity: 2, baseQuantity: 2, book: -5, difference: 7);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Counts.AssertLine(posted.DocumentLines()[0], 2, 2, -5, 7);
        Assert.Equal(7m, Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())).Quantity());
        Assert.Equal(2m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC54_Back_at_refused_negative_pairs_stay_and_only_a_movement_that_lowers_one_is_refused()
    {
        var s = await MinusFiveAsync();
        var http = s.Http;
        var documents = await Stock.DocumentsAsync(http);
        var ledger = await Stock.LedgerAsync(http);
        var onHand = await Stock.OnHandAsync(http);
        var stockList = await Balance.StockPageAsync(http, s.W1);

        Rules.AssertTenantValue(await Rules.SetAsync(http, Rules.NegativeStock, false), Rules.NegativeStock, false, s.Tenant.ApiKeyId);

        // R15: nothing stored changed when the rule changed.
        McpAssert.JsonEqual(documents, await Stock.DocumentsAsync(http), "The documents changed with the rule");
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(http), "The ledger changed with the rule");
        McpAssert.JsonEqual(onHand, await Stock.OnHandAsync(http), "Stock on hand changed with the rule");
        McpAssert.JsonEqual(stockList, await Balance.StockPageAsync(http, s.W1), "The stock list changed with the rule");
        Assert.Equal(-5m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "after the Set");

        // R21: a posting that raises the pair is accepted even if it stays negative.
        var receipt = await Stock.ReceiveAsync(http, s.W1, s.A, 3);
        Assert.Equal(-2m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "after the receipt of 3");

        // One that lowers it is refused.
        var (issue, refused) = await Rules.TryIssueAsync(http, s.W1, s.A, 1);
        using (refused)
            await Rules.InsufficientAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, issue);

        using (var reversal = await Stock.SendReverseAsync(http, receipt.Id()))
            await Rules.InsufficientAsync(reversal, "lines[0].quantity");
        Assert.Equal("posted", (await Stock.GetAsync(http, receipt.Id())).Str("status"));
        JsonBody.AssertNull(await Stock.GetAsync(http, receipt.Id()), "reversedBy");

        // A transfer out of the negative pair lowers it; a transfer into it raises it.
        var outOf = await Stock.CreateTransferAsync(http, s.W1, s.W2, (s.A, 1));
        using (var lower = await Stock.SendPostAsync(http, outOf.Id()))
            await Rules.InsufficientAsync(lower, "lines[0].quantity");
        await Stock.ReceiveAsync(http, s.W2, s.A, 1);
        await Stock.TransferAsync(http, s.W2, s.W1, s.A, 1);
        Assert.Equal((-1m, 0m), (await Stock.QuantityAsync(http, s.A, s.W1), await Stock.QuantityAsync(http, s.A, s.W2)));
        await Rules.AssertBalancedAsync(http, "after the refusals");

        // E10: a count of 0 posts (R29) and the pair is 0.
        await Counts.PostedAsync(http, s.W1, (s.A, 0, null));
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));
        Assert.Null(await Orders.OnHandItemAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "after the count");
        // With the pair at zero the rule is the one of spec 005 again.
        await Balance.AssertBalancedAsync(s.U.S, "at the end");
    }

    [Fact]
    public async Task AC54_R17_An_issue_that_takes_a_pair_exactly_to_zero_is_posted_and_one_unit_more_is_refused()
    {
        // The boundary of R17 (Q + Δ < 0) at the default.
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);

        var (tooMuch, refused) = await Rules.TryIssueAsync(s.Http, s.W1, s.A, 5.000001m);
        using (refused)
            await Rules.InsufficientAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, tooMuch);
        var exact = await Stock.IssueAsync(s.Http, s.W1, s.A, 5);

        // The refused posting consumed no number.
        Assert.Equal("SI-000001", exact.Number());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Balance.AssertBalancedAsync(s.U.S);
    }
}
