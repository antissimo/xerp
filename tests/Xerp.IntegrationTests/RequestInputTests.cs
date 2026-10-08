using System.Net;
using System.Net.Http.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 001 as amended after review 001: control characters (R4, R9), query strings (R15), no 500 (R16).</summary>
[Collection(XerpCollection.Name)]
public class RequestInputTests(XerpFixture app)
{
    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\u0007b")]
    public async Task AC47_Unit_name_with_a_control_character_is_rejected_on_create_and_replace(string name)
    {
        var tenant = await app.NewTenantAsync();
        var existing = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var created = await Uom.PostAsync(tenant.Client, "ctl", name);
        using var replaced = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{existing.Id()}", new { code = "kg", name, isActive = true });

        await HttpAssert.ValidationAsync(created, "name");
        await HttpAssert.ValidationAsync(replaced, "name");
        Assert.Equal(["kg"], (await Uom.ListAsync(tenant.Client)).Codes());
        Assert.Equal("Kilogram", (await Uom.GetAsync(tenant.Client, existing.Id())).Str("name"));
    }

    [Fact]
    public async Task AC47_Tenant_name_with_a_NUL_character_is_rejected()
    {
        using var admin = app.Admin();
        var code = XerpFixture.UniqueCode();

        using var response = await admin.PostAsJsonAsync("/api/v1/admin/tenants", new { code, name = "a\u0000b" });

        await HttpAssert.ValidationAsync(response, "name");
        Assert.Equal(0, await app.ScalarAsync<long>("""SELECT count(*) FROM "Tenants" WHERE "Code" = @code""", ("code", code)));
    }

    [Fact]
    public async Task AC47_Surrounding_tab_and_line_break_are_trimmed_from_a_name()
    {
        var tenant = await app.NewTenantAsync();

        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "\tPiece\n");

        Assert.Equal("Piece", unit.Str("name"));
    }

    [Theory]
    [InlineData("a%00b")]
    [InlineData("a%0Ab")]
    public async Task AC58_Search_with_a_control_character_is_rejected(string search)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync($"{Uom.Path}?search={search}");

        await HttpAssert.ValidationAsync(response, "search");
    }

    [Theory]
    [InlineData("?foo=1", "foo")]
    [InlineData("?serach=kg", "serach")]
    [InlineData("?Limit=1", "Limit")]
    [InlineData("?tenantId=0198c0de-0000-7000-8000-000000000001", "tenantId")]
    [InlineData("?limit=1&foo=1", "foo")]
    public async Task AC59_Unknown_query_parameter_on_the_list_is_rejected(string query, string key)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(Uom.Path + query);

        await HttpAssert.ValidationAsync(response, key);
    }

    [Fact]
    public async Task AC59_Query_parameter_on_operations_that_define_none_is_rejected()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var whoami = await tenant.Client.GetAsync("/api/v1/whoami?x=1");
        using var get = await tenant.Client.GetAsync($"{Uom.Path}/{unit.Id()}?x=1");
        using var byCode = await tenant.Client.GetAsync($"{Uom.Path}/by-code/kg?x=1");
        using var post = await tenant.Client.PostAsJsonAsync($"{Uom.Path}?x=1", new { code = "pcs", name = "Piece" });
        using var put = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{unit.Id()}?x=1", new { code = "kg", name = "Changed", isActive = true });
        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit.Id()}?x=1");

        foreach (var response in new[] { whoami, get, byCode, post, put, delete })
            await HttpAssert.ValidationAsync(response, "x");
        Assert.Equal(["kg"], (await Uom.ListAsync(tenant.Client)).Codes());
        Assert.Equal("Kilogram", (await Uom.GetAsync(tenant.Client, unit.Id())).Str("name"));
    }

    [Fact]
    public async Task AC59_Query_parameter_on_the_admin_route_is_rejected()
    {
        using var admin = app.Admin();
        var code = XerpFixture.UniqueCode();

        using var response = await admin.PostAsJsonAsync("/api/v1/admin/tenants?x=1", new { code, name = "Query" });

        await HttpAssert.ValidationAsync(response, "x");
        Assert.Equal(0, await app.ScalarAsync<long>("""SELECT count(*) FROM "Tenants" WHERE "Code" = @code""", ("code", code)));
    }

    [Fact]
    public async Task AC59_Empty_repeated_and_wrongly_cased_values()
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        Assert.Equal(1, (await Uom.ListAsync(tenant.Client, "?limit=")).Total());
        Assert.Equal(1, (await Uom.ListAsync(tenant.Client, "?isActive=")).Total());
        using var repeated = await tenant.Client.GetAsync($"{Uom.Path}?limit=1&limit=2");
        using var upperCase = await tenant.Client.GetAsync($"{Uom.Path}?isActive=TRUE");

        await HttpAssert.ValidationAsync(repeated, "limit");
        await HttpAssert.ValidationAsync(upperCase, "isActive");
    }

    [Fact]
    public async Task AC72_Method_the_path_does_not_support_is_NOT_FOUND()
    {
        var tenant = await app.NewTenantAsync();

        using var delete = await tenant.Client.DeleteAsync(Uom.Path);
        using var put = await tenant.Client.PutAsJsonAsync("/api/v1/whoami", new { });

        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.NotFoundAsync(put);
    }

    [Fact]
    public async Task AC73_Kind_of_credential_is_checked_before_the_existence_of_the_route()
    {
        var tenant = await app.NewTenantAsync();
        using var admin = app.Admin();

        using var adminOnTenantPath = await admin.GetAsync("/api/v1/nope");
        using var tenantOnAdminPath = await tenant.Client.GetAsync("/api/v1/admin/nope");

        await HttpAssert.ForbiddenAsync(adminOnTenantPath);
        await HttpAssert.ForbiddenAsync(tenantOnAdminPath);
    }

    /// <summary>Review 001, non-blocking 2: a locale change must fail a test, not silently allow duplicates.</summary>
    [Fact]
    public async Task R3_Codes_differing_only_in_the_case_of_a_non_ASCII_letter_collide()
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "Ž1", "Upper");

        using var response = await Uom.PostAsync(tenant.Client, "ž1", "Lower");

        await HttpAssert.CodeTakenAsync(response);
    }
}
