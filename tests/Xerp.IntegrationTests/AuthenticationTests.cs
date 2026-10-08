using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

[Collection(XerpCollection.Name)]
public class AuthenticationTests(XerpFixture app)
{
    private const string Tenants = "/api/v1/admin/tenants";
    private const string WhoAmI = "/api/v1/whoami";

    private static object NewTenantBody() => new { code = XerpFixture.UniqueCode(), name = "Acme" };

    [Fact]
    public async Task AC10_Health_needs_no_credentials()
    {
        using var response = await app.Anonymous().GetAsync("/health");

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("ok", body.Str("status"));
        Assert.Equal("ok", body.Str("db"));
    }

    [Fact]
    public async Task AC11_Admin_creates_tenant_and_receives_first_key()
    {
        var code = XerpFixture.UniqueCode();
        var before = DateTime.UtcNow.AddMinutes(-1);

        using var response = await app.Admin().PostAsJsonAsync(Tenants, new { code, name = "Acme" });

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        var tenant = body.GetProperty("tenant");
        Assert.Equal(7, tenant.GetProperty("id").GetGuid().Version);
        Assert.Equal(code, tenant.Str("code"));
        Assert.Equal("Acme", tenant.Str("name"));
        Assert.True(tenant.GetProperty("isActive").GetBoolean());
        Assert.EndsWith("Z", tenant.Str("createdAt"));
        Assert.InRange(tenant.GetProperty("createdAt").GetDateTime().ToUniversalTime(), before, DateTime.UtcNow.AddMinutes(1));
        var apiKey = body.GetProperty("apiKey");
        Assert.Equal(7, apiKey.GetProperty("id").GetGuid().Version);
        Assert.Equal("initial", apiKey.Str("name"));
        Assert.Equal("human", apiKey.Str("actorType"));
        Assert.Matches(new Regex("^xerp_[A-Za-z0-9_-]{43}$"), apiKey.Str("key"));
        Assert.Equal(["apiKey", "tenant"], body.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task AC11_Tenant_code_and_name_are_trimmed_and_limits_are_inclusive()
    {
        var code = XerpFixture.UniqueCode().PadRight(50, 'x');
        var name = new string('n', 200);

        using var response = await app.Admin().PostAsJsonAsync(Tenants, new { code = $"  {code}  ", name = $"  {name}  " });

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(code, body.GetProperty("tenant").Str("code"));
        Assert.Equal(name, body.GetProperty("tenant").Str("name"));
    }

    [Fact]
    public async Task AC12_Two_tenants_receive_different_keys()
    {
        var first = await app.NewTenantAsync();
        var second = await app.NewTenantAsync();

        Assert.NotEqual(first.Key, second.Key);
        Assert.NotEqual(first.ApiKeyId, second.ApiKeyId);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task AC13_Whoami_returns_the_tenant_and_actor_of_the_key()
    {
        var tenant = await app.NewTenantAsync("Acme d.o.o.");

        using var response = await tenant.Client.GetAsync(WhoAmI);

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(tenant.Id, body.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(tenant.Code, body.GetProperty("tenant").Str("code"));
        Assert.Equal("Acme d.o.o.", body.GetProperty("tenant").Str("name"));
        Assert.Equal(tenant.ApiKeyId, body.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        Assert.Equal("initial", body.GetProperty("actor").Str("name"));
        Assert.Equal("human", body.GetProperty("actor").Str("actorType"));
        Assert.DoesNotContain(tenant.Key, body.ToString());
    }

    [Fact]
    public async Task AC14_Tenant_code_differing_only_by_case_is_taken()
    {
        var code = XerpFixture.UniqueCode("acme");
        using var first = await app.Admin().PostAsJsonAsync(Tenants, new { code, name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var upper = await app.Admin().PostAsJsonAsync(Tenants, new { code = code.ToUpperInvariant(), name = "Other" });
        using var same = await app.Admin().PostAsJsonAsync(Tenants, new { code, name = "Other" });

        await HttpAssert.CodeTakenAsync(upper);
        await HttpAssert.CodeTakenAsync(same);
    }

    [Fact]
    public async Task AC14_Concurrent_creation_of_one_tenant_code_never_returns_500()
    {
        var code = XerpFixture.UniqueCode();
        using var admin = app.Admin();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => admin.PostAsJsonAsync(Tenants, new { code, name = "Race" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var conflict in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            await HttpAssert.CodeTakenAsync(conflict);
    }

    public static TheoryData<string, string> InvalidTenantBodies() => new()
    {
        { """{ "name": "Acme" }""", "code" },
        { """{ "code": null, "name": "Acme" }""", "code" },
        { """{ "code": "ac me", "name": "Acme" }""", "code" },
        { $$"""{ "code": "{{new string('c', 51)}}", "name": "Acme" }""", "code" },
        { """{ "code": "acme-valid-code", "name": "   " }""", "name" },
        { """{ "code": "acme-valid-code" }""", "name" },
        { $$"""{ "code": "acme-valid-code", "name": "{{new string('n', 201)}}" }""", "name" },
    };

    [Theory]
    [MemberData(nameof(InvalidTenantBodies))]
    public async Task AC15_Invalid_tenant_is_rejected_with_the_field_key(string json, string errorKey)
    {
        using var response = await app.Admin().PostAsync(Tenants, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Theory]
    [InlineData("""{ "code": "acme-x", "name": "Acme", "isActive": false }""")]
    [InlineData("""{ "code": "acme-x", "name": "Acme", "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "acme-x", "name": 5 }""")]
    [InlineData("""{ "code": "acme-x", """)]
    [InlineData("")]
    public async Task AC15_Tenant_body_with_unknown_property_or_malformed_json_is_rejected(string json)
    {
        using var response = await app.Admin().PostAsync(Tenants, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response);
    }

    [Fact]
    public async Task AC16_Admin_route_without_valid_admin_credentials_is_unauthenticated()
    {
        using var none = await app.Anonymous().PostAsJsonAsync(Tenants, NewTenantBody());
        using var wrong = await app.WithBearer("wrong").PostAsJsonAsync(Tenants, NewTenantBody());
        using var basicClient = app.Anonymous();
        basicClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic dXNlcjpwYXNzd29yZA==");
        using var basic = await basicClient.PostAsJsonAsync(Tenants, NewTenantBody());
        using var basicAdminClient = app.Anonymous();
        basicAdminClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Basic {XerpFixture.AdminKey}");
        using var basicAdmin = await basicAdminClient.PostAsJsonAsync(Tenants, NewTenantBody());

        await HttpAssert.UnauthenticatedAsync(none);
        await HttpAssert.UnauthenticatedAsync(wrong);
        await HttpAssert.UnauthenticatedAsync(basic);
        await HttpAssert.UnauthenticatedAsync(basicAdmin);
    }

    [Fact]
    public async Task AC17_Tenant_key_on_admin_route_is_forbidden()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsJsonAsync(Tenants, NewTenantBody());

        await HttpAssert.ForbiddenAsync(response);
    }

    [Theory]
    [InlineData(WhoAmI)]
    [InlineData(Uom.Path)]
    public async Task AC18_Admin_key_on_tenant_route_is_forbidden(string path)
    {
        using var response = await app.Admin().GetAsync(path);

        await HttpAssert.ForbiddenAsync(response);
    }

    [Theory]
    [InlineData(WhoAmI)]
    [InlineData(Uom.Path)]
    public async Task AC19_Tenant_route_without_a_valid_key_is_unauthenticated(string path)
    {
        using var none = await app.Anonymous().GetAsync(path);
        using var unknown = await app.WithBearer(XerpFixture.NewKeyString()).GetAsync(path);
        using var malformed = await app.WithBearer("not-a-key").GetAsync(path);
        using var basicClient = app.Anonymous();
        basicClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic dXNlcjpwYXNzd29yZA==");
        using var basic = await basicClient.GetAsync(path);

        await HttpAssert.UnauthenticatedAsync(none);
        await HttpAssert.UnauthenticatedAsync(unknown);
        await HttpAssert.UnauthenticatedAsync(malformed);
        await HttpAssert.UnauthenticatedAsync(basic);
    }

    [Fact]
    public async Task AC19_Write_routes_without_a_key_are_unauthenticated_and_write_nothing()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var post = await app.Anonymous().PostAsJsonAsync(Uom.Path, new { code = "g", name = "Gram" });
        using var put = await app.Anonymous().PutAsJsonAsync($"{Uom.Path}/{unit.Id()}", new { code = "x", name = "X", isActive = false });
        using var delete = await app.Anonymous().DeleteAsync($"{Uom.Path}/{unit.Id()}");
        using var byCode = await app.Anonymous().GetAsync($"{Uom.Path}/by-code/kg");
        using var byId = await app.Anonymous().GetAsync($"{Uom.Path}/{unit.Id()}");

        await HttpAssert.UnauthenticatedAsync(post);
        await HttpAssert.UnauthenticatedAsync(put);
        await HttpAssert.UnauthenticatedAsync(delete);
        await HttpAssert.UnauthenticatedAsync(byCode);
        await HttpAssert.UnauthenticatedAsync(byId);
        var list = await Uom.ListAsync(tenant.Client);
        Assert.Equal(["kg"], list.Codes());
        Assert.Equal("Kilogram", list.GetProperty("items")[0].Str("name"));
    }

    [Fact]
    public async Task AC20_Deactivated_key_is_unauthenticated()
    {
        var tenant = await app.NewTenantAsync();
        using (var ok = await tenant.Client.GetAsync(WhoAmI))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var updated = await app.ExecuteSqlAsync("""UPDATE "ApiKeys" SET "IsActive" = false WHERE "Id" = @id""", ("id", tenant.ApiKeyId));
        Assert.Equal(1, updated);

        using var whoami = await tenant.Client.GetAsync(WhoAmI);
        using var list = await tenant.Client.GetAsync(Uom.Path);
        await HttpAssert.UnauthenticatedAsync(whoami);
        await HttpAssert.UnauthenticatedAsync(list);
    }

    [Fact]
    public async Task AC20_Key_of_a_deactivated_tenant_is_unauthenticated()
    {
        var tenant = await app.NewTenantAsync();
        using (var ok = await tenant.Client.GetAsync(WhoAmI))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var updated = await app.ExecuteSqlAsync("""UPDATE "Tenants" SET "IsActive" = false WHERE "Id" = @id""", ("id", tenant.Id));
        Assert.Equal(1, updated);

        using var whoami = await tenant.Client.GetAsync(WhoAmI);
        using var list = await tenant.Client.GetAsync(Uom.Path);
        await HttpAssert.UnauthenticatedAsync(whoami);
        await HttpAssert.UnauthenticatedAsync(list);
    }

    [Fact]
    public async Task AC21_Host_without_admin_key_rejects_every_admin_request()
    {
        var host = app.CreateHost(adminKey: null);

        using var emptyClient = host.CreateClient();
        emptyClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        using var empty = await emptyClient.PostAsJsonAsync(Tenants, NewTenantBody());
        using var bareClient = host.CreateClient();
        bareClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer");
        using var bare = await bareClient.PostAsJsonAsync(Tenants, NewTenantBody());
        using var none = await host.CreateClient().PostAsJsonAsync(Tenants, NewTenantBody());
        using var otherHostsKey = await XerpFixture.Bearer(host.CreateClient(), XerpFixture.AdminKey).PostAsJsonAsync(Tenants, NewTenantBody());

        await HttpAssert.UnauthenticatedAsync(empty);
        await HttpAssert.UnauthenticatedAsync(bare);
        await HttpAssert.UnauthenticatedAsync(none);
        await HttpAssert.UnauthenticatedAsync(otherHostsKey);
    }

    [Fact]
    public async Task AC21_Host_with_31_character_admin_key_rejects_that_key()
    {
        const string shortKey = "0123456789abcdef0123456789abcde";
        Assert.Equal(31, shortKey.Length);
        var host = app.CreateHost(shortKey);

        using var response = await XerpFixture.Bearer(host.CreateClient(), shortKey).PostAsJsonAsync(Tenants, NewTenantBody());
        using var emptyClient = host.CreateClient();
        emptyClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        using var empty = await emptyClient.PostAsJsonAsync(Tenants, NewTenantBody());

        await HttpAssert.UnauthenticatedAsync(response);
        await HttpAssert.UnauthenticatedAsync(empty);
    }

    [Fact]
    public async Task AC21_Host_with_32_character_admin_key_accepts_it()
    {
        const string key = "0123456789abcdef0123456789abcdef";
        var host = app.CreateHost(key);

        using var response = await XerpFixture.Bearer(host.CreateClient(), key).PostAsJsonAsync(Tenants, NewTenantBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task AC22_Only_the_sha256_hash_of_the_key_is_stored()
    {
        var tenant = await app.NewTenantAsync();

        await using var connection = await app.OpenDbAsync();
        await using var command = new Npgsql.NpgsqlCommand("""SELECT * FROM "ApiKeys" WHERE "TenantId" = @t""", connection);
        command.Parameters.AddWithValue("t", tenant.Id);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var columns = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i))!);
        Assert.All(columns.Values, value => Assert.DoesNotContain(tenant.Key, value));
        Assert.All(columns.Values, value => Assert.DoesNotContain(tenant.Key["xerp_".Length..], value));
        Assert.Equal(XerpFixture.Sha256Hex(tenant.Key), columns["KeyHash"]);
        Assert.Equal(tenant.ApiKeyId.ToString(), columns["Id"]);
        Assert.Equal("initial", columns["Name"]);
        Assert.Equal("human", columns["ActorType"]);
        Assert.Equal("True", columns["IsActive"]);
        Assert.False(await reader.ReadAsync(), "A new tenant has exactly one API key (R12).");
    }
}
