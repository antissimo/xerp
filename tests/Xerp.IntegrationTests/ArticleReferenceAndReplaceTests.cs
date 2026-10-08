using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002: base-unit reference, replace and delete (AC-40 … AC-60).</summary>
[Collection(XerpCollection.Name)]
public class ArticleReferenceAndReplaceTests(XerpFixture app)
{
    [Fact]
    public async Task AC40_Create_with_unknown_base_unit_is_reference_not_found()
    {
        var tenant = await app.NewTenantAsync();
        await Art.UnitAsync(tenant.Client);

        using var response = await Art.PostAsync(tenant.Client, "A1", "Bolt", Guid.CreateVersion7());

        await HttpAssert.ReferenceNotFoundAsync(response, "baseUnitId");
        Assert.Equal(0, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC41_Create_with_deleted_base_unit_is_reference_not_found()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        using (var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit}"))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var response = await Art.PostAsync(tenant.Client, "A1", "Bolt", unit);

        await HttpAssert.ReferenceNotFoundAsync(response, "baseUnitId");
        Assert.Equal(0, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC42_Create_with_inactive_base_unit_is_reference_inactive()
    {
        var tenant = await app.NewTenantAsync();
        var inactive = await Art.UnitAsync(tenant.Client, "old", "Obsolete", isActive: false);

        using var response = await Art.PostAsync(tenant.Client, "A1", "Bolt", inactive);

        await HttpAssert.ReferenceInactiveAsync(response, "baseUnitId");
        Assert.Equal(0, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC43_Validation_precedes_the_reference_check()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await Art.PostAsync(tenant.Client, "A1", "   ", Guid.CreateVersion7());

        var body = await HttpAssert.ValidationAsync(response, "name");
        Assert.False(body.GetProperty("errors").TryGetProperty("baseUnitId", out _));
    }

    [Fact]
    public async Task AC44_Reference_check_precedes_code_uniqueness()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);
        var inactive = await Art.UnitAsync(tenant.Client, "old", "Obsolete", isActive: false);

        using var unknown = await Art.PostAsync(tenant.Client, "A1", "Again", Guid.CreateVersion7());
        using var inactiveUnit = await Art.PostAsync(tenant.Client, "a1", "Again", inactive);

        await HttpAssert.ReferenceNotFoundAsync(unknown, "baseUnitId");
        await HttpAssert.ReferenceInactiveAsync(inactiveUnit, "baseUnitId");
    }

    [Fact]
    public async Task AC45_Article_shows_the_current_code_and_name_of_its_base_unit()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "pcs", "Piece");
        var article = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);
        var (_, secondKey) = await app.InsertApiKeyAsync(tenant.Id);
        using var second = app.WithBearer(secondKey);

        using var rename = await second.PutAsJsonAsync($"{Uom.Path}/{unit}", new { code = "kom", name = "Komad", isActive = true });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var after = await Art.GetAsync(tenant.Client, article.Id());
        Assert.Equal(unit, after.BaseUnitId());
        Assert.Equal("kom", after.GetProperty("baseUnit").Str("code"));
        Assert.Equal("Komad", after.GetProperty("baseUnit").Str("name"));
        Assert.Equal(article.Str("updatedAt"), after.Str("updatedAt"));
        Assert.Equal(article.Str("updatedBy"), after.Str("updatedBy"));
        var listed = (await Art.ListAsync(tenant.Client)).GetProperty("items")[0];
        Assert.Equal(after.ToString(), listed.ToString());
    }

    [Fact]
    public async Task AC50_Replace_changes_every_field_and_keeps_identity_and_creation_audit()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "pcs", "Piece");
        var unit2 = await Art.UnitAsync(tenant.Client, "h", "Hour");
        var created = await Art.CreateAsync(tenant.Client, "ART-001", "Steel bolt M8", unit);

        using var response = await Art.PutAsync(tenant.Client, created.Id(),
            new { code = "ART-002", name = "Bolt M10", description = "zinc", type = "service", baseUnitId = unit2, isActive = false });

        var updated = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("ART-002", updated.Str("code"));
        Assert.Equal("Bolt M10", updated.Str("name"));
        Assert.Equal("zinc", updated.Str("description"));
        Assert.Equal("service", updated.Str("type"));
        Assert.Equal(unit2, updated.BaseUnitId());
        Assert.Equal("h", updated.GetProperty("baseUnit").Str("code"));
        Assert.Equal("Hour", updated.GetProperty("baseUnit").Str("name"));
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        Assert.Equal(created.Id(), updated.Id());
        Assert.Equal(created.Str("createdAt"), updated.Str("createdAt"));
        Assert.Equal(created.Str("createdBy"), updated.Str("createdBy"));
        Assert.True(updated.GetProperty("updatedAt").GetDateTime() >= created.GetProperty("updatedAt").GetDateTime());
        Assert.Equal(updated.ToString(), (await Art.GetAsync(tenant.Client, created.Id())).ToString());
        using var oldCode = await tenant.Client.GetAsync($"{Art.Path}/by-code/ART-001");
        await HttpAssert.NotFoundAsync(oldCode);
        using var newCode = await tenant.Client.GetAsync($"{Art.Path}/by-code/art-002");
        Assert.Equal(HttpStatusCode.OK, newCode.StatusCode);
    }

    [Theory]
    [InlineData("""{ "name": "N", "description": null, "type": "stock", "baseUnitId": "{U}", "isActive": true }""", "code")]
    [InlineData("""{ "code": "X", "description": null, "type": "stock", "baseUnitId": "{U}", "isActive": true }""", "name")]
    [InlineData("""{ "code": "X", "name": "N", "type": "stock", "baseUnitId": "{U}", "isActive": true }""", "description")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "baseUnitId": "{U}", "isActive": true }""", "type")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "stock", "isActive": true }""", "baseUnitId")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "stock", "baseUnitId": "{U}" }""", "isActive")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "stock", "baseUnitId": "{U}", "isActive": null }""", "isActive")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "Service", "baseUnitId": "{U}", "isActive": true }""", "type")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "stock", "baseUnitId": "pcs", "isActive": true }""", "baseUnitId")]
    public async Task AC51_Replace_requires_every_field_and_leaves_the_article_unchanged(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "pcs");
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit, description: "kept");

        using var response = await tenant.Client.PutAsync($"{Art.Path}/{created.Id()}", HttpAssert.Raw(json.Replace("{U}", unit.ToString())));

        await HttpAssert.ValidationAsync(response, errorKey);
        Assert.Equal(created.ToString(), (await Art.GetAsync(tenant.Client, created.Id())).ToString());
    }

    [Theory]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "stock", "baseUnitId": "{U}", "isActive": true, "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "X", "name": "N", "description": null, "type": "stock", "baseUnitId": "{U}", "isActive": true, "baseUnit": null }""")]
    [InlineData("""{ "code": "X", "name": "N", "description": 7, "type": "stock", "baseUnitId": "{U}", "isActive": true }""")]
    [InlineData("")]
    public async Task AC51_Replace_with_unknown_property_or_bad_json_is_rejected_and_leaves_the_article_unchanged(string json)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit, description: "kept");

        using var response = await tenant.Client.PutAsync($"{Art.Path}/{created.Id()}", HttpAssert.Raw(json.Replace("{U}", unit.ToString())));

        await HttpAssert.ValidationAsync(response);
        Assert.Equal(created.ToString(), (await Art.GetAsync(tenant.Client, created.Id())).ToString());
    }

    [Fact]
    public async Task AC52_Replace_with_null_description_clears_it()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit, description: "zinc");
        Assert.Equal("zinc", created.Str("description"));

        using var response = await Art.PutAsync(tenant.Client, created.Id(), Art.ReplaceBody("A1", "Bolt", unit, description: null));

        var updated = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Art.GetAsync(tenant.Client, created.Id())).GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task AC53_Replace_may_keep_its_own_code_or_change_only_its_case()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var created = await Art.CreateAsync(tenant.Client, "art-1", "Bolt", unit);

        using var sameResponse = await Art.PutAsync(tenant.Client, created.Id(), Art.ReplaceBody("art-1", "Bolt 2", unit));
        var same = await HttpAssert.JsonAsync(sameResponse, HttpStatusCode.OK);
        using var upperResponse = await Art.PutAsync(tenant.Client, created.Id(), Art.ReplaceBody("ART-1", "Bolt 2", unit));
        var upper = await HttpAssert.JsonAsync(upperResponse, HttpStatusCode.OK);

        Assert.Equal("art-1", same.Str("code"));
        Assert.Equal("Bolt 2", same.Str("name"));
        Assert.Equal("ART-1", upper.Str("code"));
        Assert.Equal("ART-1", (await Art.GetAsync(tenant.Client, created.Id())).Str("code"));
        Assert.Equal(1, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Theory]
    [InlineData("N-8")]
    [InlineData("n-8")]
    public async Task AC54_Replace_to_the_code_of_another_article_is_taken_and_leaves_the_article_unchanged(string code)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "N-8", "Nut", unit);
        var bolt = await Art.CreateAsync(tenant.Client, "B-8", "Bolt", unit);

        using var response = await Art.PutAsync(tenant.Client, bolt.Id(), Art.ReplaceBody(code, "Changed", unit, isActive: false));

        await HttpAssert.CodeTakenAsync(response);
        Assert.Equal(bolt.ToString(), (await Art.GetAsync(tenant.Client, bolt.Id())).ToString());
    }

    [Fact]
    public async Task AC55_Replace_with_unknown_or_newly_assigned_inactive_base_unit_is_rejected_and_leaves_the_article_unchanged()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var inactive = await Art.UnitAsync(tenant.Client, "old", "Obsolete", isActive: false);
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);

        using var unknown = await Art.PutAsync(tenant.Client, created.Id(), Art.ReplaceBody("A2", "Changed", Guid.CreateVersion7()));
        using var toInactive = await Art.PutAsync(tenant.Client, created.Id(), Art.ReplaceBody("A2", "Changed", inactive));

        await HttpAssert.ReferenceNotFoundAsync(unknown, "baseUnitId");
        await HttpAssert.ReferenceInactiveAsync(toInactive, "baseUnitId");
        Assert.Equal(created.ToString(), (await Art.GetAsync(tenant.Client, created.Id())).ToString());
    }

    [Fact]
    public async Task AC56_Article_may_keep_a_base_unit_that_was_deactivated_but_it_cannot_be_newly_assigned()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client, "pcs", "Piece");
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);
        using (var deactivate = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{unit}", new { code = "pcs", name = "Piece", isActive = false }))
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        using var keep = await Art.PutAsync(tenant.Client, created.Id(), Art.ReplaceBody("A1", "Bolt renamed", unit));
        using var newArticle = await Art.PostAsync(tenant.Client, "A2", "Nut", unit);

        var updated = await HttpAssert.JsonAsync(keep, HttpStatusCode.OK);
        Assert.Equal("Bolt renamed", updated.Str("name"));
        Assert.Equal(unit, updated.BaseUnitId());
        await HttpAssert.ReferenceInactiveAsync(newArticle, "baseUnitId");
        Assert.Equal(["A1"], (await Art.ListAsync(tenant.Client)).Codes());
    }

    [Fact]
    public async Task AC57_Unknown_id_or_code_is_not_found_before_any_reference_check()
    {
        var tenant = await app.NewTenantAsync();
        var id = Guid.CreateVersion7();
        var body = Art.ReplaceBody("X", "X", Guid.CreateVersion7());

        using var put = await Art.PutAsync(tenant.Client, id, body);
        using var get = await tenant.Client.GetAsync($"{Art.Path}/{id}");
        using var delete = await tenant.Client.DeleteAsync($"{Art.Path}/{id}");
        using var notUuid = await tenant.Client.GetAsync($"{Art.Path}/not-a-uuid");
        using var putNotUuid = await tenant.Client.PutAsJsonAsync($"{Art.Path}/not-a-uuid", body);
        using var deleteNotUuid = await tenant.Client.DeleteAsync($"{Art.Path}/not-a-uuid");
        using var byCode = await tenant.Client.GetAsync($"{Art.Path}/by-code/nope");
        using var byInvalidCode = await tenant.Client.GetAsync($"{Art.Path}/by-code/a%20b");
        using var byWildcard = await tenant.Client.GetAsync($"{Art.Path}/by-code/%25");

        await HttpAssert.NotFoundAsync(put);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.NotFoundAsync(notUuid);
        await HttpAssert.NotFoundAsync(putNotUuid);
        await HttpAssert.NotFoundAsync(deleteNotUuid);
        await HttpAssert.NotFoundAsync(byCode);
        await HttpAssert.NotFoundAsync(byInvalidCode);
        await HttpAssert.NotFoundAsync(byWildcard);
    }

    [Fact]
    public async Task AC58_Replace_by_a_second_key_sets_updatedBy_and_keeps_createdBy()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);
        var (secondKeyId, secondKey) = await app.InsertApiKeyAsync(tenant.Id);
        using var second = app.WithBearer(secondKey);

        using var response = await Art.PutAsync(second, created.Id(), Art.ReplaceBody("A1", "Bolt (zinc)", unit));

        var updated = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(secondKeyId, updated.GetProperty("updatedBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, updated.GetProperty("createdBy").GetGuid());
        var stored = await Art.GetAsync(tenant.Client, created.Id());
        Assert.Equal(secondKeyId, stored.GetProperty("updatedBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, stored.GetProperty("createdBy").GetGuid());
    }

    [Fact]
    public async Task AC60_Delete_removes_the_article_and_frees_its_code()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var created = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);

        using var delete = await tenant.Client.DeleteAsync($"{Art.Path}/{created.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await delete.Content.ReadAsByteArrayAsync());

        using var get = await tenant.Client.GetAsync($"{Art.Path}/{created.Id()}");
        await HttpAssert.NotFoundAsync(get);
        using var again = await tenant.Client.DeleteAsync($"{Art.Path}/{created.Id()}");
        await HttpAssert.NotFoundAsync(again);
        var recreated = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);
        Assert.NotEqual(created.Id(), recreated.Id());
        var rows = await app.ScalarAsync<long>("""SELECT count(*) FROM "Articles" WHERE "TenantId" = @t""", ("t", tenant.Id));
        Assert.Equal(1, rows);
    }
}
