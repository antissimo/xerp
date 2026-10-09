using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 003, AC-50 to AC-61: tools succeed and return exactly what the HTTP endpoint returns ("equal to HTTP").
/// The MCP client uses its own <c>agent</c> key, so attribution to the MCP key is distinguishable from the
/// tenant's initial key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpToolTests(XerpFixture app)
{
    private sealed record Session(TestTenant Tenant, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the same key as the MCP client.</summary>
        public HttpClient Http => Agent.Client;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var tenant = await app.NewTenantAsync();
        var agent = await Keys.CreateAsync(app, tenant.Client, "claude-warehouse", "agent");
        return new Session(tenant, agent, await app.McpAsync(agent.Key));
    }

    private static async Task<JsonElement> HttpGetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    [Fact]
    public async Task AC50_Whoami_equals_http_and_shows_the_agent()
    {
        await using var s = await AgentAsync();

        var result = await s.Mcp.OkAsync("whoami");

        McpAssert.JsonEqual(await HttpGetAsync(s.Http, "/api/v1/whoami"), result);
        Assert.Equal("agent", result.GetProperty("actor").Str("actorType"));
        Assert.Equal(s.Agent.Id, result.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        Assert.Equal(s.Tenant.Id, result.GetProperty("tenant").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AC50_A_human_key_may_use_mcp_as_well()
    {
        var tenant = await app.NewTenantAsync();
        await using var mcp = await app.McpAsync(tenant.Key);

        var result = await mcp.OkAsync("whoami");

        McpAssert.JsonEqual(await HttpGetAsync(tenant.Client, "/api/v1/whoami"), result);
        Assert.Equal("human", result.GetProperty("actor").Str("actorType"));
    }

    [Fact]
    public async Task AC51_Uom_create_is_attributed_to_the_mcp_key_and_equals_http_get()
    {
        await using var s = await AgentAsync();

        var result = await s.Mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram" });

        var id = result.GetProperty("id").GetGuid();
        Assert.Equal("kg", result.Str("code"));
        Assert.Equal("Kilogram", result.Str("name"));
        Assert.True(result.GetProperty("isActive").GetBoolean());
        Assert.Equal(s.Agent.Id, result.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await Uom.GetAsync(s.Http, id), result);
    }

    [Fact]
    public async Task AC52_Uom_get_by_id_and_by_code_in_any_case_equals_http_get()
    {
        await using var s = await AgentAsync();
        var unit = await Uom.CreateAsync(s.Http, "kg", "Kilogram");
        var http = await Uom.GetAsync(s.Http, unit.Id());

        var byId = await s.Mcp.OkAsync("uom_get", new { id = unit.Id() });
        var byCode = await s.Mcp.OkAsync("uom_get", new { code = "kg" });
        var byUpperCode = await s.Mcp.OkAsync("uom_get", new { code = "KG" });

        McpAssert.JsonEqual(http, byId);
        McpAssert.JsonEqual(http, byCode);
        McpAssert.JsonEqual(http, byUpperCode);
        Assert.Equal("kg", byUpperCode.Str("code"));
    }

    [Fact]
    public async Task AC53_Uom_list_equals_http_with_and_without_arguments()
    {
        await using var s = await AgentAsync();
        await Uom.CreateAsync(s.Http, "kg", "Kilogram");
        await Uom.CreateAsync(s.Http, "g", "Gram");
        await Uom.CreateAsync(s.Http, "mg", "Milligram");
        await Uom.CreateAsync(s.Http, "l", "Litre");
        await Uom.CreateAsync(s.Http, "dag", "Decagram", isActive: false);

        var all = await s.Mcp.OkAsync("uom_list", new { });
        var filtered = await s.Mcp.OkAsync("uom_list", new { search = "gram", isActive = true, limit = 1, offset = 1 });

        McpAssert.JsonEqual(await Uom.ListAsync(s.Http), all);
        Assert.Equal(5, all.Total());
        McpAssert.JsonEqual(await Uom.ListAsync(s.Http, "?search=gram&isActive=true&limit=1&offset=1"), filtered);
        Assert.Equal(3, filtered.Total());
        Assert.Single(filtered.Codes());
        Assert.Equal(1, filtered.GetProperty("limit").GetInt32());
        Assert.Equal(1, filtered.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task AC54_Uom_update_replaces_the_unit_and_is_attributed_to_the_mcp_key()
    {
        await using var s = await AgentAsync();
        var unit = await Uom.CreateAsync(s.Tenant.Client, "kg", "Kilogram");

        var result = await s.Mcp.OkAsync("uom_update", new { id = unit.Id(), code = "kgm", name = "Kilogramme", isActive = false });

        Assert.Equal(unit.Id(), result.Id());
        Assert.Equal("kgm", result.Str("code"));
        Assert.Equal("Kilogramme", result.Str("name"));
        Assert.False(result.GetProperty("isActive").GetBoolean());
        Assert.Equal(s.Agent.Id, result.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(await Uom.GetAsync(s.Http, unit.Id()), result);
    }

    [Fact]
    public async Task AC55_Uom_delete_returns_deleted_true_and_removes_the_unit()
    {
        await using var s = await AgentAsync();
        var unit = await Uom.CreateAsync(s.Http, "kg", "Kilogram");

        var result = await s.Mcp.OkAsync("uom_delete", new { id = unit.Id() });

        McpAssert.JsonEqual(JsonSerializer.SerializeToElement(new { deleted = true }), result);
        using var get = await s.Http.GetAsync($"{Uom.Path}/{unit.Id()}");
        await HttpAssert.NotFoundAsync(get);
    }

    [Fact]
    public async Task AC56_Article_create_embeds_the_base_unit_and_equals_http_get()
    {
        await using var s = await AgentAsync();
        var unit = await Uom.CreateAsync(s.Http, "pcs", "Piece");

        var result = await s.Mcp.OkAsync("article_create",
            new { code = "ART-001", name = "Steel bolt M8", type = "stock", baseUnitId = unit.Id() });

        var id = result.GetProperty("id").GetGuid();
        Assert.Equal("ART-001", result.Str("code"));
        Assert.Equal("stock", result.Str("type"));
        Assert.Equal(unit.Id(), result.GetProperty("baseUnit").Id());
        Assert.Equal("pcs", result.GetProperty("baseUnit").Str("code"));
        Assert.Equal("Piece", result.GetProperty("baseUnit").Str("name"));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("description").ValueKind);
        Assert.Equal(s.Agent.Id, result.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await Art.GetAsync(s.Http, id), result);
    }

    [Fact]
    public async Task AC57_Article_get_and_list_equal_http()
    {
        await using var s = await AgentAsync();
        var pcs = await Uom.CreateAsync(s.Http, "pcs", "Piece");
        var hour = await Uom.CreateAsync(s.Http, "h", "Hour");
        var bolt = await Art.CreateAsync(s.Http, "B-8", "Steel bolt", pcs.Id());
        await Art.CreateAsync(s.Http, "N-8", "Steel nut", pcs.Id());
        await Art.CreateAsync(s.Http, "S-1", "Bolt fitting", hour.Id(), "service");
        var http = await Art.GetAsync(s.Http, bolt.Id());

        var byId = await s.Mcp.OkAsync("article_get", new { id = bolt.Id() });
        var byCode = await s.Mcp.OkAsync("article_get", new { code = "b-8" });
        var all = await s.Mcp.OkAsync("article_list", new { });
        var filtered = await s.Mcp.OkAsync("article_list",
            new { type = "stock", baseUnitId = pcs.Id(), isActive = true, search = "bolt" });

        McpAssert.JsonEqual(http, byId);
        McpAssert.JsonEqual(http, byCode);
        McpAssert.JsonEqual(await Art.ListAsync(s.Http), all);
        Assert.Equal(3, all.Total());
        McpAssert.JsonEqual(
            await Art.ListAsync(s.Http, $"?type=stock&baseUnitId={pcs.Id()}&isActive=true&search=bolt"), filtered);
        Assert.Equal(["B-8"], filtered.Codes());
    }

    [Fact]
    public async Task AC58_Article_update_with_null_description_and_article_delete()
    {
        await using var s = await AgentAsync();
        var pcs = await Uom.CreateAsync(s.Http, "pcs", "Piece");
        var box = await Uom.CreateAsync(s.Http, "box", "Box");
        using var created = await s.Http.PostAsJsonAsync(Art.Path,
            new { code = "ART-001", name = "Bolt", type = "stock", baseUnitId = pcs.Id(), description = "zinc" });
        var article = await HttpAssert.JsonAsync(created, HttpStatusCode.Created);

        var updated = await s.Mcp.OkAsync("article_update", new
        {
            id = article.Id(), code = "ART-002", name = "Bolt M10", description = (string?)null,
            type = "service", baseUnitId = box.Id(), isActive = false,
        });

        Assert.Equal(JsonValueKind.Null, updated.GetProperty("description").ValueKind);
        Assert.Equal("ART-002", updated.Str("code"));
        Assert.Equal("Bolt M10", updated.Str("name"));
        Assert.Equal("service", updated.Str("type"));
        Assert.Equal(box.Id(), updated.GetProperty("baseUnit").Id());
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        McpAssert.JsonEqual(await Art.GetAsync(s.Http, article.Id()), updated);

        var deleted = await s.Mcp.OkAsync("article_delete", new { id = article.Id() });

        McpAssert.JsonEqual(JsonSerializer.SerializeToElement(new { deleted = true }), deleted);
        using var get = await s.Http.GetAsync($"{Art.Path}/{article.Id()}");
        await HttpAssert.NotFoundAsync(get);
    }

    [Fact]
    public async Task AC59_Api_key_list_and_get_equal_http_and_never_contain_keys()
    {
        await using var s = await AgentAsync();
        var other = await Keys.CreateAsync(app, s.Tenant.Client, "other-bot", "agent");
        var revoked = await Keys.CreateAsync(app, s.Tenant.Client, "old-bot", "agent");
        using (var revoke = await Keys.RevokeAsync(s.Tenant.Client, revoked.Id))
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var all = await s.Mcp.OkAsync("api_key_list", new { });
        var filtered = await s.Mcp.OkAsync("api_key_list", new { actorType = "agent", isActive = true });
        var one = await s.Mcp.OkAsync("api_key_get", new { id = other.Id });

        McpAssert.JsonEqual(await Keys.ListAsync(s.Http), all);
        Assert.Equal(["initial", "claude-warehouse", "other-bot", "old-bot"], all.Names());
        McpAssert.JsonEqual(await Keys.ListAsync(s.Http, "?actorType=agent&isActive=true"), filtered);
        Assert.Equal(["claude-warehouse", "other-bot"], filtered.Names());
        McpAssert.JsonEqual(await Keys.GetAsync(s.Http, other.Id), one);
        foreach (var item in all.GetProperty("items").EnumerateArray().Append(one))
        {
            Assert.False(item.TryGetProperty("key", out _));
            Assert.False(item.TryGetProperty("keyHash", out _));
        }
        // S3: no plaintext key in any tool result.
        foreach (var key in new[] { s.Tenant.Key, s.Agent.Key, other.Key, revoked.Key })
            Assert.DoesNotContain(key, all.ToString() + filtered + one);
    }

    [Fact]
    public async Task AC60_Api_key_revoke_through_a_tool_disables_the_other_key()
    {
        await using var s = await AgentAsync();
        var other = await Keys.CreateAsync(app, s.Tenant.Client, "other-bot", "agent");

        var result = await s.Mcp.OkAsync("api_key_revoke", new { id = other.Id });

        Assert.Equal(other.Id, result.Id());
        Assert.False(result.GetProperty("isActive").GetBoolean());
        Assert.Equal(s.Agent.Id, result.GetProperty("revokedBy").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("revokedAt").ValueKind);
        using var whoami = await other.Client.GetAsync("/api/v1/whoami");
        await HttpAssert.UnauthenticatedAsync(whoami);
        McpAssert.JsonEqual(await Keys.GetAsync(s.Http, other.Id), result);
    }

    [Fact]
    public async Task AC61_Created_over_http_and_updated_through_a_tool_keeps_both_actors()
    {
        await using var s = await AgentAsync();
        var unit = await Uom.CreateAsync(s.Tenant.Client, "kg", "Kilogram");

        await s.Mcp.OkAsync("uom_update", new { id = unit.Id(), code = "kg", name = "Kilo", isActive = true });

        var after = await Uom.GetAsync(s.Tenant.Client, unit.Id());
        Assert.Equal("Kilo", after.Str("name"));
        Assert.Equal(s.Tenant.ApiKeyId, after.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Agent.Id, after.GetProperty("updatedBy").GetGuid());
    }
}
