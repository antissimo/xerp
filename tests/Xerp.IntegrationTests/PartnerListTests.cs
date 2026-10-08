using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 004, AC-60 to AC-68: the partner list — ordering, paging, role and active filters, search.</summary>
[Collection(XerpCollection.Name)]
public class PartnerListTests(XerpFixture app)
{
    private static readonly MasterApi P = MasterApi.Partners;

    private static JsonObject Partner(string code, string name, bool customer, bool supplier, bool active = true) => new()
    {
        ["code"] = code, ["name"] = name, ["isCustomer"] = customer, ["isSupplier"] = supplier, ["isActive"] = active,
    };

    [Fact]
    public async Task AC60_List_of_a_new_tenant_is_the_empty_envelope()
    {
        var tenant = await app.NewTenantAsync();

        var list = await P.ListAsync(tenant.Client);

        Assert.Equal(["items", "limit", "offset", "total"], list.PropertyNames());
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(0, list.Total());
        Assert.Equal(50, list.GetProperty("limit").GetInt32());
        Assert.Equal(0, list.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task AC61_List_is_ordered_by_code_case_insensitively_and_items_equal_get()
    {
        var tenant = await app.NewTenantAsync();
        foreach (var code in new[] { "b", "A", "c" })
            await P.CreateAsync(tenant.Client, P.Full(code));

        var list = await P.ListAsync(tenant.Client);

        Assert.Equal(["A", "b", "c"], list.Codes());
        Assert.Equal(3, list.Total());
        foreach (var item in list.GetProperty("items").EnumerateArray())
            McpAssert.JsonEqual(await P.GetAsync(tenant.Client, item.Id()), item);
    }

    [Fact]
    public async Task AC62_Paging_with_limit_and_offset()
    {
        var tenant = await app.NewTenantAsync();
        foreach (var code in new[] { "b", "A", "c" })
            await P.CreateAsync(tenant.Client, code);

        var firstTwo = await P.ListAsync(tenant.Client, "?limit=2");
        var third = await P.ListAsync(tenant.Client, "?limit=2&offset=2");
        var beyond = await P.ListAsync(tenant.Client, "?offset=10");

        Assert.Equal(["A", "b"], firstTwo.Codes());
        Assert.Equal(3, firstTwo.Total());
        Assert.Equal(2, firstTwo.GetProperty("limit").GetInt32());
        Assert.Equal(["c"], third.Codes());
        Assert.Equal(3, third.Total());
        Assert.Equal(2, third.GetProperty("offset").GetInt32());
        Assert.Empty(beyond.Codes());
        Assert.Equal(3, beyond.Total());
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=501", "limit")]
    [InlineData("?limit=abc", "limit")]
    [InlineData("?offset=-1", "offset")]
    public async Task AC62_Out_of_range_paging_is_rejected_with_the_parameter_key(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(P.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Theory]
    [InlineData("?foo=1", "foo")]
    [InlineData("?country=HR", "country")]
    [InlineData("?taxId=1", "taxId")]
    [InlineData("?iscustomer=true", "iscustomer")]
    [InlineData("?search=a%00b", "search")]
    public async Task AC63_Unknown_query_parameter_or_control_character_in_search_is_rejected(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, "P1");

        using var response = await tenant.Client.GetAsync(P.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Fact]
    public async Task AC63_Unknown_query_parameter_on_get_and_too_long_search_are_rejected()
    {
        var tenant = await app.NewTenantAsync();
        var partner = await P.CreateAsync(tenant.Client, "P1");

        using var get = await tenant.Client.GetAsync($"{P.Path}/{partner.Id()}?x=1");
        using var longSearch = await tenant.Client.GetAsync($"{P.Path}?search={new string('a', 101)}");
        var maxSearch = await P.ListAsync(tenant.Client, $"?search={new string('a', 100)}");

        await HttpAssert.ValidationAsync(get, "x");
        await HttpAssert.ValidationAsync(longSearch, "search");
        Assert.Equal(0, maxSearch.Total());
    }

    [Fact]
    public async Task AC64_IsActive_filter_and_total()
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, Partner("on-1", "Active one", true, false));
        await P.CreateAsync(tenant.Client, Partner("on-2", "Active two", false, true));
        await P.CreateAsync(tenant.Client, Partner("off-1", "Retired", true, true, active: false));

        var active = await P.ListAsync(tenant.Client, "?isActive=true");
        var inactive = await P.ListAsync(tenant.Client, "?isActive=false");
        var all = await P.ListAsync(tenant.Client);
        using var invalid = await tenant.Client.GetAsync($"{P.Path}?isActive=maybe");

        Assert.Equal(["on-1", "on-2"], active.Codes());
        Assert.Equal(2, active.Total());
        Assert.Equal(["off-1"], inactive.Codes());
        Assert.Equal(1, inactive.Total());
        Assert.Equal(["off-1", "on-1", "on-2"], all.Codes());
        Assert.Equal(3, all.Total());
        await HttpAssert.ValidationAsync(invalid, "isActive");
    }

    [Theory]
    [InlineData("?isCustomer=true", "B,C")]
    [InlineData("?isSupplier=true", "B,S")]
    [InlineData("?isCustomer=true&isSupplier=true", "B")]
    [InlineData("?isCustomer=false", "S")]
    [InlineData("?isSupplier=false", "C")]
    [InlineData("?isCustomer=true&isSupplier=false", "C")]
    [InlineData("?isCustomer=false&isSupplier=true", "S")]
    [InlineData("?isCustomer=false&isSupplier=false", "")]
    [InlineData("", "B,C,S")]
    public async Task AC65_Role_filters_select_customers_suppliers_and_both(string query, string expected)
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, Partner("C", "Customer only", true, false));
        await P.CreateAsync(tenant.Client, Partner("S", "Supplier only", false, true));
        await P.CreateAsync(tenant.Client, Partner("B", "Both", true, true));

        var list = await P.ListAsync(tenant.Client, query);

        var codes = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(codes, list.Codes());
        Assert.Equal(codes.Length, list.Total());
    }

    [Theory]
    [InlineData("?isCustomer=yes", "isCustomer")]
    [InlineData("?isSupplier=1", "isSupplier")]
    [InlineData("?isCustomer=TRUE", "isCustomer")] // R18: exactly true or false
    public async Task AC65_Role_filter_accepts_exactly_true_or_false(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(P.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Theory]
    [InlineData("?search=STEEL", "A-1")] // name; the city "Steelville" of C-2 is not searched
    [InlineData("?search=-1", "A-1,B-1")] // code
    [InlineData("?search=de22", "B-1")] // tax id, case-insensitive
    [InlineData("?search=zzz", "")]
    [InlineData("?search=", "A-1,B-1,C-2")]
    [InlineData("?search=Zagreb", "")] // E: no address field is searched
    public async Task AC66_Search_matches_code_name_and_tax_id_but_not_the_address(string query, string expected)
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, P.Minimal("A-1", "Acme Steel").With("taxId", "HR111"));
        await P.CreateAsync(tenant.Client, P.Minimal("B-1", "Baltic Wood").With("taxId", "DE222").With("addressLine1", "Zagreb road 1"));
        await P.CreateAsync(tenant.Client, P.Minimal("C-2", "Cedar").With("city", "Steelville").With("region", "Zagreb county"));

        var list = await P.ListAsync(tenant.Client, query);

        var codes = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(codes, list.Codes());
        Assert.Equal(codes.Length, list.Total());
    }

    [Fact]
    public async Task AC67_Search_treats_percent_and_underscore_literally()
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, "P1", "100% cotton");
        await P.CreateAsync(tenant.Client, "P2", "Plain");

        var percent = await P.ListAsync(tenant.Client, "?search=%25");
        var underscore = await P.ListAsync(tenant.Client, "?search=_");

        Assert.Equal(["P1"], percent.Codes());
        Assert.Equal(1, percent.Total());
        Assert.Empty(underscore.Codes());
        Assert.Equal(0, underscore.Total());
    }

    [Fact]
    public async Task AC67_Wildcards_in_a_tax_id_search_are_literal_too()
    {
        // S3: the pattern is escaped for all three searched columns.
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, P.Minimal("P1", "First").With("taxId", "A_1"));
        await P.CreateAsync(tenant.Client, P.Minimal("P2", "Second").With("taxId", "AX1"));

        var list = await P.ListAsync(tenant.Client, "?search=a_1");

        Assert.Equal(["P1"], list.Codes());
    }

    [Fact]
    public async Task AC68_Filters_combine_with_and()
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, Partner("K-1", "Kappa match", true, false));
        await P.CreateAsync(tenant.Client, Partner("K-2", "Kappa supplier", false, true));
        await P.CreateAsync(tenant.Client, Partner("K-3", "Kappa retired", true, false, active: false));
        await P.CreateAsync(tenant.Client, Partner("L-1", "Lambda", true, false));
        await P.CreateAsync(tenant.Client, Partner("K-4", "Kappa both", true, true));

        var list = await P.ListAsync(tenant.Client, "?isCustomer=true&isActive=true&search=kappa");
        var paged = await P.ListAsync(tenant.Client, "?isCustomer=true&isActive=true&search=kappa&limit=1&offset=1");

        Assert.Equal(["K-1", "K-4"], list.Codes());
        Assert.Equal(2, list.Total());
        Assert.Equal(["K-4"], paged.Codes());
        Assert.Equal(2, paged.Total());
    }
}
