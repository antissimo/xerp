using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 006, AC-90 to AC-92 (tenant isolation over HTTP and through tools): for tenant Y, the warehouses,
/// documents and transfer numbers of tenant X do not exist, and nothing Y does can reverse or move X's stock.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockTransferReversalIsolationTests(XerpFixture app)
{
    private sealed record Side(StockSetup S, McpConnection? Connection) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public McpConnection Mcp => Connection ?? throw new InvalidOperationException("This side has no MCP client.");

        public ValueTask DisposeAsync() => Connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private async Task<Side> SideAsync(bool mcp = false)
    {
        var setup = await Stock.SetupAsync(app);
        return new Side(setup, mcp ? await app.McpAsync(setup.Tenant.Key) : null);
    }

    [Fact]
    public async Task AC90_Another_tenants_warehouse_is_not_a_destination()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 5);
        var draft = await Stock.CreateTransferAsync(y.Http, y.S.W1, y.S.W2, (y.S.A, 1));

        using var create = await Stock.PostAsync(y.Http, Stock.Transfer(y.S.W1, x.S.W2, (y.S.A, 1)));
        using var replace = await Stock.PutAsync(y.Http, draft.Id(), Stock.TransferReplacement(y.S.W1, x.S.W2, (y.S.A, 1)));
        using var random = await Stock.PostAsync(y.Http, Stock.Transfer(y.S.W1, Guid.NewGuid(), (y.S.A, 1)));

        var foreign = await Stock.ConflictAsync(create, "REFERENCE_NOT_FOUND", "toWarehouseId");
        await Stock.ConflictAsync(replace, "REFERENCE_NOT_FOUND", "toWarehouseId");
        // T1: exactly as a random id.
        var unknown = await Stock.ConflictAsync(random, "REFERENCE_NOT_FOUND", "toWarehouseId");
        Assert.Equal(McpAssert.ErrorKeys(unknown), McpAssert.ErrorKeys(foreign));
        await y.Mcp.ErrorAsync("stock_document_create", Stock.Transfer(y.S.W1, x.S.W2, (y.S.A, 1)), "REFERENCE_NOT_FOUND", "toWarehouseId");
        await Stock.AssertUnchangedAsync(y.Http, draft);
        Assert.Equal(2, (await Stock.DocumentsAsync(y.Http)).Total());
        // X's warehouse did not become used by Y's attempts.
        using var deleted = await MasterApi.Warehouses.DeleteAsync(x.Http, x.S.W2);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("transfer")]
    public async Task AC91_Another_tenants_document_cannot_be_reversed(string type)
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        var document = type == "transfer"
            ? await Stock.TransferAsync(x.Http, x.S.W1, x.S.W2, x.S.A, 30)
            : await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.B, 30);
        // Y has stock of its own under the same codes, so a leak could not hide behind missing stock.
        await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 100);
        var xStock = await Stock.StockMapAsync(x.Http);
        var xDocuments = (await Stock.DocumentsAsync(x.Http)).Total();
        var yStock = await Stock.StockMapAsync(y.Http);

        using var overHttp = await Stock.SendReverseAsync(y.Http, document.Id());

        await HttpAssert.NotFoundAsync(overHttp);
        await y.Mcp.ErrorAsync("stock_document_reverse", new { id = document.Id(), documentDate = Stock.Date }, "NOT_FOUND");
        var after = await Stock.GetAsync(x.Http, document.Id());
        Assert.Equal("posted", after.Str("status"));
        McpAssert.JsonEqual(document, after, "Y's attempt changed X's document");
        Assert.Equal(xStock, await Stock.StockMapAsync(x.Http));
        Assert.Equal(xDocuments, (await Stock.DocumentsAsync(x.Http)).Total());
        Assert.Equal(yStock, await Stock.StockMapAsync(y.Http));
        Assert.Equal(1, (await Stock.DocumentsAsync(y.Http)).Total());
        // X can still reverse it.
        var reversing = await Stock.ReverseAsync(x.Http, document.Id());
        Stock.AssertLink(reversing, "reversalOf", document);
    }

    [Fact]
    public async Task AC92_Transfer_numbers_are_per_tenant_and_a_reversal_advances_only_its_own_tenants_series()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var xReceipt = await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 10);
        var xTransfer = await Stock.TransferAsync(x.Http, x.S.W1, x.S.W2, x.S.A, 4);
        var xSecond = await Stock.TransferAsync(x.Http, x.S.W1, x.S.W2, x.S.A, 1);
        await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 10);

        var yTransfer = await Stock.TransferAsync(y.Http, y.S.W1, y.S.W2, y.S.A, 4);
        var xReversal = await Stock.ReverseAsync(x.Http, xTransfer.Id());
        var xIssue = await Stock.IssueAsync(x.Http, x.S.W1, x.S.A, 1);
        var xIssueReversal = await Stock.ReverseAsync(x.Http, xIssue.Id());
        var ySecond = await Stock.TransferAsync(y.Http, y.S.W1, y.S.W2, y.S.A, 1);
        var yIssue = await Stock.IssueAsync(y.Http, y.S.W1, y.S.A, 1);
        var yReceipt = await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 1);

        Assert.Equal(("SR-000001", "ST-000001", "ST-000002", "ST-000003"),
            (xReceipt.Number(), xTransfer.Number(), xSecond.Number(), xReversal.Number()));
        Assert.Equal(("SI-000001", "SI-000002"), (xIssue.Number(), xIssueReversal.Number()));
        Assert.Equal(("ST-000001", "ST-000002", "SI-000001", "SR-000002"),
            (yTransfer.Number(), ySecond.Number(), yIssue.Number(), yReceipt.Number()));
        // Each tenant's number resolves to its own document.
        using var xByNumber = await Stock.ByNumberAsync(x.Http, "ST-000001");
        using var yByNumber = await Stock.ByNumberAsync(y.Http, "ST-000001");
        using var yThird = await Stock.ByNumberAsync(y.Http, "ST-000003");
        Assert.Equal(xTransfer.Id(), (await HttpAssert.JsonAsync(xByNumber, HttpStatusCode.OK)).Id());
        Assert.Equal(yTransfer.Id(), (await HttpAssert.JsonAsync(yByNumber, HttpStatusCode.OK)).Id());
        await HttpAssert.NotFoundAsync(yThird);
        // And each tenant's stock is its own: X's reversal moved nothing of Y's.
        Assert.Equal(5m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W2));
        Assert.Equal(5m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(1m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W2));
        Assert.Equal(9m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
    }

    [Fact]
    public async Task AC92_T3_Parallel_transfers_and_reversals_of_two_tenants_keep_stock_and_numbers_apart()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var work = new List<Func<Task<HttpResponseMessage>>>();
        foreach (var side in new[] { x, y })
        {
            await Stock.ReceiveAsync(side.Http, side.S.W1, side.S.A, 20);
            for (var i = 0; i < 4; i++)
            {
                var draft = await Stock.CreateTransferAsync(side.Http, side.S.W1, side.S.W2, (side.S.A, 2));
                var posted = await Stock.TransferAsync(side.Http, side.S.W1, side.S.W2, side.S.A, 1);
                work.Add(() => Stock.SendPostAsync(side.Http, draft.Id()));
                work.Add(() => Stock.SendReverseAsync(side.Http, posted.Id()));
            }
        }

        var responses = await Task.WhenAll(work.Select(request => Task.Run(request)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"Unexpected status {(int)r.StatusCode}"));
        foreach (var side in new[] { x, y })
        {
            // 4 transfers posted before, 4 posted and 4 reversed in parallel: ST-000001 … ST-000012, no gap.
            var transfers = await Stock.DocumentsAsync(side.Http, "?type=transfer&limit=500");
            Assert.Equal(Enumerable.Range(1, 12).Select(n => $"ST-{n:D6}").ToArray(),
                transfers.Items().Select(d => d.Number()).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(12m, await Stock.QuantityAsync(side.Http, side.S.A, side.S.W1));
            Assert.Equal(8m, await Stock.QuantityAsync(side.Http, side.S.A, side.S.W2));
            await Stock.AssertStockEqualsLedgerAsync(side.Http);
        }
        foreach (var response in responses)
            response.Dispose();
    }
}
