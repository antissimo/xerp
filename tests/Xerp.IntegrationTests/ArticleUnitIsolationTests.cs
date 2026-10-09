using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 007, AC-90 to AC-92: tenant isolation of conversions and of line units. Tenants X and Y each have this
/// spec's setup, so every code (<c>A</c>, <c>box</c>, <c>pack</c>) exists in both.
/// </summary>
[Collection(XerpCollection.Name)]
public class ArticleUnitIsolationTests(XerpFixture app)
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
    public async Task AC90_Another_tenants_conversions_are_not_found_over_http()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var before = await Units.GetAsync(x.Http, x.S.A, x.S.Box);

        using var list = await y.Http.GetAsync(Units.Path(x.S.A));
        using var get = await Units.SendGetAsync(y.Http, x.S.A, x.S.Box);
        using var put = await Units.PutAsync(y.Http, x.S.A, x.S.Box, 99);
        using var delete = await Units.DeleteAsync(y.Http, x.S.A, x.S.Box);
        // X's article with Y's own unit is still X's article.
        using var putOwnUnit = await Units.PutAsync(y.Http, x.S.A, y.S.Pack, 99);
        using var getOwnUnit = await Units.SendGetAsync(y.Http, x.S.A, y.S.Box);
        // Exactly as a random article id.
        using var random = await Units.PutAsync(y.Http, Guid.NewGuid(), y.S.Pack, 99);

        await HttpAssert.NotFoundAsync(list);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(put);
        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.NotFoundAsync(putOwnUnit);
        await HttpAssert.NotFoundAsync(getOwnUnit);
        await HttpAssert.NotFoundAsync(random);
        McpAssert.JsonEqual(before, await Units.GetAsync(x.Http, x.S.A, x.S.Box), "X's conversion changed");
        Assert.Equal(new[] { "box" }, (await Units.ListAsync(x.Http, x.S.A)).UnitCodes());
        Assert.Equal(new[] { "box" }, (await Units.ListAsync(y.Http, y.S.A)).UnitCodes());
    }

    [Fact]
    public async Task AC90_Another_tenants_conversions_are_not_found_through_tools()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        var before = await Units.GetAsync(x.Http, x.S.A, x.S.Box);
        var address = new { articleId = x.S.A, unitId = x.S.Box };

        await y.Mcp.ErrorAsync("article_unit_list", new { articleId = x.S.A }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("article_unit_get", address, "NOT_FOUND");
        await y.Mcp.ErrorAsync("article_unit_set", new { articleId = x.S.A, unitId = x.S.Box, factor = 99 }, "NOT_FOUND");
        await y.Mcp.ErrorAsync("article_unit_delete", address, "NOT_FOUND");

        McpAssert.JsonEqual(before, await Units.GetAsync(x.Http, x.S.A, x.S.Box), "X's conversion changed");
    }

    [Fact]
    public async Task AC91_Another_tenants_unit_is_a_reference_that_does_not_exist()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync(mcp: true);
        var draft = await Units.CreateAsync(y.Http, "receipt", y.S.W1, (y.S.A, 1, null));

        using var set = await Units.PutAsync(y.Http, y.S.A, x.S.Pack, 6);
        using var randomSet = await Units.PutAsync(y.Http, y.S.A, Guid.NewGuid(), 6);
        using var line = await Stock.PostAsync(y.Http, Units.Draft("receipt", y.S.W1, (y.S.A, 1, x.S.Box)));
        using var replace = await Stock.PutAsync(y.Http, draft.Id(), Units.Replacement(y.S.W1, (y.S.A, 1, x.S.Box)));
        // X's base unit is not Y's base unit either.
        using var xBase = await Stock.PostAsync(y.Http, Units.Draft("receipt", y.S.W1, (y.S.A, 1, x.S.Pcs)));
        using var get = await Units.SendGetAsync(y.Http, y.S.A, x.S.Box);
        using var delete = await Units.DeleteAsync(y.Http, y.S.A, x.S.Box);
        var filtered = await Art.ListAsync(y.Http, $"?alternativeUnitId={x.S.Box}");

        var foreign = await HttpAssert.ReferenceNotFoundAsync(set, "unitId");
        var unknown = await HttpAssert.ReferenceNotFoundAsync(randomSet, "unitId");
        Assert.Equal(McpAssert.ErrorKeys(unknown), McpAssert.ErrorKeys(foreign));
        await Stock.ConflictAsync(line, "REFERENCE_NOT_FOUND", "lines[0].unitId");
        await Stock.ConflictAsync(replace, "REFERENCE_NOT_FOUND", "lines[0].unitId");
        await Stock.ConflictAsync(xBase, "REFERENCE_NOT_FOUND", "lines[0].unitId");
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(delete);
        Assert.Equal(0, filtered.Total());
        Assert.Empty(filtered.Codes());
        // The same through tools.
        await y.Mcp.ErrorAsync("article_unit_set", new { articleId = y.S.A, unitId = x.S.Pack, factor = 6 }, "REFERENCE_NOT_FOUND", "unitId");
        await y.Mcp.ErrorAsync("stock_document_create", Units.Draft("receipt", y.S.W1, (y.S.A, 1, x.S.Box)), "REFERENCE_NOT_FOUND", "lines[0].unitId");
        Assert.Equal(0, (await y.Mcp.OkAsync("article_list", new { alternativeUnitId = x.S.Box })).Total());

        Assert.Equal(new[] { "box" }, (await Units.ListAsync(y.Http, y.S.A)).UnitCodes());
        Assert.Equal(1, (await Stock.DocumentsAsync(y.Http)).Total());
        await Stock.AssertUnchangedAsync(y.Http, draft);
        // X's units are not used by anything Y did.
        using var deletePack = await Units.DeleteUnitAsync(x.Http, x.S.Pack);
        Assert.Equal(HttpStatusCode.NoContent, deletePack.StatusCode);
    }

    [Fact]
    public async Task AC92_Each_tenant_converts_with_its_own_factor()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Units.SetAsync(y.Http, y.S.A, y.S.Box, 50);

        var inY = await Units.PostedAsync(y.Http, "receipt", y.S.W1, (y.S.A, 1, y.S.Box));
        var inX = await Units.PostedAsync(x.Http, "receipt", x.S.W1, (x.S.A, 1, x.S.Box));

        Assert.Equal(12m, (await Units.GetAsync(x.Http, x.S.A, x.S.Box)).Factor());
        Units.AssertLine(inY.DocumentLines()[0], "box", 1m, 50m, 50m);
        Units.AssertLine(inX.DocumentLines()[0], "box", 1m, 12m, 12m);
        Assert.Equal(50m, Assert.Single(await Stock.EntriesAsync(y.Http, inY.Id())).Quantity());
        Assert.Equal(12m, Assert.Single(await Stock.EntriesAsync(x.Http, inX.Id())).Quantity());
        Assert.Equal(50m, await Stock.QuantityAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(12m, await Stock.QuantityAsync(x.Http, x.S.A, x.S.W1));
    }

    [Fact]
    public async Task AC92_T3_One_tenants_conversions_and_lines_never_freeze_another_tenants_masters()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await Units.PostedAsync(x.Http, "receipt", x.S.W1, (x.S.A, 1, x.S.Box));
        await Units.CreateAsync(x.Http, "receipt", x.S.W1, (x.S.A, 1, x.S.Box));

        // Y's A has no stock documents: Y can drop its conversion, change the base unit and delete the unit.
        using var deleteConversion = await Units.DeleteAsync(y.Http, y.S.A, y.S.Box);
        Assert.Equal(HttpStatusCode.NoContent, deleteConversion.StatusCode);
        var changed = await Stock.ReplaceArticleAsync(y.Http, y.S.A, Stock.ArticleBody(y.S.S.ArticleA).With("baseUnitId", y.S.Pack.ToString()));
        Assert.Equal(y.S.Pack, changed.GetProperty("baseUnit").Id());
        using var deleteUnit = await Units.DeleteUnitAsync(y.Http, y.S.Box);
        Assert.Equal(HttpStatusCode.NoContent, deleteUnit.StatusCode);

        // X's are still frozen.
        using var xConversion = await Units.DeleteAsync(x.Http, x.S.A, x.S.Box);
        using var xUnit = await Units.DeleteUnitAsync(x.Http, x.S.Box);
        await HttpAssert.InUseAsync(xConversion);
        await HttpAssert.InUseAsync(xUnit);
    }
}
