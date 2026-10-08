using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

[Collection(XerpCollection.Name)]
public class UnitOfMeasureTests(XerpFixture app)
{
    private static readonly string[] Representation =
        ["code", "createdAt", "createdBy", "id", "isActive", "name", "updatedAt", "updatedBy"];

    [Fact]
    public async Task AC30_Create_returns_201_with_location_and_full_representation()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await Uom.PostAsync(tenant.Client, "kg", "Kilogram");

        var unit = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(7, unit.Id().Version);
        Assert.Equal("kg", unit.Str("code"));
        Assert.Equal("Kilogram", unit.Str("name"));
        Assert.True(unit.GetProperty("isActive").GetBoolean());
        Assert.Equal(unit.Str("createdAt"), unit.Str("updatedAt"));
        Assert.EndsWith("Z", unit.Str("createdAt"));
        Assert.InRange(unit.GetProperty("createdAt").GetDateTime().ToUniversalTime(), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(tenant.ApiKeyId, unit.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, unit.GetProperty("updatedBy").GetGuid());
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/v1/units-of-measure/{unit.Id()}", response.Headers.Location!.ToString());
        Assert.False(unit.TryGetProperty("tenantId", out _));
        Assert.Equal(Representation, unit.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task AC31_Get_by_id_and_by_code_in_any_case_return_the_same_unit()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        var byId = await Uom.GetAsync(tenant.Client, created.Id());
        using var lowerResponse = await tenant.Client.GetAsync($"{Uom.Path}/by-code/kg");
        var lower = await HttpAssert.JsonAsync(lowerResponse, HttpStatusCode.OK);
        using var upperResponse = await tenant.Client.GetAsync($"{Uom.Path}/by-code/KG");
        var upper = await HttpAssert.JsonAsync(upperResponse, HttpStatusCode.OK);

        Assert.Equal(created.ToString(), byId.ToString());
        Assert.Equal(created.ToString(), lower.ToString());
        Assert.Equal(created.ToString(), upper.ToString());
        Assert.Equal("kg", upper.Str("code"));
    }

    [Fact]
    public async Task AC32_Code_and_name_are_trimmed()
    {
        var tenant = await app.NewTenantAsync();

        var unit = await Uom.CreateAsync(tenant.Client, "  pcs  ", "  Piece  ");

        Assert.Equal("pcs", unit.Str("code"));
        Assert.Equal("Piece", unit.Str("name"));
        var stored = await Uom.GetAsync(tenant.Client, unit.Id());
        Assert.Equal("pcs", stored.Str("code"));
        Assert.Equal("Piece", stored.Str("name"));
    }

    [Fact]
    public async Task AC33_Create_inactive()
    {
        var tenant = await app.NewTenantAsync();

        var unit = await Uom.CreateAsync(tenant.Client, "old", "Obsolete", isActive: false);

        Assert.False(unit.GetProperty("isActive").GetBoolean());
        Assert.False((await Uom.GetAsync(tenant.Client, unit.Id())).GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData("m²")]
    [InlineData("kom.")]
    [InlineData("box-10")]
    [InlineData("l_1")]
    public async Task AC34_Codes_with_letters_numbers_dot_underscore_dash_are_accepted(string code)
    {
        var tenant = await app.NewTenantAsync();

        var unit = await Uom.CreateAsync(tenant.Client, code, "Unit");

        Assert.Equal(code, unit.Str("code"));
        using var byCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/{Uri.EscapeDataString(code)}");
        Assert.Equal(unit.Id(), (await HttpAssert.JsonAsync(byCode, HttpStatusCode.OK)).Id());
    }

    [Fact]
    public async Task AC35_Length_limits_are_inclusive()
    {
        var tenant = await app.NewTenantAsync();

        var max = await Uom.CreateAsync(tenant.Client, new string('c', 50), new string('n', 200));
        using var longCode = await Uom.PostAsync(tenant.Client, new string('d', 51), "Name");
        using var longName = await Uom.PostAsync(tenant.Client, "ok", new string('n', 201));

        Assert.Equal(50, max.Str("code").Length);
        Assert.Equal(200, max.Str("name").Length);
        await HttpAssert.ValidationAsync(longCode, "code");
        await HttpAssert.ValidationAsync(longName, "name");
        Assert.Equal(1, (await Uom.ListAsync(tenant.Client)).Total());
    }

    public static TheoryData<string, string[]> InvalidFieldBodies() => new()
    {
        { """{ "name": "Name" }""", ["code"] },
        { """{ "code": null, "name": "Name" }""", ["code"] },
        { """{ "code": "", "name": "Name" }""", ["code"] },
        { """{ "code": "   ", "name": "Name" }""", ["code"] },
        { """{ "code": "a b", "name": "Name" }""", ["code"] },
        { """{ "code": "a/b", "name": "Name" }""", ["code"] },
        { """{ "code": "a%", "name": "Name" }""", ["code"] },
        { """{ "code": "a?", "name": "Name" }""", ["code"] },
        { """{ "code": "kg" }""", ["name"] },
        { """{ "code": "kg", "name": null }""", ["name"] },
        { """{ "code": "kg", "name": "" }""", ["name"] },
        { """{ "code": "kg", "name": "   " }""", ["name"] },
        { """{ "code": "a b", "name": " " }""", ["code", "name"] },
        { """{ }""", ["code", "name"] },
    };

    [Theory]
    [MemberData(nameof(InvalidFieldBodies))]
    public async Task AC36_Invalid_code_or_name_is_rejected_with_the_field_keys(string json, string[] errorKeys)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(Uom.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKeys);
        Assert.Equal(0, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Theory]
    [InlineData("""{ "code": "kg", "name": "Kilogram", "tenantId": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "kg", "name": "Kilogram", "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "kg", "name": "Kilogram", "foo": 1 }""")]
    [InlineData("""{ "code": "kg", "name": "Kilogram", "createdAt": "2020-01-01T00:00:00Z" }""")]
    [InlineData("""{ "code": "kg", "name": "Kilogram", "createdBy": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "kg", "name": "Kilogram" """)]
    [InlineData("""not json""")]
    [InlineData("")]
    [InlineData("""{ "code": "kg", "name": 123 }""")]
    [InlineData("""{ "code": "kg", "name": "Kilogram", "isActive": "yes" }""")]
    [InlineData("""[ { "code": "kg", "name": "Kilogram" } ]""")]
    [InlineData("null")]
    public async Task AC37_Unknown_property_malformed_json_empty_body_or_wrong_type_is_rejected(string json)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(Uom.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response);
        Assert.Equal(0, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC37_Request_without_a_body_is_rejected()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(Uom.Path, content: null);

        await HttpAssert.ValidationAsync(response);
        Assert.Equal(0, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC38_Duplicate_code_in_any_case_is_taken()
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var same = await Uom.PostAsync(tenant.Client, "kg", "Again");
        using var upper = await Uom.PostAsync(tenant.Client, "KG", "Upper");
        using var padded = await Uom.PostAsync(tenant.Client, "  Kg ", "Padded");

        await HttpAssert.CodeTakenAsync(same);
        await HttpAssert.CodeTakenAsync(upper);
        await HttpAssert.CodeTakenAsync(padded);
        var list = await Uom.ListAsync(tenant.Client);
        Assert.Equal(["kg"], list.Codes());
        Assert.Equal("Kilogram", list.GetProperty("items")[0].Str("name"));
    }

    [Fact]
    public async Task AC39_Ten_parallel_creates_of_one_code_give_one_201_and_nine_409()
    {
        var tenant = await app.NewTenantAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => Uom.PostAsync(tenant.Client, i % 2 == 0 ? "race" : "RACE", $"Racer {i}")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await HttpAssert.CodeTakenAsync(conflict);
        Assert.Equal(1, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC40_Replace_changes_values_and_keeps_identity_and_creation_audit()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var response = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{created.Id()}",
            new { code = "kgm", name = "Kilogramme", isActive = false });

        var updated = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("kgm", updated.Str("code"));
        Assert.Equal("Kilogramme", updated.Str("name"));
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        Assert.Equal(created.Id(), updated.Id());
        Assert.Equal(created.Str("createdAt"), updated.Str("createdAt"));
        Assert.Equal(created.Str("createdBy"), updated.Str("createdBy"));
        Assert.Equal(tenant.ApiKeyId, updated.GetProperty("updatedBy").GetGuid());
        Assert.True(updated.GetProperty("updatedAt").GetDateTime() >= created.GetProperty("updatedAt").GetDateTime());
        Assert.Equal(Representation, updated.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(updated.ToString(), (await Uom.GetAsync(tenant.Client, created.Id())).ToString());
        using var oldCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/kg");
        await HttpAssert.NotFoundAsync(oldCode);
        using var newCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/kgm");
        Assert.Equal(HttpStatusCode.OK, newCode.StatusCode);
    }

    [Theory]
    [InlineData("""{ "code": "x", "name": "X" }""", "isActive")]
    [InlineData("""{ "code": "x", "name": "X", "isActive": null }""", "isActive")]
    [InlineData("""{ "name": "X", "isActive": false }""", "code")]
    [InlineData("""{ "code": "x", "isActive": false }""", "name")]
    public async Task AC41_Replace_requires_all_fields_and_leaves_the_unit_unchanged(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var response = await tenant.Client.PutAsync($"{Uom.Path}/{created.Id()}", HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
        Assert.Equal(created.ToString(), (await Uom.GetAsync(tenant.Client, created.Id())).ToString());
    }

    [Theory]
    [InlineData("""{ "code": "x", "name": "X", "isActive": true, "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "x", "name": "X", "isActive": true, "updatedBy": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "x", "name": "X", "isActive": 1 }""")]
    [InlineData("")]
    public async Task AC41_Replace_with_unknown_property_or_bad_json_is_rejected_and_leaves_the_unit_unchanged(string json)
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var response = await tenant.Client.PutAsync($"{Uom.Path}/{created.Id()}", HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response);
        Assert.Equal(created.ToString(), (await Uom.GetAsync(tenant.Client, created.Id())).ToString());
    }

    [Fact]
    public async Task AC42_Replace_may_keep_its_own_code_or_change_only_its_case()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var sameResponse = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{created.Id()}",
            new { code = "kg", name = "Kilo", isActive = true });
        var same = await HttpAssert.JsonAsync(sameResponse, HttpStatusCode.OK);
        using var upperResponse = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{created.Id()}",
            new { code = "KG", name = "Kilo", isActive = true });
        var upper = await HttpAssert.JsonAsync(upperResponse, HttpStatusCode.OK);

        Assert.Equal("kg", same.Str("code"));
        Assert.Equal("Kilo", same.Str("name"));
        Assert.Equal("KG", upper.Str("code"));
        Assert.Equal("KG", (await Uom.GetAsync(tenant.Client, created.Id())).Str("code"));
        Assert.Equal(1, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Theory]
    [InlineData("g")]
    [InlineData("G")]
    public async Task AC43_Replace_to_the_code_of_another_unit_is_taken_and_leaves_the_unit_unchanged(string code)
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "g", "Gram");
        var kg = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var response = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{kg.Id()}",
            new { code, name = "Changed", isActive = false });

        await HttpAssert.CodeTakenAsync(response);
        Assert.Equal(kg.ToString(), (await Uom.GetAsync(tenant.Client, kg.Id())).ToString());
    }

    [Fact]
    public async Task AC44_Unknown_id_or_code_is_not_found()
    {
        var tenant = await app.NewTenantAsync();
        var id = Guid.CreateVersion7();

        using var put = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{id}", new { code = "x", name = "X", isActive = true });
        using var get = await tenant.Client.GetAsync($"{Uom.Path}/{id}");
        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{id}");
        using var notUuid = await tenant.Client.GetAsync($"{Uom.Path}/not-a-uuid");
        using var putNotUuid = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/not-a-uuid", new { code = "x", name = "X", isActive = true });
        using var deleteNotUuid = await tenant.Client.DeleteAsync($"{Uom.Path}/not-a-uuid");
        using var byCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/nope");
        using var byInvalidCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/a%20b");
        using var byLongCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/{new string('x', 51)}");
        using var byWildcard = await tenant.Client.GetAsync($"{Uom.Path}/by-code/%25");

        await HttpAssert.NotFoundAsync(put);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.NotFoundAsync(notUuid);
        await HttpAssert.NotFoundAsync(putNotUuid);
        await HttpAssert.NotFoundAsync(deleteNotUuid);
        await HttpAssert.NotFoundAsync(byCode);
        await HttpAssert.NotFoundAsync(byInvalidCode);
        await HttpAssert.NotFoundAsync(byLongCode);
        await HttpAssert.NotFoundAsync(byWildcard);
        Assert.Equal(0, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC45_Delete_removes_the_unit_and_frees_its_code()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{created.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await delete.Content.ReadAsByteArrayAsync());

        using var get = await tenant.Client.GetAsync($"{Uom.Path}/{created.Id()}");
        await HttpAssert.NotFoundAsync(get);
        using var again = await tenant.Client.DeleteAsync($"{Uom.Path}/{created.Id()}");
        await HttpAssert.NotFoundAsync(again);
        var recreated = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");
        Assert.NotEqual(created.Id(), recreated.Id());
        var rows = await app.ScalarAsync<long>("""SELECT count(*) FROM "UnitsOfMeasure" WHERE "TenantId" = @t""", ("t", tenant.Id));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task AC46_Replace_by_a_second_key_sets_updatedBy_and_keeps_createdBy()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");
        var (secondKeyId, secondKey) = await app.InsertApiKeyAsync(tenant.Id);
        using var second = app.WithBearer(secondKey);

        using var response = await second.PutAsJsonAsync($"{Uom.Path}/{created.Id()}",
            new { code = "kg", name = "Kilogram (SI)", isActive = true });

        var updated = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(secondKeyId, updated.GetProperty("updatedBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, updated.GetProperty("createdBy").GetGuid());
        var stored = await Uom.GetAsync(tenant.Client, created.Id());
        Assert.Equal(secondKeyId, stored.GetProperty("updatedBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, stored.GetProperty("createdBy").GetGuid());

        using var whoami = await second.GetAsync("/api/v1/whoami");
        var me = await HttpAssert.JsonAsync(whoami, HttpStatusCode.OK);
        Assert.Equal(secondKeyId, me.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        Assert.Equal("agent", me.GetProperty("actor").Str("actorType"));
        Assert.Equal(tenant.Id, me.GetProperty("tenant").GetProperty("id").GetGuid());
    }
}
