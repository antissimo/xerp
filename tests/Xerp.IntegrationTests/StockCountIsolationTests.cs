using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 008, AC-90 to AC-92: the book quantity is the caller's tenant's stock only; another tenant's movements
/// never make a count outdated; another tenant's count does not exist; count numbers are per tenant.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockCountIsolationTests(XerpFixture app)
{
    private sealed record Side(UnitSetup S, McpConnection? Connection) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public McpConnection Mcp => Connection ?? throw new InvalidOperationException("This side has no MCP client.");

        public ValueTask DisposeAsync() => Connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private async Task<Side> SideAsync(bool mcp = false)
    {
        var setup = await Units.SetupAsync(app);
        return new Side(setup, mcp ? await app.McpAsync(setup.Tenant.Key) : null);
    }

    [Fact]
    public async Task AC90_The_book_quantity_is_the_callers_own_stock()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);

        var draft = await Counts.CountAsync(y.Http, y.S.W1, y.S.A, 5);
        Counts.AssertLine(draft.DocumentLines()[0], quantity: 5m, baseQuantity: 5m, book: 0m, difference: 5m);
        var posted = await Stock.PostDocumentAsync(y.Http, draft.Id());

        Assert.Equal(new[] { (1, 5m) }, await Counts.MovementsAsync(y.Http, posted.Id()));
        Assert.Equal(5m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
        Assert.Equal(1, (await Stock.LedgerAsync(x.Http)).Total());
        // And the other way round: X's count sees 100, not 105.
        var ofX = await Counts.CountAsync(x.Http, x.S.W1, x.S.A, 100);
        Counts.AssertLine(ofX.DocumentLines()[0], quantity: 100m, book: 100m);
    }

    [Fact]
    public async Task AC91_Another_tenants_movements_never_make_a_count_outdated()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 20);
        var draft = await Counts.CreateAsync(y.Http, y.S.W1, (y.S.A, 18, null), (y.S.B, 2, null));

        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.B, 100);
        await Stock.IssueAsync(x.Http, x.S.W1, x.S.A, 30);
        await Stock.TransferAsync(x.Http, x.S.W1, x.S.W2, x.S.B, 10);
        await Counts.PostedAsync(x.Http, x.S.W1, (x.S.A, 1, null));

        // Reading it after X's movements shows the same book quantities (T1).
        McpAssert.JsonEqual(draft, await Stock.GetAsync(y.Http, draft.Id()), "Y's draft count changed");
        var posted = await Stock.PostDocumentAsync(y.Http, draft.Id());

        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(new[] { (1, -2m), (2, 2m) }, await Counts.MovementsAsync(y.Http, posted.Id()));
        Assert.Equal(18m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(1m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC92_Another_tenants_count_is_not_found_for_every_operation(bool posted)
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        var count = await Counts.CountAsync(x.Http, x.S.W1, x.S.A, 97);
        if (posted)
            count = await Stock.PostDocumentAsync(x.Http, count.Id());
        var id = count.Id();

        using var get = await y.Http.GetAsync($"{Stock.Documents}/{id}");
        using var put = await Stock.PutAsync(y.Http, id, Stock.Replacement(y.S.W1, (y.S.A, 1)));
        using var delete = await Stock.DeleteAsync(y.Http, id);
        using var post = await Stock.SendPostAsync(y.Http, id);
        using var reverse = await Stock.SendReverseAsync(y.Http, id);
        using var byNumber = await Stock.ByNumberAsync(y.Http, "SC-000001");

        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(put);
        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.NotFoundAsync(post);
        await HttpAssert.NotFoundAsync(reverse);
        await HttpAssert.NotFoundAsync(byNumber);
        await y.Mcp.ErrorAsync("stock_document_get", new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_update", Stock.Replacement(y.S.W1, (y.S.A, 1)).WithId(id), "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_delete", new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_post", new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_reverse", new { id, documentDate = Stock.Date }, "NOT_FOUND");
        Assert.Equal(0, (await Stock.DocumentsAsync(y.Http, "?type=count")).Total());
        Assert.Equal(0, (await y.Mcp.OkAsync("stock_document_list", new { type = "count" })).Total());

        await Stock.AssertUnchangedAsync(x.Http, count);
        Assert.Equal(posted ? 97m : 100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
        // T3: Y's first count is SC-000001 although X may have one.
        var ofY = await Counts.PostedAsync(y.Http, y.S.W1, (y.S.A, 5, null));
        Assert.Equal("SC-000001", ofY.Number());
        using var yByNumber = await Stock.ByNumberAsync(y.Http, "SC-000001");
        Assert.Equal(ofY.Id(), (await HttpAssert.JsonAsync(yByNumber, HttpStatusCode.OK)).Id());
    }

    [Fact]
    public async Task AC92_A_count_cannot_name_another_tenants_warehouse_or_article()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);

        using var warehouse = await Stock.PostAsync(y.Http, Counts.Draft(x.S.W1, (y.S.A, 1, null)));
        using var article = await Stock.PostAsync(y.Http, Counts.Draft(y.S.W1, (x.S.A, 0, null)));

        await Stock.ConflictAsync(warehouse, "REFERENCE_NOT_FOUND", "warehouseId");
        await Stock.ConflictAsync(article, "REFERENCE_NOT_FOUND", "lines[0].articleId");
        Assert.Equal(0, (await Stock.DocumentsAsync(y.Http)).Total());
        Assert.Equal(100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
    }

    [Fact]
    public async Task AC91_T1_Counts_of_two_tenants_posted_in_parallel_keep_stock_and_numbers_apart()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var work = new List<Func<Task<HttpResponseMessage>>>();
        foreach (var side in new[] { x, y })
        {
            await Stock.ReceiveAsync(side.Http, side.S.W1, side.S.A, 10);
            await Stock.ReceiveAsync(side.Http, side.S.W1, side.S.B, 10);
            // Two counts of different articles per tenant: neither can make the other outdated.
            var countA = await Counts.CountAsync(side.Http, side.S.W1, side.S.A, side == x ? 7 : 3);
            var countB = await Counts.CountAsync(side.Http, side.S.W1, side.S.B, side == x ? 12 : 0);
            work.Add(() => Stock.SendPostAsync(side.Http, countA.Id()));
            work.Add(() => Stock.SendPostAsync(side.Http, countB.Id()));
        }

        var responses = await Task.WhenAll(work.Select(send => Task.Run(send)));

        var numbers = new List<string?>();
        foreach (var response in responses)
        {
            numbers.Add((await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Number());
            response.Dispose();
        }
        Assert.Equal(new[] { "SC-000001", "SC-000002" }, numbers.Take(2).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "SC-000001", "SC-000002" }, numbers.Skip(2).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal((7m, 12m), (await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1), await Stock.QuantityAsync(x.Http, x.S.B, x.S.W1)));
        Assert.Equal((3m, 0m), (await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1), await Stock.QuantityAsync(y.Http, y.S.B, y.S.W1)));
        await Stock.AssertStockEqualsLedgerAsync(x.Http);
        await Stock.AssertStockEqualsLedgerAsync(y.Http);
    }
}
