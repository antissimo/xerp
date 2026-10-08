using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-10 (the routes need a tenant key) and AC-90 to AC-95 (tenant isolation over HTTP and through
/// tools): for tenant Y, the documents, ledger entries, stock and numbers of tenant X do not exist.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockIsolationTests(XerpFixture app)
{
    // ---- authentication ----

    public static TheoryData<string, string> ProtectedRequests() => new()
    {
        { "GET", Stock.Documents },
        { "POST", Stock.Documents },
        { "POST", Stock.Documents + "/0199c0de-0000-7000-8000-000000000001/post" },
        { "GET", Stock.OnHand },
        { "GET", Stock.Ledger },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST" && !path.EndsWith("/post", StringComparison.Ordinal))
            request.Content = HttpAssert.Raw(
                """{ "type": "receipt", "documentDate": "2026-10-09", "warehouseId": "0199c0de-0000-7000-8000-000000000002", "lines": [ { "articleId": "0199c0de-0000-7000-8000-000000000003", "quantity": 1 } ] }""");
        return request;
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task AC10_Without_a_credential_is_unauthenticated_and_the_admin_key_is_forbidden(string method, string path)
    {
        using var anonymous = app.Anonymous();
        using var admin = app.Admin();

        using var noCredential = await anonymous.SendAsync(Request(method, path));
        using var adminKey = await admin.SendAsync(Request(method, path));

        await HttpAssert.UnauthenticatedAsync(noCredential);
        await HttpAssert.ForbiddenAsync(adminKey);
    }

    // ---- isolation ----

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

    private static void AssertEmpty(JsonElement list)
    {
        Assert.Empty(list.Items());
        Assert.Equal(0, list.Total());
    }

    [Fact]
    public async Task AC90_Another_tenants_stock_ledger_and_documents_are_not_listed_over_http()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var posted = await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        await Stock.CreateAsync(x.Http, "issue", x.S.W1, (x.S.A, 1));

        AssertEmpty(await Stock.OnHandAsync(y.Http));
        AssertEmpty(await Stock.LedgerAsync(y.Http));
        AssertEmpty(await Stock.DocumentsAsync(y.Http));
        AssertEmpty(await Stock.OnHandAsync(y.Http, $"?articleId={x.S.A}"));
        AssertEmpty(await Stock.OnHandAsync(y.Http, $"?warehouseId={x.S.W1}"));
        AssertEmpty(await Stock.LedgerAsync(y.Http, $"?articleId={x.S.A}"));
        AssertEmpty(await Stock.LedgerAsync(y.Http, $"?warehouseId={x.S.W1}"));
        AssertEmpty(await Stock.LedgerAsync(y.Http, $"?documentId={posted.Id()}"));
        AssertEmpty(await Stock.DocumentsAsync(y.Http, $"?warehouseId={x.S.W1}"));
        AssertEmpty(await Stock.DocumentsAsync(y.Http, "?search=SR-000001"));
        Assert.Equal(0m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
        // X still sees its own.
        Assert.Equal(100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
        Assert.Equal(2, (await Stock.DocumentsAsync(x.Http)).Total());
    }

    [Fact]
    public async Task AC90_Another_tenants_stock_ledger_and_documents_are_not_listed_through_tools()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        var posted = await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);

        AssertEmpty(await y.Mcp.OkAsync("stock_on_hand_list"));
        AssertEmpty(await y.Mcp.OkAsync("stock_ledger_entry_list"));
        AssertEmpty(await y.Mcp.OkAsync("stock_document_list"));
        AssertEmpty(await y.Mcp.OkAsync("stock_on_hand_list", new { articleId = x.S.A, warehouseId = x.S.W1 }));
        AssertEmpty(await y.Mcp.OkAsync("stock_ledger_entry_list", new { articleId = x.S.A }));
        AssertEmpty(await y.Mcp.OkAsync("stock_ledger_entry_list", new { documentId = posted.Id() }));
        AssertEmpty(await y.Mcp.OkAsync("stock_document_list", new { warehouseId = x.S.W1 }));
        AssertEmpty(await y.Mcp.OkAsync("stock_document_list", new { search = "SR-000001" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC91_Another_tenants_document_is_not_found_for_every_operation_over_http(bool posted)
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        var draft = await Stock.CreateAsync(x.Http, "issue", x.S.W1, (x.S.A, 10));
        var target = posted ? await Stock.PostDocumentAsync(x.Http, draft.Id()) : draft;
        var stock = await Stock.OnHandAsync(x.Http);
        var ledger = await Stock.LedgerAsync(x.Http);

        using var get = await y.Http.GetAsync($"{Stock.Documents}/{target.Id()}");
        using var put = await Stock.PutAsync(y.Http, target.Id(), Stock.Replacement(y.S.W1, (y.S.A, 1)));
        using var delete = await Stock.DeleteAsync(y.Http, target.Id());
        using var post = await Stock.SendPostAsync(y.Http, target.Id());
        using var byNumber = await Stock.ByNumberAsync(y.Http, "SR-000001");

        foreach (var response in new[] { get, put, delete, post, byNumber })
            await HttpAssert.NotFoundAsync(response);
        await Stock.AssertUnchangedAsync(x.Http, target);
        McpAssert.JsonEqual(stock, await Stock.OnHandAsync(x.Http), "X's stock changed");
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(x.Http), "X's ledger changed");
        AssertEmpty(await Stock.DocumentsAsync(y.Http));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC91_Another_tenants_document_is_not_found_through_tools(bool posted)
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        var draft = await Stock.CreateAsync(x.Http, "issue", x.S.W1, (x.S.A, 10));
        var target = posted ? await Stock.PostDocumentAsync(x.Http, draft.Id()) : draft;

        await y.Mcp.ErrorAsync("stock_document_get", new { id = target.Id() }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_get", new { number = "SR-000001" }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_update", Stock.Replacement(y.S.W1, (y.S.A, 1)).WithId(target.Id()), "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_delete", new { id = target.Id() }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("stock_document_post", new { id = target.Id() }, "NOT_FOUND");

        await Stock.AssertUnchangedAsync(x.Http, target);
        Assert.Equal(posted ? 90m : 100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
    }

    [Fact]
    public async Task AC92_Another_tenants_warehouse_or_article_is_a_reference_that_does_not_exist()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        var foreignWarehouse = Stock.Draft("receipt", x.S.W1, (y.S.A, 1));
        var foreignArticle = Stock.Draft("receipt", y.S.W1, (x.S.A, 1));
        var draft = await Stock.CreateAsync(y.Http, "receipt", y.S.W1, (y.S.A, 1));

        using var httpWarehouse = await Stock.PostAsync(y.Http, foreignWarehouse);
        using var httpArticle = await Stock.PostAsync(y.Http, foreignArticle);
        using var httpReplace = await Stock.PutAsync(y.Http, draft.Id(), Stock.Replacement(x.S.W2, (y.S.A, 1)));
        using var httpRandom = await Stock.PostAsync(y.Http, Stock.Draft("receipt", Guid.NewGuid(), (y.S.A, 1)));

        var problem = await Stock.ConflictAsync(httpWarehouse, "REFERENCE_NOT_FOUND", "warehouseId");
        await Stock.ConflictAsync(httpArticle, "REFERENCE_NOT_FOUND", "lines[0].articleId");
        await Stock.ConflictAsync(httpReplace, "REFERENCE_NOT_FOUND", "warehouseId");
        // T3: exactly as a random id.
        var random = await Stock.ConflictAsync(httpRandom, "REFERENCE_NOT_FOUND", "warehouseId");
        Assert.Equal(McpAssert.ErrorKeys(random), McpAssert.ErrorKeys(problem));
        await y.Mcp.ErrorAsync("stock_document_create", foreignWarehouse, "REFERENCE_NOT_FOUND", "warehouseId");
        await y.Mcp.ErrorAsync("stock_document_create", foreignArticle, "REFERENCE_NOT_FOUND", "lines[0].articleId");
        Assert.Equal(1, (await Stock.DocumentsAsync(y.Http)).Total());
        AssertEmpty(await Stock.DocumentsAsync(x.Http));
    }

    [Fact]
    public async Task AC93_Document_numbers_are_per_tenant()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var ofX = await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 1);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 1); // X is at SR-000002

        var ofY = await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 2);

        Assert.Equal("SR-000001", ofX.Number());
        Assert.Equal("SR-000001", ofY.Number());
        Assert.NotEqual(ofX.Id(), ofY.Id());
        using var byNumberX = await Stock.ByNumberAsync(x.Http, "SR-000001");
        using var byNumberY = await Stock.ByNumberAsync(y.Http, "SR-000001");
        McpAssert.JsonEqual(ofX, await HttpAssert.JsonAsync(byNumberX, HttpStatusCode.OK));
        McpAssert.JsonEqual(ofY, await HttpAssert.JsonAsync(byNumberY, HttpStatusCode.OK));
        using var y2 = await Stock.ByNumberAsync(y.Http, "SR-000002");
        await HttpAssert.NotFoundAsync(y2);
        Assert.Equal("SR-000003", (await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 1)).Number());
        Assert.Equal("SI-000001", (await Stock.IssueAsync(y.Http, y.S.W1, y.S.A, 1)).Number());
    }

    [Fact]
    public async Task AC94_Stock_is_per_tenant()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        await Stock.ReceiveAsync(x.Http, x.S.W1, x.S.A, 100);
        var issue = await Stock.CreateAsync(y.Http, "issue", y.S.W1, (y.S.A, 1));

        using var refused = await Stock.SendPostAsync(y.Http, issue.Id());

        await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await y.Mcp.ErrorAsync("stock_document_post", new { id = issue.Id() }, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(0m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(100m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
        Assert.Equal(1, (await Stock.LedgerAsync(x.Http)).Total());
    }

    [Fact]
    public async Task AC94_T4_Parallel_postings_of_two_tenants_keep_stock_and_numbers_apart()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var posts = new List<(HttpClient Client, Guid Id)>();
        foreach (var side in new[] { x, y })
            for (var i = 0; i < 5; i++)
                posts.Add((side.Http, (await Stock.CreateAsync(side.Http, "receipt", side.S.W1, (side.S.A, side == x ? 1 : 10))).Id()));

        var responses = await Task.WhenAll(posts.Select(p => Task.Run(() => Stock.SendPostAsync(p.Client, p.Id))));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
        string[] expected = ["SR-000001", "SR-000002", "SR-000003", "SR-000004", "SR-000005"];
        foreach (var side in new[] { x, y })
        {
            var numbers = (await Stock.DocumentsAsync(side.Http)).Items().Select(d => d.Number()!).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, numbers);
        }
        Assert.Equal(5m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
        Assert.Equal(50m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
    }

    [Fact]
    public async Task AC95_One_tenants_documents_never_make_another_tenants_masters_used()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Stock.CreateAsync(y.Http, "receipt", y.S.W1, (y.S.A, 1));
        await Stock.ReceiveAsync(y.Http, y.S.W1, y.S.A, 1);
        var otherUnit = await Uom.CreateAsync(x.Http, "kg", "Kilogram");

        // X's article A is not frozen and X's A and W1 are not in use.
        var changed = await Stock.ReplaceArticleAsync(x.Http, x.S.A,
            Stock.ArticleBody(x.S.ArticleA).With("type", "service").With("baseUnitId", otherUnit.Id().ToString()));
        using var article = await x.Http.DeleteAsync($"{Art.Path}/{x.S.A}");
        using var warehouse = await MasterApi.Warehouses.DeleteAsync(x.Http, x.S.W1);

        Assert.Equal("service", changed.Str("type"));
        Assert.Equal(HttpStatusCode.NoContent, article.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, warehouse.StatusCode);
        // Y's own masters are in use.
        using var ownArticle = await y.Http.DeleteAsync($"{Art.Path}/{y.S.A}");
        using var ownWarehouse = await MasterApi.Warehouses.DeleteAsync(y.Http, y.S.W1);
        await Stock.ConflictAsync(ownArticle, "IN_USE");
        await Stock.ConflictAsync(ownWarehouse, "IN_USE");
    }
}
