using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, R13 as amended and spec 006, R5 (review 006, required change 1; builder): on posting the inactive
/// masters of the header are reported together and alone; inactive articles only when the header is clean.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockPostingInactiveMastersTests(XerpFixture app)
{
    /// <summary><c>REFERENCE_INACTIVE</c> with exactly these keys (Stock.ConflictAsync asserts "at least").</summary>
    private static async Task AssertOnlyAsync(HttpResponseMessage response, params string[] keys)
    {
        var problem = await Stock.ConflictAsync(response, "REFERENCE_INACTIVE", keys);
        Assert.Equal(keys.Order(StringComparer.Ordinal), McpAssert.ErrorKeys(problem).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task R5_A_transfer_with_an_inactive_destination_and_an_inactive_article_reports_the_destination_alone()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 10m);
        var draft = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1m), (s.B, 1m));
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);

        using (var destination = await Stock.SendPostAsync(s.Http, draft.Id()))
            await AssertOnlyAsync(destination, "toWarehouseId");

        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        using (var both = await Stock.SendPostAsync(s.Http, draft.Id()))
            await AssertOnlyAsync(both, "warehouseId", "toWarehouseId");

        // The header is clean again: now the article is reported.
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, true);
        using (var article = await Stock.SendPostAsync(s.Http, draft.Id()))
            await AssertOnlyAsync(article, "lines[0].articleId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    public async Task R13_A_receipt_or_an_issue_with_an_inactive_warehouse_and_an_inactive_article_reports_the_warehouse_alone(string type)
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10m);
        var draft = await Stock.CreateAsync(s.Http, type, s.W1, (s.A, 1m));
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);

        using (var warehouse = await Stock.SendPostAsync(s.Http, draft.Id()))
            await AssertOnlyAsync(warehouse, "warehouseId");

        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        using (var article = await Stock.SendPostAsync(s.Http, draft.Id()))
            await AssertOnlyAsync(article, "lines[0].articleId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
    }
}
