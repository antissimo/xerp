using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 003, AC-10 to AC-26: API key management over HTTP. Isolation of keys: McpTenantIsolationTests (AC-95, AC-96).</summary>
[Collection(XerpCollection.Name)]
public class ApiKeyTests(XerpFixture app)
{
    private const string WhoAmI = "/api/v1/whoami";

    [Fact]
    public async Task AC10_Create_returns_the_key_once_with_provenance_and_no_store()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsJsonAsync(Keys.Path, new { name = "claude-warehouse", actorType = "agent" });

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("claude-warehouse", body.Str("name"));
        Assert.Equal("agent", body.Str("actorType"));
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.InRange(body.GetProperty("createdAt").GetDateTime().ToUniversalTime(),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(tenant.ApiKeyId, body.GetProperty("createdBy").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("revokedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("revokedBy").ValueKind);
        Assert.Matches(new Regex("^xerp_[A-Za-z0-9_-]{43}$"), body.Str("key"));
        Assert.EndsWith($"/api/v1/api-keys/{id}", response.Headers.Location?.ToString());
        Assert.True(response.Headers.TryGetValues("Cache-Control", out var cacheControl), "Cache-Control header is missing.");
        Assert.Contains("no-store", string.Join(", ", cacheControl!));
        Assert.False(body.TryGetProperty("tenantId", out _));
        Assert.False(body.TryGetProperty("keyHash", out _));
    }

    [Fact]
    public async Task AC11_Whoami_with_the_new_key_shows_the_same_tenant_and_the_new_actor()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Keys.CreateAsync(app, tenant.Client, "claude-warehouse", "agent");

        using var response = await created.Client.GetAsync(WhoAmI);

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(tenant.Id, body.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(created.Id, body.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        Assert.Equal("claude-warehouse", body.GetProperty("actor").Str("name"));
        Assert.Equal("agent", body.GetProperty("actor").Str("actorType"));
    }

    [Fact]
    public async Task AC12_Only_the_sha256_hash_of_a_created_key_is_stored()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Keys.CreateAsync(app, tenant.Client);

        var hash = await app.ScalarAsync<string>("""SELECT "KeyHash" FROM "ApiKeys" WHERE "Id" = @id""", ("id", created.Id));
        var rowsWithPlaintext = await app.ScalarAsync<long>(
            """SELECT count(*) FROM "ApiKeys" k WHERE k::text LIKE '%' || @key || '%'""", ("key", created.Key));

        Assert.Equal(XerpFixture.Sha256Hex(created.Key), hash);
        Assert.Equal(0, rowsWithPlaintext);
    }

    [Fact]
    public async Task AC13_Name_is_trimmed_and_need_not_be_unique()
    {
        var tenant = await app.NewTenantAsync();

        var trimmed = await Keys.CreateAsync(app, tenant.Client, "  bot  ", "agent");
        var second = await Keys.CreateAsync(app, tenant.Client, "bot", "agent");

        Assert.Equal("bot", trimmed.Body.Str("name"));
        Assert.Equal("bot", second.Body.Str("name"));
        Assert.NotEqual(trimmed.Id, second.Id);
        Assert.NotEqual(trimmed.Key, second.Key);
    }

    public static TheoryData<string, string> InvalidKeyBodies() => new()
    {
        { """{ "actorType": "agent" }""", "name" },
        { """{ "name": null, "actorType": "agent" }""", "name" },
        { """{ "name": "", "actorType": "agent" }""", "name" },
        { """{ "name": "   ", "actorType": "agent" }""", "name" },
        { $$"""{ "name": "{{new string('n', 101)}}", "actorType": "agent" }""", "name" },
        { """{ "name": "bot" }""", "actorType" },
        { """{ "name": "bot", "actorType": null }""", "actorType" },
        { """{ "name": "bot", "actorType": "Agent" }""", "actorType" },
        { """{ "name": "bot", "actorType": "robot" }""", "actorType" },
    };

    [Theory]
    [MemberData(nameof(InvalidKeyBodies))]
    public async Task AC14_Invalid_name_or_actorType_is_rejected_with_the_field_key(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(Keys.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Fact]
    public async Task AC14_Name_of_100_characters_is_accepted()
    {
        var tenant = await app.NewTenantAsync();
        var name = new string('n', 100);

        var created = await Keys.CreateAsync(app, tenant.Client, name, "human");

        Assert.Equal(name, created.Body.Str("name"));
    }

    [Theory]
    [InlineData("""{ "name": "bot", "actorType": "agent", "key": "xerp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }""")]
    [InlineData("""{ "name": "bot", "actorType": "agent", "isActive": false }""")]
    [InlineData("""{ "name": "bot", "actorType": "agent", "tenantId": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "name": "bot", "actorType": "agent", "foo": 1 }""")]
    [InlineData("""{ "name": "bot", """)]
    public async Task AC15_Unknown_property_or_malformed_json_is_rejected_and_creates_no_key(string json)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(Keys.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response);
        var list = await Keys.ListAsync(tenant.Client);
        Assert.Equal(1, list.Total());
        Assert.Equal(["initial"], list.Names());
    }

    [Fact]
    public async Task AC16_Get_returns_the_representation_without_the_key()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Keys.CreateAsync(app, tenant.Client, "claude-warehouse", "agent");

        var body = await Keys.GetAsync(tenant.Client, created.Id);
        var initial = await Keys.GetAsync(tenant.Client, tenant.ApiKeyId);

        Assert.Equal(
            ["actorType", "createdAt", "createdBy", "id", "isActive", "name", "revokedAt", "revokedBy"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var property in body.EnumerateObject())
            McpAssert.JsonEqual(created.Body.GetProperty(property.Name), property.Value, $"'{property.Name}' differs from the 201 body");
        Assert.DoesNotContain(created.Key, body.ToString());

        Assert.Equal("initial", initial.Str("name"));
        Assert.Equal("human", initial.Str("actorType"));
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("createdBy").ValueKind);
        Assert.False(initial.TryGetProperty("key", out _));
    }

    [Fact]
    public async Task AC17_List_is_in_creation_order_and_never_contains_keys()
    {
        var tenant = await app.NewTenantAsync();

        var fresh = await Keys.ListAsync(tenant.Client);
        await Keys.CreateAsync(app, tenant.Client, "b-bot", "agent");
        await Keys.CreateAsync(app, tenant.Client, "a-bot", "human");
        var list = await Keys.ListAsync(tenant.Client);

        Assert.Equal(["initial"], fresh.Names());
        Assert.Equal(1, fresh.Total());
        Assert.Equal(50, fresh.GetProperty("limit").GetInt32());
        Assert.Equal(0, fresh.GetProperty("offset").GetInt32());
        Assert.Equal(["initial", "b-bot", "a-bot"], list.Names());
        Assert.Equal(3, list.Total());
        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            Assert.False(item.TryGetProperty("key", out _));
            Assert.False(item.TryGetProperty("keyHash", out _));
        }
    }

    [Fact]
    public async Task AC18_List_filters_search_and_paging()
    {
        var tenant = await app.NewTenantAsync();
        await Keys.CreateAsync(app, tenant.Client, "Alpha-Bot", "agent");
        await Keys.CreateAsync(app, tenant.Client, "worker", "human");
        var revoked = await Keys.CreateAsync(app, tenant.Client, "beta-bot", "agent");
        using (var revoke = await Keys.RevokeAsync(tenant.Client, revoked.Id))
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var agents = await Keys.ListAsync(tenant.Client, "?actorType=agent");
        var inactive = await Keys.ListAsync(tenant.Client, "?isActive=false");
        var search = await Keys.ListAsync(tenant.Client, "?search=BOT");
        var page = await Keys.ListAsync(tenant.Client, "?limit=1&offset=1");

        Assert.Equal(["Alpha-Bot", "beta-bot"], agents.Names());
        Assert.Equal(2, agents.Total());
        Assert.Equal(["beta-bot"], inactive.Names());
        Assert.Equal(1, inactive.Total());
        Assert.Equal(["Alpha-Bot", "beta-bot"], search.Names());
        Assert.Equal(2, search.Total());
        Assert.Equal(["Alpha-Bot"], page.Names());
        Assert.Equal(4, page.Total());
        Assert.Equal(1, page.GetProperty("limit").GetInt32());
        Assert.Equal(1, page.GetProperty("offset").GetInt32());

        using var badActorType = await tenant.Client.GetAsync(Keys.Path + "?actorType=robot");
        using var badIsActive = await tenant.Client.GetAsync(Keys.Path + "?isActive=maybe");
        using var limitZero = await tenant.Client.GetAsync(Keys.Path + "?limit=0");
        using var limitTooBig = await tenant.Client.GetAsync(Keys.Path + "?limit=501");
        await HttpAssert.ValidationAsync(badActorType, "actorType");
        await HttpAssert.ValidationAsync(badIsActive, "isActive");
        await HttpAssert.ValidationAsync(limitZero, "limit");
        await HttpAssert.ValidationAsync(limitTooBig, "limit");
    }

    [Fact]
    public async Task AC19_Revoked_key_is_rejected_from_the_next_request_on()
    {
        var tenant = await app.NewTenantAsync();
        var a = await Keys.CreateAsync(app, tenant.Client, "a", "human");
        var b = await Keys.CreateAsync(app, tenant.Client, "b", "agent");

        using var response = await Keys.RevokeAsync(a.Client, b.Id);

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(b.Id, body.Id());
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.InRange(body.GetProperty("revokedAt").GetDateTime().ToUniversalTime(),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(a.Id, body.GetProperty("revokedBy").GetGuid());
        Assert.False(body.TryGetProperty("key", out _));

        using var whoami = await b.Client.GetAsync(WhoAmI);
        await HttpAssert.UnauthenticatedAsync(whoami);
        var after = await Keys.GetAsync(a.Client, b.Id);
        Assert.False(after.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task AC20_Revoking_twice_succeeds_and_keeps_the_first_revocation()
    {
        var tenant = await app.NewTenantAsync();
        var a = await Keys.CreateAsync(app, tenant.Client, "a", "human");
        var b = await Keys.CreateAsync(app, tenant.Client, "b", "agent");
        using var firstResponse = await Keys.RevokeAsync(a.Client, b.Id);
        var first = await HttpAssert.JsonAsync(firstResponse, HttpStatusCode.OK);

        // The second revocation is made by another key, so a changed revokedBy would show.
        using var secondResponse = await Keys.RevokeAsync(tenant.Client, b.Id);

        var second = await HttpAssert.JsonAsync(secondResponse, HttpStatusCode.OK);
        Assert.False(second.GetProperty("isActive").GetBoolean());
        Assert.Equal(first.Str("revokedAt"), second.Str("revokedAt"));
        Assert.Equal(a.Id, second.GetProperty("revokedBy").GetGuid());
    }

    [Fact]
    public async Task AC21_A_key_cannot_revoke_itself()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await Keys.RevokeAsync(tenant.Client, tenant.ApiKeyId);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "CANNOT_REVOKE_SELF");
        using var whoami = await tenant.Client.GetAsync(WhoAmI);
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        Assert.True((await Keys.GetAsync(tenant.Client, tenant.ApiKeyId)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task AC22_Records_keep_the_id_of_a_revoked_key_as_createdBy()
    {
        var tenant = await app.NewTenantAsync();
        var b = await Keys.CreateAsync(app, tenant.Client, "b", "agent");
        var unit = await Uom.CreateAsync(b.Client, "kg", "Kilogram");

        using var revoke = await Keys.RevokeAsync(tenant.Client, b.Id);

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var after = await Uom.GetAsync(tenant.Client, unit.Id());
        Assert.Equal(b.Id, after.GetProperty("createdBy").GetGuid());
    }

    [Fact]
    public async Task AC23_The_initial_key_can_be_revoked_by_a_second_key()
    {
        var tenant = await app.NewTenantAsync();
        var second = await Keys.CreateAsync(app, tenant.Client, "second", "human");

        using var response = await Keys.RevokeAsync(second.Client, tenant.ApiKeyId);

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        using var withSecond = await second.Client.GetAsync(WhoAmI);
        using var withInitial = await tenant.Client.GetAsync(WhoAmI);
        Assert.Equal(HttpStatusCode.OK, withSecond.StatusCode);
        await HttpAssert.UnauthenticatedAsync(withInitial);
    }

    [Fact]
    public async Task AC24_Unknown_or_malformed_key_id_is_not_found()
    {
        var tenant = await app.NewTenantAsync();

        using var getUnknown = await tenant.Client.GetAsync($"{Keys.Path}/{Guid.NewGuid()}");
        using var getMalformed = await tenant.Client.GetAsync($"{Keys.Path}/not-a-uuid");
        using var revokeUnknown = await Keys.RevokeAsync(tenant.Client, Guid.NewGuid());

        await HttpAssert.NotFoundAsync(getUnknown);
        await HttpAssert.NotFoundAsync(getMalformed);
        await HttpAssert.NotFoundAsync(revokeUnknown);
    }

    [Fact]
    public async Task AC25_Two_keys_revoking_each_other_in_parallel_never_both_succeed()
    {
        var tenant = await app.NewTenantAsync();

        for (var round = 0; round < 20; round++)
        {
            var a = await Keys.CreateAsync(app, tenant.Client, $"a-{round}", "agent");
            var b = await Keys.CreateAsync(app, tenant.Client, $"b-{round}", "agent");

            var responses = await Task.WhenAll(Keys.RevokeAsync(a.Client, b.Id), Keys.RevokeAsync(b.Client, a.Id));

            var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
            Assert.True(statuses is [200, 401], $"Round {round}: expected one 200 and one 401, got {string.Join(", ", statuses)}.");
            await HttpAssert.UnauthenticatedAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Unauthorized));

            using var whoamiA = await a.Client.GetAsync(WhoAmI);
            using var whoamiB = await b.Client.GetAsync(WhoAmI);
            var after = new[] { (int)whoamiA.StatusCode, (int)whoamiB.StatusCode }.Order().ToArray();
            Assert.True(after is [200, 401], $"Round {round}: exactly one of A and B must still authenticate, got {string.Join(", ", after)}.");
        }
    }

    [Fact]
    public async Task AC26_Key_routes_need_a_tenant_key()
    {
        var id = Guid.NewGuid();
        using var anonymous = app.Anonymous();
        using var admin = app.Admin();

        using var listAnonymous = await anonymous.GetAsync(Keys.Path);
        using var createAnonymous = await anonymous.PostAsJsonAsync(Keys.Path, new { name = "bot", actorType = "agent" });
        using var revokeAnonymous = await Keys.RevokeAsync(anonymous, id);
        using var listAdmin = await admin.GetAsync(Keys.Path);
        using var createAdmin = await admin.PostAsJsonAsync(Keys.Path, new { name = "bot", actorType = "agent" });
        using var revokeAdmin = await Keys.RevokeAsync(admin, id);

        await HttpAssert.UnauthenticatedAsync(listAnonymous);
        await HttpAssert.UnauthenticatedAsync(createAnonymous);
        await HttpAssert.UnauthenticatedAsync(revokeAnonymous);
        await HttpAssert.ForbiddenAsync(listAdmin);
        await HttpAssert.ForbiddenAsync(createAdmin);
        await HttpAssert.ForbiddenAsync(revokeAdmin);
    }
}
