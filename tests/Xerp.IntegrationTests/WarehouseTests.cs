using System.Net;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 004, AC-70 to AC-85: warehouses over HTTP — a master with code, name and an optional address.</summary>
[Collection(XerpCollection.Name)]
public class WarehouseTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    // Spec 004, 4.2: all properties are always present; no partner properties. Spec 011, 4.1: isDefault.
    private static readonly string[] Representation =
    [
        "addressLine1", "addressLine2", "city", "code", "countryCode", "createdAt", "createdBy", "id", "isActive",
        "isDefault", "name", "postalCode", "region", "updatedAt", "updatedBy",
    ];

    /// <summary>The number of warehouses, the default one every tenant is created with included (spec 011, R1).</summary>
    private async Task<int> TotalAsync(TestTenant tenant) => (await W.ListAsync(tenant.Client)).Total();

    private static JsonObject Changed(string code) => new()
    {
        ["code"] = code, ["name"] = "Transit store", ["addressLine1"] = "Hafenstrasse 9", ["addressLine2"] = "Gate 3",
        ["postalCode"] = "20457", ["city"] = "Hamburg", ["region"] = "Hamburg", ["countryCode"] = "DE",
        ["isActive"] = false,
    };

    [Fact]
    public async Task AC70_Create_with_code_and_name_returns_201_with_defaults()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await W.PostAsync(tenant.Client, W.Minimal("WH-1", "Main warehouse"));

        var warehouse = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.NotEqual(Guid.Empty, warehouse.Id());
        Assert.Equal("WH-1", warehouse.Str("code"));
        Assert.Equal("Main warehouse", warehouse.Str("name"));
        JsonBody.AssertNull(warehouse, MasterApi.AddressFields);
        Assert.True(warehouse.Bool("isActive"));
        Assert.Equal(warehouse.Str("createdAt"), warehouse.Str("updatedAt"));
        Assert.Equal(tenant.ApiKeyId, warehouse.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, warehouse.GetProperty("updatedBy").GetGuid());
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/v1/warehouses/{warehouse.Id()}", response.Headers.Location!.ToString());
        foreach (var absent in new[] { "tenantId", "isCustomer", "isSupplier", "taxId" })
            Assert.False(warehouse.TryGetProperty(absent, out _), $"A warehouse has no '{absent}'.");
        Assert.Equal(Representation, warehouse.PropertyNames());
    }

    [Fact]
    public async Task AC71_Create_with_address_and_inactive_then_get_by_id_and_by_code_in_any_case()
    {
        var tenant = await app.NewTenantAsync();
        var body = W.Full("Wh-Main").With("isActive", false);

        var created = await W.CreateAsync(tenant.Client, body);

        JsonBody.AssertHasValues(body, created);
        McpAssert.JsonEqual(created, await W.GetAsync(tenant.Client, created.Id()));
        foreach (var spelling in new[] { "Wh-Main", "WH-MAIN", "wh-main" })
        {
            using var response = await W.ByCodeAsync(tenant.Client, spelling);
            var byCode = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
            Assert.Equal(created.Id(), byCode.Id());
            Assert.Equal("Wh-Main", byCode.Str("code"));
        }
    }

    [Fact]
    public async Task AC72_Code_name_and_address_are_trimmed()
    {
        var tenant = await app.NewTenantAsync();
        var body = new JsonObject
        {
            ["code"] = "  W1  ", ["name"] = "  Main  ", ["addressLine1"] = "  Dock 1  ", ["addressLine2"] = "  Hall B  ",
            ["postalCode"] = "  21000  ", ["city"] = "  Split  ", ["region"] = "  Dalmatia  ", ["countryCode"] = " HR ",
        };

        var created = await W.CreateAsync(tenant.Client, body);

        var expected = new JsonObject
        {
            ["code"] = "W1", ["name"] = "Main", ["addressLine1"] = "Dock 1", ["addressLine2"] = "Hall B",
            ["postalCode"] = "21000", ["city"] = "Split", ["region"] = "Dalmatia", ["countryCode"] = "HR",
        };
        JsonBody.AssertHasValues(expected, created);
        JsonBody.AssertHasValues(expected, await W.GetAsync(tenant.Client, created.Id()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AC72_Empty_blank_and_null_address_fields_all_mean_no_value(string? value)
    {
        var tenant = await app.NewTenantAsync();
        var body = W.Minimal("W1");
        foreach (var field in MasterApi.AddressFields)
            body.With(field, value);

        var created = await W.CreateAsync(tenant.Client, body);

        JsonBody.AssertNull(created, MasterApi.AddressFields);
        JsonBody.AssertNull(await W.GetAsync(tenant.Client, created.Id()), MasterApi.AddressFields);
    }

    [Fact]
    public async Task AC72_Address_fields_are_independent()
    {
        // R5: any subset may be filled; none requires another.
        var tenant = await app.NewTenantAsync();

        var cityOnly = await W.CreateAsync(tenant.Client, W.Minimal("W1").With("city", "Rijeka"));
        var countryOnly = await W.CreateAsync(tenant.Client, W.Minimal("W2").With("countryCode", "SI"));

        Assert.Equal("Rijeka", cityOnly.Str("city"));
        JsonBody.AssertNull(cityOnly, "addressLine1", "addressLine2", "postalCode", "region", "countryCode");
        Assert.Equal("SI", countryOnly.Str("countryCode"));
        JsonBody.AssertNull(countryOnly, "addressLine1", "addressLine2", "postalCode", "city", "region");
    }

    [Fact]
    public async Task AC73_Maximum_lengths_are_accepted()
    {
        var tenant = await app.NewTenantAsync();
        var body = new JsonObject
        {
            ["code"] = new string('c', 50), ["name"] = new string('n', 200), ["addressLine1"] = new string('a', 200),
            ["addressLine2"] = new string('b', 200), ["postalCode"] = new string('1', 20),
            ["city"] = new string('y', 100), ["region"] = new string('r', 100),
        };

        var created = await W.CreateAsync(tenant.Client, body);

        JsonBody.AssertHasValues(body, created);
    }

    [Theory]
    [InlineData("code", 50)]
    [InlineData("name", 200)]
    [InlineData("addressLine1", 200)]
    [InlineData("addressLine2", 200)]
    [InlineData("postalCode", 20)]
    [InlineData("city", 100)]
    [InlineData("region", 100)]
    public async Task AC73_One_character_over_the_maximum_is_rejected_with_the_field_key(string field, int max)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await W.PostAsync(tenant.Client, W.Minimal("W1").With(field, new string('x', max + 1)));

        await HttpAssert.ValidationAsync(response, field);
        Assert.Equal(1, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("""{ "name": "Main" }""", "code")]
    [InlineData("""{ "code": null, "name": "Main" }""", "code")]
    [InlineData("""{ "code": "", "name": "Main" }""", "code")]
    [InlineData("""{ "code": "   ", "name": "Main" }""", "code")]
    [InlineData("""{ "code": "a b", "name": "Main" }""", "code")]
    [InlineData("""{ "code": "W1" }""", "name")]
    [InlineData("""{ "code": "W1", "name": null }""", "name")]
    [InlineData("""{ "code": "W1", "name": "" }""", "name")]
    [InlineData("""{ "code": "W1", "name": "   " }""", "name")]
    [InlineData("""{ "code": "W1", "name": "a\nb" }""", "name")]
    [InlineData("""{ "code": "W1", "name": "Main", "countryCode": "hr" }""", "countryCode")]
    [InlineData("""{ "code": "W1", "name": "Main", "countryCode": "HRV" }""", "countryCode")]
    [InlineData("""{ "code": "W1", "name": "Main", "city": "a\u0000b" }""", "city")]
    [InlineData("""{ "code": "W1", "name": "Main", "city": "a\nb" }""", "city")]
    public async Task AC74_Invalid_field_is_rejected_with_its_key(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(W.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
        Assert.Equal(1, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC74_Empty_object_reports_code_and_name_together()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(W.Path, HttpAssert.Raw("{}"));

        await HttpAssert.ValidationAsync(response, "code", "name");
    }

    [Theory]
    [InlineData("""{ "code": "W1", "name": "Main", "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "W1", "name": "Main", "tenantId": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "W1", "name": "Main", "isCustomer": true }""")]
    [InlineData("""{ "code": "W1", "name": "Main", "taxId": "HR1" }""")]
    [InlineData("""{ "code": "W1", "name": "Main", "foo": 1 }""")]
    [InlineData("""{ "code": "W1", "name": "Main" """)]
    public async Task AC75_Unknown_property_or_malformed_json_is_rejected(string json)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(W.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response);
        Assert.Equal(1, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC76_Duplicate_code_in_any_case_is_taken()
    {
        var tenant = await app.NewTenantAsync();
        await W.CreateAsync(tenant.Client, "WH-1", "First");

        using var same = await W.PostAsync(tenant.Client, W.Minimal("WH-1", "Again"));
        using var lower = await W.PostAsync(tenant.Client, W.Minimal("wh-1", "Lower"));

        await HttpAssert.CodeTakenAsync(same);
        await HttpAssert.CodeTakenAsync(lower);
        var list = await W.ListAsync(tenant.Client);
        Assert.Equal(["CENTRAL", "WH-1"], list.Codes());
        Assert.Equal("First", list.GetProperty("items")[1].Str("name"));
    }

    [Fact]
    public async Task AC76_Ten_parallel_creates_of_one_code_give_one_201_and_nine_409()
    {
        var tenant = await app.NewTenantAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => W.PostAsync(tenant.Client, W.Minimal(i % 2 == 0 ? "race" : "RACE", $"Racer {i}"))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await HttpAssert.CodeTakenAsync(conflict);
        Assert.Equal(2, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC77_Replace_changes_every_field_and_keeps_identity_and_creation_audit()
    {
        var tenant = await app.NewTenantAsync();
        var created = await W.CreateAsync(tenant.Client, "W-OLD", "Main");
        var body = Changed("W-NEW");

        var replaced = await W.ReplaceAsync(tenant.Client, created.Id(), body);

        JsonBody.AssertHasValues(body, replaced);
        Assert.Equal(created.Id(), replaced.Id());
        Assert.Equal(created.Str("createdAt"), replaced.Str("createdAt"));
        Assert.Equal(created.Str("createdBy"), replaced.Str("createdBy"));
        Assert.Equal(Representation, replaced.PropertyNames());
        McpAssert.JsonEqual(replaced, await W.GetAsync(tenant.Client, created.Id()));
        using var oldCode = await W.ByCodeAsync(tenant.Client, "W-OLD");
        await HttpAssert.NotFoundAsync(oldCode);
    }

    public static TheoryData<string> WarehouseFields() => new(MasterApi.Warehouses.ReplaceFields);

    [Theory]
    [MemberData(nameof(WarehouseFields))]
    public async Task AC78_Replace_without_one_of_the_nine_fields_is_rejected_and_changes_nothing(string field)
    {
        var tenant = await app.NewTenantAsync();
        var created = await W.CreateAsync(tenant.Client, W.Full("W1"));

        using var response = await W.PutAsync(tenant.Client, created.Id(), Changed("W2").Without(field));

        await HttpAssert.ValidationAsync(response, field);
        await W.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC78_Replace_with_null_clears_the_address()
    {
        var tenant = await app.NewTenantAsync();
        var created = await W.CreateAsync(tenant.Client, W.Full("W1"));
        var body = W.Full("W1");
        foreach (var field in MasterApi.AddressFields)
            body.With(field, null);

        var replaced = await W.ReplaceAsync(tenant.Client, created.Id(), body);

        JsonBody.AssertNull(replaced, MasterApi.AddressFields);
        JsonBody.AssertNull(await W.GetAsync(tenant.Client, created.Id()), MasterApi.AddressFields);
    }

    [Fact]
    public async Task AC79_Replace_may_keep_its_own_code_or_change_only_its_letter_case()
    {
        var tenant = await app.NewTenantAsync();
        var created = await W.CreateAsync(tenant.Client, "Abc-1");

        var kept = await W.ReplaceAsync(tenant.Client, created.Id(), Changed("Abc-1"));
        var recased = await W.ReplaceAsync(tenant.Client, created.Id(), Changed("ABC-1"));

        Assert.Equal("Abc-1", kept.Str("code"));
        Assert.Equal("ABC-1", recased.Str("code"));
        Assert.Equal(2, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("OTHER")]
    public async Task AC79_Replace_with_the_code_of_another_warehouse_is_taken_and_changes_nothing(string code)
    {
        var tenant = await app.NewTenantAsync();
        await W.CreateAsync(tenant.Client, "Other");
        var created = await W.CreateAsync(tenant.Client, W.Full("Mine"));

        using var response = await W.PutAsync(tenant.Client, created.Id(), Changed(code));

        await HttpAssert.CodeTakenAsync(response);
        await W.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC80_Unknown_warehouse_is_not_found_and_validation_precedes_existence()
    {
        var tenant = await app.NewTenantAsync();
        var id = Guid.NewGuid();

        using var put = await W.PutAsync(tenant.Client, id, W.Full("W1"));
        using var get = await tenant.Client.GetAsync($"{W.Path}/{id}");
        using var delete = await W.DeleteAsync(tenant.Client, id);
        using var invalidPut = await W.PutAsync(tenant.Client, id, W.Full("W1").With("name", ""));
        using var notUuid = await tenant.Client.GetAsync($"{W.Path}/not-a-uuid");
        using var byCode = await W.ByCodeAsync(tenant.Client, "nope");

        await HttpAssert.NotFoundAsync(put);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.ValidationAsync(invalidPut, "name");
        await HttpAssert.NotFoundAsync(notUuid);
        await HttpAssert.NotFoundAsync(byCode);
        Assert.Equal(1, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC81_Delete_removes_the_warehouse_and_frees_its_code()
    {
        var tenant = await app.NewTenantAsync();
        var created = await W.CreateAsync(tenant.Client, "W1");

        using var first = await W.DeleteAsync(tenant.Client, created.Id());
        using var get = await tenant.Client.GetAsync($"{W.Path}/{created.Id()}");
        using var second = await W.DeleteAsync(tenant.Client, created.Id());
        var again = await W.CreateAsync(tenant.Client, "W1");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Empty(await first.Content.ReadAsByteArrayAsync());
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(second);
        Assert.NotEqual(created.Id(), again.Id());
        Assert.Equal(2, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC82_List_envelope_ordering_and_paging()
    {
        var tenant = await app.NewTenantAsync();

        var empty = await W.ListAsync(tenant.Client);
        foreach (var code in new[] { "b", "A", "c" })
            await W.CreateAsync(tenant.Client, code);
        var all = await W.ListAsync(tenant.Client);
        var third = await W.ListAsync(tenant.Client, "?limit=2&offset=2");

        // Spec 011, R1: "empty" is "only the default warehouse".
        Assert.Equal(["items", "limit", "offset", "total"], empty.PropertyNames());
        Assert.Equal(["CENTRAL"], empty.Codes());
        Assert.Equal(1, empty.Total());
        Assert.Equal(50, empty.GetProperty("limit").GetInt32());
        Assert.Equal(0, empty.GetProperty("offset").GetInt32());
        Assert.Equal(["A", "b", "c", "CENTRAL"], all.Codes());
        Assert.Equal(4, all.Total());
        Assert.Equal(["c", "CENTRAL"], third.Codes());
        Assert.Equal(4, third.Total());
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=501", "limit")]
    [InlineData("?offset=-1", "offset")]
    public async Task AC82_Out_of_range_paging_is_rejected_with_the_parameter_key(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.GetAsync(W.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Fact]
    public async Task AC83_IsActive_filter_and_total()
    {
        var tenant = await app.NewTenantAsync();
        await W.CreateAsync(tenant.Client, W.Minimal("on-1"));
        await W.CreateAsync(tenant.Client, W.Minimal("on-2"));
        await W.CreateAsync(tenant.Client, W.Minimal("off-1").With("isActive", false));

        var active = await W.ListAsync(tenant.Client, "?isActive=true");
        var inactive = await W.ListAsync(tenant.Client, "?isActive=false");
        var all = await W.ListAsync(tenant.Client);
        using var invalid = await tenant.Client.GetAsync($"{W.Path}?isActive=maybe");

        Assert.Equal(["CENTRAL", "on-1", "on-2"], active.Codes());
        Assert.Equal(3, active.Total());
        Assert.Equal(["off-1"], inactive.Codes());
        Assert.Equal(1, inactive.Total());
        Assert.Equal(4, all.Total());
        await HttpAssert.ValidationAsync(invalid, "isActive");
    }

    [Theory]
    [InlineData("?search=MAIN", "W-1")] // name, case-insensitive
    [InlineData("?search=w-", "W-1,W-2")] // code, case-insensitive
    [InlineData("?search=rijeka", "")] // city is not searched
    [InlineData("?search=%25", "X-3")] // a literal percent sign only
    [InlineData("?search=_", "")]
    [InlineData("?search=main&isActive=false", "")]
    [InlineData("?search=", "CENTRAL,W-1,W-2,X-3")]
    public async Task AC83_Search_matches_code_and_name_but_not_the_address(string query, string expectedCodes)
    {
        var tenant = await app.NewTenantAsync();
        await W.CreateAsync(tenant.Client, W.Minimal("W-1", "Main store").With("city", "Rijeka"));
        await W.CreateAsync(tenant.Client, W.Minimal("W-2", "Transit").With("addressLine1", "Rijeka port"));
        await W.CreateAsync(tenant.Client, W.Minimal("X-3", "50% reserve"));

        var list = await W.ListAsync(tenant.Client, query);

        var codes = expectedCodes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(codes, list.Codes());
        Assert.Equal(codes.Length, list.Total());
    }

    [Theory]
    [InlineData("?foo=1", "foo")]
    [InlineData("?isCustomer=true", "isCustomer")]
    [InlineData("?search=a%00b", "search")]
    public async Task AC84_Unknown_query_parameter_or_control_character_in_search_is_rejected(string query, string errorKey)
    {
        var tenant = await app.NewTenantAsync();
        await W.CreateAsync(tenant.Client, "W1");

        using var response = await tenant.Client.GetAsync(W.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Fact]
    public async Task AC85_Replace_by_a_second_key_sets_updatedBy_and_keeps_createdBy()
    {
        var tenant = await app.NewTenantAsync();
        var k2 = await Keys.CreateAsync(app, tenant.Client, "second", "human");
        var created = await W.CreateAsync(tenant.Client, "W1");

        var replaced = await W.ReplaceAsync(k2.Client, created.Id(), Changed("W1"));

        Assert.Equal(tenant.ApiKeyId, replaced.GetProperty("createdBy").GetGuid());
        Assert.Equal(k2.Id, replaced.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(replaced, await W.GetAsync(tenant.Client, created.Id()));
    }
}
