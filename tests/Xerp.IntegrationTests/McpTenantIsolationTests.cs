using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 003, AC-90 to AC-97: tenants A and B, each with its own key and its own MCP client. Through tools and
/// through the API key endpoints, records of the other tenant do not exist.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpTenantIsolationTests(XerpFixture app)
{
    private sealed record Side(TestTenant Tenant, McpConnection Mcp);

    private sealed record World(Side A, Side B, Guid UnitId, Guid ArticleId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await A.Mcp.DisposeAsync();
            await B.Mcp.DisposeAsync();
        }
    }

    private async Task<Side> SideAsync()
    {
        var tenant = await app.NewTenantAsync();
        return new Side(tenant, await app.McpAsync(tenant.Key));
    }

    /// <summary>A creates unit <c>kg</c> and article <c>X1</c> through tools; B has nothing.</summary>
    private async Task<World> WorldAsync()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        var unit = await a.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram" });
        var article = await a.Mcp.OkAsync("article_create",
            new { code = "X1", name = "Article of A", type = "stock", baseUnitId = unit.Id() });
        return new World(a, b, unit.Id(), article.Id());
    }

    [Fact]
    public async Task AC90_Records_of_A_are_not_listed_or_counted_for_B()
    {
        await using var w = await WorldAsync();

        var units = await w.B.Mcp.OkAsync("uom_list", new { });
        var articles = await w.B.Mcp.OkAsync("article_list", new { });
        var whoami = await w.B.Mcp.OkAsync("whoami");

        Assert.Empty(units.Codes());
        Assert.Equal(0, units.Total());
        Assert.Empty(articles.Codes());
        Assert.Equal(0, articles.Total());
        Assert.Equal(w.B.Tenant.Id, whoami.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(w.B.Tenant.Code, whoami.GetProperty("tenant").Str("code"));
    }

    [Fact]
    public async Task AC90_Units_of_A_are_not_listed_for_B()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        await using var _a = a.Mcp;
        await using var _b = b.Mcp;
        await a.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram" });

        var units = await b.Mcp.OkAsync("uom_list", new { });
        var search = await b.Mcp.OkAsync("uom_list", new { search = "kg" });

        Assert.Equal(0, units.Total());
        Assert.Empty(units.Codes());
        Assert.Equal(0, search.Total());
        Assert.Equal(["kg"], (await a.Mcp.OkAsync("uom_list", new { })).Codes());
    }

    [Fact]
    public async Task AC91_Units_of_A_are_not_found_by_B_and_stay_unchanged()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        await using var _a = a.Mcp;
        await using var _b = b.Mcp;
        var unit = await a.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram" });

        await b.Mcp.ErrorAsync("uom_get", new { id = unit.Id() }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("uom_get", new { code = "kg" }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("uom_update", new { id = unit.Id(), code = "hacked", name = "Hacked", isActive = false }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("uom_delete", new { id = unit.Id() }, "NOT_FOUND");

        McpAssert.JsonEqual(unit, await a.Mcp.OkAsync("uom_get", new { id = unit.Id() }));
    }

    [Fact]
    public async Task AC91_Articles_of_A_are_not_found_by_B_and_stay_unchanged()
    {
        await using var w = await WorldAsync();
        var bUnit = await w.B.Mcp.OkAsync("uom_create", new { code = "pcs", name = "Piece" });
        var before = await w.A.Mcp.OkAsync("article_get", new { id = w.ArticleId });

        await w.B.Mcp.ErrorAsync("article_get", new { id = w.ArticleId }, "NOT_FOUND");
        await w.B.Mcp.ErrorAsync("article_get", new { code = "X1" }, "NOT_FOUND");
        await w.B.Mcp.ErrorAsync("article_update", new
        {
            id = w.ArticleId, code = "hacked", name = "Hacked", description = (string?)null,
            type = "service", baseUnitId = bUnit.Id(), isActive = false,
        }, "NOT_FOUND");
        await w.B.Mcp.ErrorAsync("article_delete", new { id = w.ArticleId }, "NOT_FOUND");
        await w.B.Mcp.ErrorAsync("uom_get", new { id = w.UnitId }, "NOT_FOUND");
        await w.B.Mcp.ErrorAsync("uom_delete", new { id = w.UnitId }, "NOT_FOUND");

        McpAssert.JsonEqual(before, await w.A.Mcp.OkAsync("article_get", new { id = w.ArticleId }));
        Assert.Equal("kg", (await w.A.Mcp.OkAsync("uom_get", new { id = w.UnitId })).Str("code"));
    }

    [Fact]
    public async Task AC92_A_unit_of_A_does_not_exist_as_a_reference_for_B()
    {
        await using var w = await WorldAsync();

        await w.B.Mcp.ErrorAsync("article_create",
            new { code = "Y1", name = "Article of B", type = "stock", baseUnitId = w.UnitId }, "REFERENCE_NOT_FOUND", "baseUnitId");
        var filtered = await w.B.Mcp.OkAsync("article_list", new { baseUnitId = w.UnitId });

        Assert.Empty(filtered.Codes());
        Assert.Equal(0, filtered.Total());
        Assert.Equal(0, (await w.B.Mcp.OkAsync("article_list", new { })).Total());
    }

    [Fact]
    public async Task AC93_Codes_do_not_collide_across_tenants()
    {
        await using var w = await WorldAsync();

        var unit = await w.B.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram of B" });
        var article = await w.B.Mcp.OkAsync("article_create",
            new { code = "X1", name = "Article of B", type = "stock", baseUnitId = unit.Id() });

        Assert.NotEqual(w.UnitId, unit.Id());
        Assert.NotEqual(w.ArticleId, article.Id());
        Assert.Equal(unit.Id(), article.GetProperty("baseUnit").Id());
        Assert.Equal("Article of A", (await w.A.Mcp.OkAsync("article_get", new { code = "X1" })).Str("name"));
    }

    [Fact]
    public async Task AC93_Unit_codes_do_not_collide_across_tenants()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        await using var _a = a.Mcp;
        await using var _b = b.Mcp;
        var unitA = await a.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram" });

        var unitB = await b.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram of B" });

        Assert.NotEqual(unitA.Id(), unitB.Id());
        Assert.Equal("Kilogram", (await a.Mcp.OkAsync("uom_get", new { code = "kg" })).Str("name"));
        Assert.Equal("Kilogram of B", (await b.Mcp.OkAsync("uom_get", new { code = "kg" })).Str("name"));
    }

    [Fact]
    public async Task AC94_A_tenantId_argument_is_an_unknown_argument_and_creates_nothing()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        await using var _a = a.Mcp;
        await using var _b = b.Mcp;

        await b.Mcp.ErrorAsync("uom_create", new { code = "kg", name = "Kilogram", tenantId = a.Tenant.Id }, "VALIDATION_FAILED");

        Assert.Equal(0, (await a.Mcp.OkAsync("uom_list", new { })).Total());
        Assert.Equal(0, (await b.Mcp.OkAsync("uom_list", new { })).Total());
    }

    [Fact]
    public async Task AC95_Keys_of_A_do_not_exist_for_B_over_http()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var aBot = await Keys.CreateAsync(app, a.Client, "a-bot", "agent");

        var list = await Keys.ListAsync(b.Client);
        using var get = await b.Client.GetAsync($"{Keys.Path}/{a.ApiKeyId}");
        using var getBot = await b.Client.GetAsync($"{Keys.Path}/{aBot.Id}");
        using var revoke = await Keys.RevokeAsync(b.Client, a.ApiKeyId);
        using var revokeBot = await Keys.RevokeAsync(b.Client, aBot.Id);

        Assert.Equal([b.ApiKeyId], list.Ids());
        Assert.Equal(1, list.Total());
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(getBot);
        await HttpAssert.NotFoundAsync(revoke);
        await HttpAssert.NotFoundAsync(revokeBot);
        using var whoamiA = await a.Client.GetAsync("/api/v1/whoami");
        using var whoamiBot = await aBot.Client.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.OK, whoamiA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, whoamiBot.StatusCode);
        Assert.True((await Keys.GetAsync(a.Client, aBot.Id)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task AC95_Keys_of_A_do_not_exist_for_B_through_tools()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        await using var _a = a.Mcp;
        await using var _b = b.Mcp;

        var list = await b.Mcp.OkAsync("api_key_list", new { });
        await b.Mcp.ErrorAsync("api_key_get", new { id = a.Tenant.ApiKeyId }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("api_key_revoke", new { id = a.Tenant.ApiKeyId }, "NOT_FOUND");

        Assert.Equal([b.Tenant.ApiKeyId], list.Ids());
        Assert.Equal(1, list.Total());
        var whoami = await a.Mcp.OkAsync("whoami");
        Assert.Equal(a.Tenant.Id, whoami.GetProperty("tenant").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AC96_A_key_created_by_B_belongs_to_B()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();

        var created = await Keys.CreateAsync(app, b.Client, "b-bot", "agent");

        using var response = await created.Client.GetAsync("/api/v1/whoami");
        var whoami = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(b.Id, whoami.GetProperty("tenant").GetProperty("id").GetGuid());
        var listA = await Keys.ListAsync(a.Client);
        Assert.Equal([a.ApiKeyId], listA.Ids());
        Assert.Equal(1, listA.Total());
        Assert.Equal([b.ApiKeyId, created.Id], (await Keys.ListAsync(b.Client)).Ids());
    }

    [Fact]
    public async Task AC97_Interleaved_parallel_calls_of_two_tenants_never_mix_data()
    {
        var a = await SideAsync();
        var b = await SideAsync();
        await using var _a = a.Mcp;
        await using var _b = b.Mcp;

        // Per tenant: 10 uom_create and 10 uom_list, all 40 calls in flight together.
        async Task<string[][]> RunAsync(McpConnection mcp, string prefix)
        {
            var creates = Enumerable.Range(0, 10)
                .Select(i => mcp.OkAsync("uom_create", new { code = $"{prefix}-{i}", name = $"Unit {prefix} {i}" }))
                .ToArray();
            var lists = Enumerable.Range(0, 10).Select(_ => mcp.OkAsync("uom_list", new { })).ToArray();
            await Task.WhenAll(creates);
            return (await Task.WhenAll(lists)).Select(list => list.Codes()).ToArray();
        }

        var runA = RunAsync(a.Mcp, "a");
        var runB = RunAsync(b.Mcp, "b");
        var listsA = await runA;
        var listsB = await runB;

        Assert.All(listsA, codes => Assert.All(codes, code => Assert.StartsWith("a-", code)));
        Assert.All(listsB, codes => Assert.All(codes, code => Assert.StartsWith("b-", code)));
        var finalA = await a.Mcp.OkAsync("uom_list", new { });
        var finalB = await b.Mcp.OkAsync("uom_list", new { });
        Assert.Equal(10, finalA.Total());
        Assert.Equal(10, finalB.Total());
        Assert.All(finalA.Codes(), code => Assert.StartsWith("a-", code));
        Assert.All(finalB.Codes(), code => Assert.StartsWith("b-", code));
    }
}
