using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002: list (AC-70 … AC-79).</summary>
[Collection(XerpCollection.Name)]
public class ArticleListTests(XerpFixture app)
{
    private async Task<(TestTenant Tenant, Guid Unit)> TenantWithAsync(params (string Code, string Name)[] articles)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        foreach (var (code, name) in articles)
            await Art.CreateAsync(tenant.Client, code, name, unit);
        return (tenant, unit);
    }

    [Fact]
    public async Task AC70_New_tenant_has_an_empty_list_with_default_paging()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(Art.Path);

        var list = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("""{"items":[],"total":0,"limit":50,"offset":0}""", list.GetRawText());
    }

    [Fact]
    public async Task AC71_List_is_ordered_by_code_case_insensitively_with_full_items()
    {
        var (tenant, unit) = await TenantWithAsync(("b", "Bee"), ("A", "Ay"), ("c", "Sea"));

        var list = await Art.ListAsync(tenant.Client);

        Assert.Equal(["A", "b", "c"], list.Codes());
        Assert.Equal(3, list.Total());
        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(item.ToString(), (await Art.GetAsync(tenant.Client, item.Id())).ToString());
            Assert.Equal(unit, item.BaseUnitId());
            Assert.Equal("pcs", item.GetProperty("baseUnit").Str("code"));
            Assert.Equal("Piece", item.GetProperty("baseUnit").Str("name"));
        }
    }

    [Fact]
    public async Task AC72_Paging()
    {
        var (tenant, _) = await TenantWithAsync(("b", "Bee"), ("a", "Ay"), ("c", "Sea"));

        var first = await Art.ListAsync(tenant.Client, "?limit=2");
        var second = await Art.ListAsync(tenant.Client, "?limit=2&offset=2");
        var beyond = await Art.ListAsync(tenant.Client, "?offset=10");

        Assert.Equal(["a", "b"], first.Codes());
        Assert.Equal(3, first.Total());
        Assert.Equal(2, first.GetProperty("limit").GetInt32());
        Assert.Equal(0, first.GetProperty("offset").GetInt32());
        Assert.Equal(["c"], second.Codes());
        Assert.Equal(3, second.Total());
        Assert.Empty(beyond.Codes());
        Assert.Equal(3, beyond.Total());
        Assert.Equal(50, beyond.GetProperty("limit").GetInt32());
        Assert.Equal(10, beyond.GetProperty("offset").GetInt32());
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=501", "limit")]
    [InlineData("?limit=abc", "limit")]
    [InlineData("?offset=-1", "offset")]
    public async Task AC73_Out_of_range_paging_is_rejected(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(Art.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Fact]
    public async Task AC74_IsActive_filter()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "a", "Active one", unit);
        await Art.CreateAsync(tenant.Client, "b", "Inactive", unit, isActive: false);
        await Art.CreateAsync(tenant.Client, "c", "Active two", unit);

        var active = await Art.ListAsync(tenant.Client, "?isActive=true");
        var inactive = await Art.ListAsync(tenant.Client, "?isActive=false");
        var both = await Art.ListAsync(tenant.Client);
        using var maybe = await tenant.Client.GetAsync($"{Art.Path}?isActive=maybe");

        Assert.Equal(["a", "c"], active.Codes());
        Assert.Equal(2, active.Total());
        Assert.Equal(["b"], inactive.Codes());
        Assert.Equal(1, inactive.Total());
        Assert.Equal(["a", "b", "c"], both.Codes());
        Assert.Equal(3, both.Total());
        await HttpAssert.ValidationAsync(maybe, "isActive");
    }

    [Fact]
    public async Task AC75_Type_filter()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "s1", "Stock one", unit);
        await Art.CreateAsync(tenant.Client, "v1", "Service one", unit, type: "service");
        await Art.CreateAsync(tenant.Client, "s2", "Stock two", unit);

        var stock = await Art.ListAsync(tenant.Client, "?type=stock");
        var service = await Art.ListAsync(tenant.Client, "?type=service");
        using var goods = await tenant.Client.GetAsync($"{Art.Path}?type=goods");
        using var capitalised = await tenant.Client.GetAsync($"{Art.Path}?type=Stock");

        Assert.Equal(["s1", "s2"], stock.Codes());
        Assert.Equal(2, stock.Total());
        Assert.Equal(["v1"], service.Codes());
        Assert.Equal(1, service.Total());
        await HttpAssert.ValidationAsync(goods, "type");
        await HttpAssert.ValidationAsync(capitalised, "type");
    }

    [Fact]
    public async Task AC76_BaseUnitId_filter()
    {
        var tenant = await app.NewTenantAsync();
        var unit1 = await Art.UnitAsync(tenant.Client, "pcs", "Piece");
        var unit2 = await Art.UnitAsync(tenant.Client, "kg", "Kilogram");
        await Art.CreateAsync(tenant.Client, "a", "On one", unit1);
        await Art.CreateAsync(tenant.Client, "b", "On two", unit2);
        await Art.CreateAsync(tenant.Client, "c", "On one too", unit1);

        var onOne = await Art.ListAsync(tenant.Client, $"?baseUnitId={unit1}");
        var onTwo = await Art.ListAsync(tenant.Client, $"?baseUnitId={unit2}");
        var unknown = await Art.ListAsync(tenant.Client, $"?baseUnitId={Guid.CreateVersion7()}");
        using var malformed = await tenant.Client.GetAsync($"{Art.Path}?baseUnitId=abc");

        Assert.Equal(["a", "c"], onOne.Codes());
        Assert.Equal(2, onOne.Total());
        Assert.Equal(["b"], onTwo.Codes());
        Assert.Equal(1, onTwo.Total());
        Assert.Empty(unknown.Codes());
        Assert.Equal(0, unknown.Total());
        await HttpAssert.ValidationAsync(malformed, "baseUnitId");
    }

    [Fact]
    public async Task AC77_Filters_combine_with_and()
    {
        var tenant = await app.NewTenantAsync();
        var unit1 = await Art.UnitAsync(tenant.Client, "pcs", "Piece");
        var unit2 = await Art.UnitAsync(tenant.Client, "kg", "Kilogram");
        await Art.CreateAsync(tenant.Client, "match-1", "Steel bolt", unit1);
        await Art.CreateAsync(tenant.Client, "match-2", "Steel nut", unit1);
        await Art.CreateAsync(tenant.Client, "x-type", "Steel service", unit1, type: "service");
        await Art.CreateAsync(tenant.Client, "x-inactive", "Steel old", unit1, isActive: false);
        await Art.CreateAsync(tenant.Client, "x-unit", "Steel bar", unit2);
        await Art.CreateAsync(tenant.Client, "x-search", "Copper wire", unit1);

        var all = await Art.ListAsync(tenant.Client, $"?type=stock&isActive=true&baseUnitId={unit1}&search=steel");
        var paged = await Art.ListAsync(tenant.Client, $"?type=stock&isActive=true&baseUnitId={unit1}&search=steel&limit=1&offset=1");

        Assert.Equal(["match-1", "match-2"], all.Codes());
        Assert.Equal(2, all.Total());
        Assert.Equal(["match-2"], paged.Codes());
        Assert.Equal(2, paged.Total());
    }

    [Fact]
    public async Task AC78_Search_matches_code_or_name_but_not_description()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "B-8", "Steel bolt", unit);
        await Art.CreateAsync(tenant.Client, "N-8", "Steel nut", unit);
        await Art.CreateAsync(tenant.Client, "W-1", "Washer", unit, description: "bolt washer");

        var bolt = await Art.ListAsync(tenant.Client, "?search=BOLT");
        var steel = await Art.ListAsync(tenant.Client, "?search=steel");
        var eight = await Art.ListAsync(tenant.Client, "?search=-8");
        var none = await Art.ListAsync(tenant.Client, "?search=zzz");
        var empty = await Art.ListAsync(tenant.Client, "?search=");
        var unitName = await Art.ListAsync(tenant.Client, "?search=Piece");

        Assert.Equal(["B-8"], bolt.Codes());
        Assert.Equal(1, bolt.Total());
        Assert.Equal(["B-8", "N-8"], steel.Codes());
        Assert.Equal(["B-8", "N-8"], eight.Codes());
        Assert.Empty(none.Codes());
        Assert.Equal(0, none.Total());
        Assert.Equal(["B-8", "N-8", "W-1"], empty.Codes());
        Assert.Empty(unitName.Codes()); // the base unit's name is not searched either
    }

    [Fact]
    public async Task AC79_Search_wildcards_are_literal_and_length_is_limited()
    {
        var (tenant, _) = await TenantWithAsync(("c1", "100% cotton"), ("p1", "Plain"));

        var percent = await Art.ListAsync(tenant.Client, "?search=%25");
        var underscore = await Art.ListAsync(tenant.Client, "?search=_");
        var ok = await Art.ListAsync(tenant.Client, $"?search={new string('x', 100)}");
        using var tooLong = await tenant.Client.GetAsync($"{Art.Path}?search={new string('x', 101)}");

        Assert.Equal(["c1"], percent.Codes());
        Assert.Equal(1, percent.Total());
        Assert.Empty(underscore.Codes());
        Assert.Equal(0, ok.Total());
        await HttpAssert.ValidationAsync(tooLong, "search");
    }
}
