using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

[Collection(XerpCollection.Name)]
public class UnitOfMeasureListTests(XerpFixture app)
{
    private async Task<TestTenant> TenantWithAsync(params (string Code, string Name)[] units)
    {
        var tenant = await app.NewTenantAsync();
        foreach (var (code, name) in units)
            await Uom.CreateAsync(tenant.Client, code, name);
        return tenant;
    }

    [Fact]
    public async Task AC50_New_tenant_has_an_empty_list_with_default_paging()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(Uom.Path);

        var list = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("""{"items":[],"total":0,"limit":50,"offset":0}""", list.GetRawText());
    }

    [Fact]
    public async Task AC51_List_is_ordered_by_code_case_insensitively()
    {
        var tenant = await TenantWithAsync(("b", "Bee"), ("a", "Ay"), ("c", "Sea"));

        var list = await Uom.ListAsync(tenant.Client);

        Assert.Equal(["a", "b", "c"], list.Codes());
        Assert.Equal(3, list.Total());

        var mixed = await TenantWithAsync(("b", "x"), ("C", "x"), ("a", "x"), ("D", "x"));
        Assert.Equal(["a", "b", "C", "D"], (await Uom.ListAsync(mixed.Client)).Codes());
    }

    [Fact]
    public async Task AC52_Paging()
    {
        var tenant = await TenantWithAsync(("b", "Bee"), ("a", "Ay"), ("c", "Sea"));

        var first = await Uom.ListAsync(tenant.Client, "?limit=2");
        var second = await Uom.ListAsync(tenant.Client, "?limit=2&offset=2");
        var beyond = await Uom.ListAsync(tenant.Client, "?offset=10");

        Assert.Equal(["a", "b"], first.Codes());
        Assert.Equal(3, first.Total());
        Assert.Equal(2, first.GetProperty("limit").GetInt32());
        Assert.Equal(0, first.GetProperty("offset").GetInt32());
        Assert.Equal(["c"], second.Codes());
        Assert.Equal(3, second.Total());
        Assert.Equal(2, second.GetProperty("offset").GetInt32());
        Assert.Empty(beyond.Codes());
        Assert.Equal(3, beyond.Total());
        Assert.Equal(50, beyond.GetProperty("limit").GetInt32());
        Assert.Equal(10, beyond.GetProperty("offset").GetInt32());
    }

    [Theory]
    [InlineData("?limit=1", 1)]
    [InlineData("?limit=500", 500)]
    public async Task AC53_Limit_bounds_are_accepted(string query, int limit)
    {
        var tenant = await TenantWithAsync(("a", "Ay"), ("b", "Bee"));

        var list = await Uom.ListAsync(tenant.Client, query);

        Assert.Equal(limit, list.GetProperty("limit").GetInt32());
        Assert.Equal(Math.Min(limit, 2), list.Codes().Length);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=501", "limit")]
    [InlineData("?limit=-1", "limit")]
    [InlineData("?limit=abc", "limit")]
    [InlineData("?limit=1.5", "limit")]
    [InlineData("?limit=99999999999999999999", "limit")]
    [InlineData("?offset=-1", "offset")]
    [InlineData("?offset=abc", "offset")]
    public async Task AC53_Out_of_range_paging_is_rejected_not_clamped(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(Uom.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Fact]
    public async Task AC54_IsActive_filter()
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "a", "Active one");
        await Uom.CreateAsync(tenant.Client, "b", "Inactive", isActive: false);
        await Uom.CreateAsync(tenant.Client, "c", "Active two");

        var active = await Uom.ListAsync(tenant.Client, "?isActive=true");
        var inactive = await Uom.ListAsync(tenant.Client, "?isActive=false");
        var both = await Uom.ListAsync(tenant.Client);
        using var maybe = await tenant.Client.GetAsync($"{Uom.Path}?isActive=maybe");

        Assert.Equal(["a", "c"], active.Codes());
        Assert.Equal(2, active.Total());
        Assert.Equal(["b"], inactive.Codes());
        Assert.Equal(1, inactive.Total());
        Assert.Equal(["a", "b", "c"], both.Codes());
        Assert.Equal(3, both.Total());
        await HttpAssert.ValidationAsync(maybe, "isActive");
    }

    [Fact]
    public async Task AC55_Search_matches_code_or_name_case_insensitively()
    {
        var tenant = await TenantWithAsync(("kg", "Kilogram"), ("g", "Gram"), ("pcs", "Piece"));

        var gram = await Uom.ListAsync(tenant.Client, "?search=GRAM");
        var pc = await Uom.ListAsync(tenant.Client, "?search=pc");
        var padded = await Uom.ListAsync(tenant.Client, "?search=%20kg%20");
        var none = await Uom.ListAsync(tenant.Client, "?search=zzz");
        var empty = await Uom.ListAsync(tenant.Client, "?search=");
        var blank = await Uom.ListAsync(tenant.Client, "?search=%20%20");
        var upperCode = await Uom.ListAsync(tenant.Client, "?search=PCS");

        Assert.Equal(["g", "kg"], gram.Codes());
        Assert.Equal(2, gram.Total());
        Assert.Equal(["pcs"], pc.Codes());
        Assert.Equal(["kg"], padded.Codes());
        Assert.Empty(none.Codes());
        Assert.Equal(0, none.Total());
        Assert.Equal(["g", "kg", "pcs"], empty.Codes());
        Assert.Equal(["g", "kg", "pcs"], blank.Codes());
        Assert.Equal(["pcs"], upperCode.Codes());
    }

    [Fact]
    public async Task AC55_Search_combines_with_isActive_and_paging()
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");
        await Uom.CreateAsync(tenant.Client, "g", "Gram", isActive: false);
        await Uom.CreateAsync(tenant.Client, "mg", "Milligram");

        var active = await Uom.ListAsync(tenant.Client, "?search=gram&isActive=true&limit=1&offset=1");

        Assert.Equal(["mg"], active.Codes());
        Assert.Equal(2, active.Total());
    }

    [Fact]
    public async Task AC56_Search_wildcards_are_literal()
    {
        var tenant = await TenantWithAsync(("cot", "100% cotton"), ("pln", "Plain"));

        var percent = await Uom.ListAsync(tenant.Client, "?search=%25");
        var underscore = await Uom.ListAsync(tenant.Client, "?search=_");
        var backslash = await Uom.ListAsync(tenant.Client, "?search=%5C");
        var mixed = await Uom.ListAsync(tenant.Client, "?search=0%25%20c");

        Assert.Equal(["cot"], percent.Codes());
        Assert.Equal(1, percent.Total());
        Assert.Empty(underscore.Codes());
        Assert.Equal(0, underscore.Total());
        Assert.Empty(backslash.Codes());
        Assert.Equal(["cot"], mixed.Codes());
    }

    [Fact]
    public async Task AC56_Underscore_and_backslash_match_themselves()
    {
        var tenant = await TenantWithAsync(("l_1", "Litre one"), ("l11", "Litre eleven"), ("bs", @"back\slash"));

        Assert.Equal(["l_1"], (await Uom.ListAsync(tenant.Client, "?search=_")).Codes());
        Assert.Equal(["bs"], (await Uom.ListAsync(tenant.Client, "?search=%5C")).Codes());
    }

    [Fact]
    public async Task AC57_Search_length_limit()
    {
        var tenant = await app.NewTenantAsync();

        var ok = await Uom.ListAsync(tenant.Client, $"?search={new string('x', 100)}");
        using var tooLong = await tenant.Client.GetAsync($"{Uom.Path}?search={new string('x', 101)}");

        Assert.Equal(0, ok.Total());
        await HttpAssert.ValidationAsync(tooLong, "search");
    }
}
