using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002: authentication, create and read (AC-10 … AC-33).</summary>
[Collection(XerpCollection.Name)]
public class ArticleTests(XerpFixture app)
{
    private static readonly string[] Representation =
    [
        "baseUnit", "code", "createdAt", "createdBy", "description", "id", "isActive", "name", "type", "updatedAt", "updatedBy",
    ];

    /// <summary>Replaces <c>{U}</c> with a unit id and <c>{UCODE}</c> with that unit's code.</summary>
    private static string Fill(string json, Guid unitId, string unitCode = "pcs") =>
        json.Replace("{U}", unitId.ToString()).Replace("{UCODE}", unitCode);

    [Fact]
    public async Task AC10_Article_routes_without_a_valid_key_are_unauthenticated()
    {
        var id = Guid.CreateVersion7();
        var body = Art.Body("A1", "Bolt", Guid.CreateVersion7());

        foreach (var client in new[] { app.Anonymous(), app.WithBearer(XerpFixture.NewKeyString()) })
        {
            using var list = await client.GetAsync(Art.Path);
            using var get = await client.GetAsync($"{Art.Path}/{id}");
            using var byCode = await client.GetAsync($"{Art.Path}/by-code/A1");
            using var post = await client.PostAsJsonAsync(Art.Path, body);
            using var put = await client.PutAsJsonAsync($"{Art.Path}/{id}", body);
            using var delete = await client.DeleteAsync($"{Art.Path}/{id}");

            await HttpAssert.UnauthenticatedAsync(list);
            await HttpAssert.UnauthenticatedAsync(get);
            await HttpAssert.UnauthenticatedAsync(byCode);
            await HttpAssert.UnauthenticatedAsync(post);
            await HttpAssert.UnauthenticatedAsync(put);
            await HttpAssert.UnauthenticatedAsync(delete);
        }
    }

    [Fact]
    public async Task AC11_Admin_key_on_article_routes_is_forbidden()
    {
        using var list = await app.Admin().GetAsync(Art.Path);
        using var post = await app.Admin().PostAsJsonAsync(Art.Path, Art.Body("A1", "Bolt", Guid.CreateVersion7()));

        await HttpAssert.ForbiddenAsync(list);
        await HttpAssert.ForbiddenAsync(post);
    }

    [Fact]
    public async Task AC20_Create_returns_201_with_location_and_full_representation()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "pcs", "Piece");

        using var response = await Art.PostAsync(tenant.Client, "ART-001", "Steel bolt M8", unit);

        var article = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(7, article.Id().Version);
        Assert.Equal("ART-001", article.Str("code"));
        Assert.Equal("Steel bolt M8", article.Str("name"));
        Assert.Equal(JsonValueKind.Null, article.GetProperty("description").ValueKind);
        Assert.Equal("stock", article.Str("type"));
        Assert.Equal("""{"id":"{{U}}","code":"pcs","name":"Piece"}""".Replace("{{U}}", unit.ToString()), article.GetProperty("baseUnit").GetRawText());
        Assert.True(article.GetProperty("isActive").GetBoolean());
        Assert.Equal(article.Str("createdAt"), article.Str("updatedAt"));
        Assert.EndsWith("Z", article.Str("createdAt"));
        Assert.Equal(tenant.ApiKeyId, article.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, article.GetProperty("updatedBy").GetGuid());
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/v1/articles/{article.Id()}", response.Headers.Location!.ToString());
        Assert.False(article.TryGetProperty("tenantId", out _));
        Assert.False(article.TryGetProperty("baseUnitId", out _));
        Assert.Equal(Representation, article.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task AC21_Get_by_id_and_by_code_in_any_case_return_the_same_article()
    {
        var tenant = await app.NewTenantAsync();
        var created = await Art.CreateAsync(tenant.Client, "ART-001", "Steel bolt M8", await Art.UnitAsync(tenant.Client));

        var byId = await Art.GetAsync(tenant.Client, created.Id());
        using var exactResponse = await tenant.Client.GetAsync($"{Art.Path}/by-code/ART-001");
        var exact = await HttpAssert.JsonAsync(exactResponse, HttpStatusCode.OK);
        using var lowerResponse = await tenant.Client.GetAsync($"{Art.Path}/by-code/art-001");
        var lower = await HttpAssert.JsonAsync(lowerResponse, HttpStatusCode.OK);

        Assert.Equal(created.ToString(), byId.ToString());
        Assert.Equal(created.ToString(), exact.ToString());
        Assert.Equal(created.ToString(), lower.ToString());
        Assert.Equal("ART-001", lower.Str("code"));
    }

    [Fact]
    public async Task AC22_Code_name_and_description_are_trimmed_and_line_breaks_kept()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);

        var article = await Art.CreateAsync(tenant.Client, "  A1  ", "  Bolt  ", unit, description: "  line1\nline2  ");

        Assert.Equal("A1", article.Str("code"));
        Assert.Equal("Bolt", article.Str("name"));
        Assert.Equal("line1\nline2", article.Str("description"));
        Assert.Equal(article.ToString(), (await Art.GetAsync(tenant.Client, article.Id())).ToString());
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public async Task AC23_Empty_blank_or_null_description_is_stored_as_null(string descriptionJson)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var json = Fill("""{ "code": "A1", "name": "Bolt", "type": "stock", "baseUnitId": "{U}", "description": {D} }""", unit)
            .Replace("{D}", descriptionJson);

        using var response = await tenant.Client.PostAsync(Art.Path, HttpAssert.Raw(json));

        var article = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, article.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Art.GetAsync(tenant.Client, article.Id())).GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task AC24_Create_inactive_service()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "h", "Hour");

        var article = await Art.CreateAsync(tenant.Client, "SRV-1", "Consulting", unit, type: "service", isActive: false);

        Assert.Equal("service", article.Str("type"));
        Assert.False(article.GetProperty("isActive").GetBoolean());
        Assert.Equal(unit, article.BaseUnitId());
        Assert.Equal("h", article.GetProperty("baseUnit").Str("code"));
        Assert.Equal(article.ToString(), (await Art.GetAsync(tenant.Client, article.Id())).ToString());
    }

    [Fact]
    public async Task AC25_Length_limits_are_inclusive()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);

        var max = await Art.CreateAsync(tenant.Client, new string('c', 50), new string('n', 200), unit, description: new string('d', 2000));
        using var longCode = await Art.PostAsync(tenant.Client, new string('x', 51), "Name", unit);
        using var longName = await Art.PostAsync(tenant.Client, "ok", new string('n', 201), unit);
        using var longDescription = await Art.PostAsync(tenant.Client, "ok", "Name", unit, description: new string('d', 2001));

        Assert.Equal(50, max.Str("code").Length);
        Assert.Equal(200, max.Str("name").Length);
        Assert.Equal(2000, max.Str("description").Length);
        await HttpAssert.ValidationAsync(longCode, "code");
        await HttpAssert.ValidationAsync(longName, "name");
        await HttpAssert.ValidationAsync(longDescription, "description");
        Assert.Equal(1, (await Art.ListAsync(tenant.Client)).Total());
    }

    public static TheoryData<string, string[]> InvalidFieldBodies() => new()
    {
        // AC-26
        { """{ "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": null, "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": "", "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": "   ", "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": "a b", "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": "a/b", "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": "a%", "name": "N", "type": "stock", "baseUnitId": "{U}" }""", ["code"] },
        { """{ "code": "A1", "type": "stock", "baseUnitId": "{U}" }""", ["name"] },
        { """{ "code": "A1", "name": null, "type": "stock", "baseUnitId": "{U}" }""", ["name"] },
        { """{ "code": "A1", "name": "", "type": "stock", "baseUnitId": "{U}" }""", ["name"] },
        { """{ "code": "A1", "name": "   ", "type": "stock", "baseUnitId": "{U}" }""", ["name"] },
        // AC-27
        { """{ "code": "A1", "name": "N", "baseUnitId": "{U}" }""", ["type"] },
        { """{ "code": "A1", "name": "N", "type": null, "baseUnitId": "{U}" }""", ["type"] },
        { """{ "code": "A1", "name": "N", "type": "", "baseUnitId": "{U}" }""", ["type"] },
        { """{ "code": "A1", "name": "N", "type": "Stock", "baseUnitId": "{U}" }""", ["type"] },
        { """{ "code": "A1", "name": "N", "type": "goods", "baseUnitId": "{U}" }""", ["type"] },
        // AC-28
        { """{ "code": "A1", "name": "N", "type": "stock" }""", ["baseUnitId"] },
        { """{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": null }""", ["baseUnitId"] },
        { """{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "" }""", ["baseUnitId"] },
        { """{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "abc" }""", ["baseUnitId"] },
        { """{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{UCODE}" }""", ["baseUnitId"] },
        // AC-29
        { """{ }""", ["code", "name", "type", "baseUnitId"] },
        { """{ "code": "a b", "name": " ", "type": "goods", "baseUnitId": "abc" }""", ["code", "name", "type", "baseUnitId"] },
    };

    [Theory]
    [MemberData(nameof(InvalidFieldBodies))]
    public async Task AC26_AC27_AC28_AC29_Invalid_fields_are_rejected_with_their_keys(string json, string[] errorKeys)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "pcs");

        using var response = await tenant.Client.PostAsync(Art.Path, HttpAssert.Raw(Fill(json, unit, "pcs")));

        await HttpAssert.ValidationAsync(response, errorKeys);
        Assert.Equal(0, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Theory]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": 123 }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": { "id": "{U}" } }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": 1, "baseUnitId": "{U}" }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "description": 5 }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "tenantId": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "baseUnit": { "id": "{U}" } }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "baseUnitCode": "pcs" }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "foo": 1 }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "createdAt": "2020-01-01T00:00:00Z" }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "createdBy": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}", "descriptionProvided": true }""")]
    [InlineData("""{ "code": "A1", "name": "N", "type": "stock", "baseUnitId": "{U}" """)]
    [InlineData("")]
    public async Task AC28_AC30_Wrong_json_type_unknown_property_malformed_json_or_empty_body_is_rejected(string json)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);

        using var response = await tenant.Client.PostAsync(Art.Path, HttpAssert.Raw(Fill(json, unit)));

        await HttpAssert.ValidationAsync(response);
        Assert.Equal("""{"items":[],"total":0,"limit":50,"offset":0}""", (await Art.ListAsync(tenant.Client)).GetRawText());
    }

    [Fact]
    public async Task AC31_Duplicate_code_in_any_case_is_taken()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "ART-1", "First", unit);

        using var same = await Art.PostAsync(tenant.Client, "ART-1", "Again", unit);
        using var lower = await Art.PostAsync(tenant.Client, "art-1", "Lower", unit);

        await HttpAssert.CodeTakenAsync(same);
        await HttpAssert.CodeTakenAsync(lower);
        var list = await Art.ListAsync(tenant.Client);
        Assert.Equal(["ART-1"], list.Codes());
        Assert.Equal("First", list.GetProperty("items")[0].Str("name"));
    }

    [Fact]
    public async Task AC32_Ten_parallel_creates_of_one_code_give_one_201_and_nine_409()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => Art.PostAsync(tenant.Client, i % 2 == 0 ? "race" : "RACE", $"Racer {i}", unit)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await HttpAssert.CodeTakenAsync(conflict);
        Assert.Equal(1, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC33_Article_codes_and_unit_codes_are_separate_namespaces()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "kg", "Kilogram");

        var article = await Art.CreateAsync(tenant.Client, "kg", "Sold by the kilogram", unit);

        using var unitByCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/kg");
        using var articleByCode = await tenant.Client.GetAsync($"{Art.Path}/by-code/kg");
        var foundUnit = await HttpAssert.JsonAsync(unitByCode, HttpStatusCode.OK);
        var foundArticle = await HttpAssert.JsonAsync(articleByCode, HttpStatusCode.OK);
        Assert.Equal(unit, foundUnit.Id());
        Assert.Equal("Kilogram", foundUnit.Str("name"));
        Assert.Equal(article.Id(), foundArticle.Id());
        Assert.Equal("Sold by the kilogram", foundArticle.Str("name"));
        Assert.NotEqual(unit, article.Id());
    }
}
