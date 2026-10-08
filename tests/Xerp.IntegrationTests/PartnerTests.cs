using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 004, AC-20 to AC-50: partners over HTTP — create, read, replace, delete. Roles (R14, R15), optional
/// text (R3), country code (R4), tax id (R16) and the order of checks (R11).
/// </summary>
[Collection(XerpCollection.Name)]
public class PartnerTests(XerpFixture app)
{
    private static readonly MasterApi P = MasterApi.Partners;

    private static readonly string[] OptionalText =
        ["taxId", "addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    // Spec 004, 4.1: all properties are always present.
    private static readonly string[] Representation =
    [
        "addressLine1", "addressLine2", "city", "code", "countryCode", "createdAt", "createdBy", "id", "isActive",
        "isCustomer", "isSupplier", "name", "postalCode", "region", "taxId", "updatedAt", "updatedBy",
    ];

    private async Task<int> TotalAsync(TestTenant tenant) => (await P.ListAsync(tenant.Client)).Total();

    // ---- create and read ----

    [Fact]
    public async Task AC20_Create_with_code_name_and_one_role_returns_201_with_defaults()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await P.PostAsync(tenant.Client, P.Minimal("P-001", "Acme d.o.o."));

        var partner = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.NotEqual(Guid.Empty, partner.Id());
        Assert.Equal("P-001", partner.Str("code"));
        Assert.Equal("Acme d.o.o.", partner.Str("name"));
        Assert.True(partner.Bool("isCustomer"));
        Assert.False(partner.Bool("isSupplier"));
        JsonBody.AssertNull(partner, OptionalText);
        Assert.True(partner.Bool("isActive"));
        Assert.Equal(partner.Str("createdAt"), partner.Str("updatedAt"));
        Assert.Equal(tenant.ApiKeyId, partner.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, partner.GetProperty("updatedBy").GetGuid());
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/v1/partners/{partner.Id()}", response.Headers.Location!.ToString());
        Assert.False(partner.TryGetProperty("tenantId", out _));
        Assert.Equal(Representation, partner.PropertyNames());
    }

    [Fact]
    public async Task AC21_Create_with_every_field_then_get_by_id_and_by_code_in_any_case()
    {
        var tenant = await app.NewTenantAsync();
        var body = P.Full("Cust-001").With("isActive", false);

        var created = await P.CreateAsync(tenant.Client, body);

        JsonBody.AssertHasValues(body, created);
        McpAssert.JsonEqual(created, await P.GetAsync(tenant.Client, created.Id()));
        foreach (var spelling in new[] { "Cust-001", "CUST-001", "cust-001" })
        {
            using var response = await P.ByCodeAsync(tenant.Client, spelling);
            var byCode = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
            Assert.Equal(created.Id(), byCode.Id());
            Assert.Equal("Cust-001", byCode.Str("code"));
        }
    }

    [Fact]
    public async Task AC22_Code_name_and_optional_text_are_trimmed()
    {
        var tenant = await app.NewTenantAsync();
        var body = P.Minimal("  P1  ", "  Acme  ")
            .With("taxId", "  X1  ").With("city", "  Split  ").With("countryCode", " HR ");

        var created = await P.CreateAsync(tenant.Client, body);

        var stored = await P.GetAsync(tenant.Client, created.Id());
        foreach (var partner in new[] { created, stored })
        {
            Assert.Equal("P1", partner.Str("code"));
            Assert.Equal("Acme", partner.Str("name"));
            Assert.Equal("X1", partner.Str("taxId"));
            Assert.Equal("Split", partner.Str("city"));
            Assert.Equal("HR", partner.Str("countryCode"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AC23_Empty_blank_and_null_optional_text_all_mean_no_value(string? value)
    {
        var tenant = await app.NewTenantAsync();
        var body = P.Minimal("P1");
        foreach (var field in OptionalText)
            body.With(field, value);

        var created = await P.CreateAsync(tenant.Client, body);

        JsonBody.AssertNull(created, OptionalText);
        JsonBody.AssertNull(await P.GetAsync(tenant.Client, created.Id()), OptionalText);
    }

    [Fact]
    public async Task AC24_Maximum_lengths_are_accepted()
    {
        var tenant = await app.NewTenantAsync();
        var body = new JsonObject
        {
            ["code"] = new string('c', 50), ["name"] = new string('n', 200), ["isCustomer"] = true,
            ["taxId"] = new string('t', 50), ["addressLine1"] = new string('a', 200),
            ["addressLine2"] = new string('b', 200), ["postalCode"] = new string('1', 20),
            ["city"] = new string('y', 100), ["region"] = new string('r', 100),
        };

        var created = await P.CreateAsync(tenant.Client, body);

        JsonBody.AssertHasValues(body, created);
    }

    [Theory]
    [InlineData("code", 50)]
    [InlineData("name", 200)]
    [InlineData("taxId", 50)]
    [InlineData("addressLine1", 200)]
    [InlineData("addressLine2", 200)]
    [InlineData("postalCode", 20)]
    [InlineData("city", 100)]
    [InlineData("region", 100)]
    public async Task AC24_One_character_over_the_maximum_is_rejected_with_the_field_key(string field, int max)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await P.PostAsync(tenant.Client, P.Minimal("P1").With(field, new string('x', max + 1)));

        await HttpAssert.ValidationAsync(response, field);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("""{ "name": "Acme", "isCustomer": true }""", "code")]
    [InlineData("""{ "code": null, "name": "Acme", "isCustomer": true }""", "code")]
    [InlineData("""{ "code": "", "name": "Acme", "isCustomer": true }""", "code")]
    [InlineData("""{ "code": "   ", "name": "Acme", "isCustomer": true }""", "code")]
    [InlineData("""{ "code": "a b", "name": "Acme", "isCustomer": true }""", "code")]
    [InlineData("""{ "code": "a/b", "name": "Acme", "isCustomer": true }""", "code")]
    [InlineData("""{ "code": "P1", "isCustomer": true }""", "name")]
    [InlineData("""{ "code": "P1", "name": null, "isCustomer": true }""", "name")]
    [InlineData("""{ "code": "P1", "name": "", "isCustomer": true }""", "name")]
    [InlineData("""{ "code": "P1", "name": "   ", "isCustomer": true }""", "name")]
    public async Task AC25_Invalid_code_or_name_is_rejected_with_the_field_key(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(P.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("""{ "code": "P1", "name": "Acme" }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": false, "isSupplier": false }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isSupplier": false }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": false }""")]
    public async Task AC26_A_partner_without_a_role_is_rejected_under_both_role_keys(string json)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(P.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, "isCustomer", "isSupplier");
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC26_Supplier_only_and_both_roles_are_accepted()
    {
        var tenant = await app.NewTenantAsync();

        var supplier = await P.CreateAsync(tenant.Client,
            new JsonObject { ["code"] = "S1", ["name"] = "Supplier", ["isSupplier"] = true });
        var both = await P.CreateAsync(tenant.Client,
            new JsonObject { ["code"] = "B1", ["name"] = "Both", ["isCustomer"] = true, ["isSupplier"] = true });

        Assert.False(supplier.Bool("isCustomer"));
        Assert.True(supplier.Bool("isSupplier"));
        Assert.True(both.Bool("isCustomer"));
        Assert.True(both.Bool("isSupplier"));
    }

    [Theory]
    [InlineData("""{ "code": "P1", "name": "Acme", "isSupplier": true, "isCustomer": null }""", "isCustomer")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isSupplier": true, "isCustomer": "true" }""", "isCustomer")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isSupplier": true, "isCustomer": 1 }""", "isCustomer")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "isActive": "yes" }""", "isActive")]
    public async Task AC27_A_boolean_of_the_wrong_json_type_is_rejected_with_the_property_key(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(P.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC28_Empty_object_reports_code_name_and_both_roles_together()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(P.Path, HttpAssert.Raw("{}"));

        await HttpAssert.ValidationAsync(response, "code", "name", "isCustomer", "isSupplier");
    }

    [Theory]
    [InlineData("hr")]
    [InlineData("H")]
    [InlineData("HRV")]
    [InlineData("H1")]
    [InlineData("12")]
    [InlineData("Croatia")]
    [InlineData("Hr")]
    [InlineData("ÅB")] // E6: upper-case letters, but not ASCII
    public async Task AC29_Country_code_that_is_not_two_upper_case_ascii_letters_is_rejected(string countryCode)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await P.PostAsync(tenant.Client, P.Minimal("P1").With("countryCode", countryCode));

        await HttpAssert.ValidationAsync(response, "countryCode");
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC29_Country_code_is_not_checked_against_a_list_of_countries()
    {
        var tenant = await app.NewTenantAsync();

        var created = await P.CreateAsync(tenant.Client, P.Minimal("P1").With("countryCode", "XX"));

        Assert.Equal("XX", created.Str("countryCode"));
    }

    public static TheoryData<string, string> ControlCharacterCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in new[] { "name", "taxId", "addressLine1", "city" })
            foreach (var character in new[] { "nul", "lf", "tab" }) // tab: E5
                data.Add(field, character);
        return data;
    }

    [Theory]
    [MemberData(nameof(ControlCharacterCases))]
    public async Task AC30_Control_characters_are_rejected_with_the_field_key(string field, string character)
    {
        var tenant = await app.NewTenantAsync();
        var value = ControlText.Of(character);

        using var response = await P.PostAsync(tenant.Client, P.Minimal("P1").With(field, value));

        await HttpAssert.ValidationAsync(response, field);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "taxId": 123 }""", "taxId")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "city": { "name": "Zagreb" } }""", "city")] // E5
    public async Task AC30_Optional_text_of_the_wrong_json_type_is_rejected_with_the_field_key(string json, string errorKey)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(P.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response, errorKey);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC31_Tax_id_has_no_format_and_is_not_unique()
    {
        var tenant = await app.NewTenantAsync();

        var spaced = await P.CreateAsync(tenant.Client, P.Minimal("P1").With("taxId", "HR 123"));
        var odd = await P.CreateAsync(tenant.Client, P.Minimal("P2").With("taxId", "äö-/.123"));
        var branch = await P.CreateAsync(tenant.Client, P.Minimal("P3").With("taxId", "HR 123"));

        Assert.Equal("HR 123", spaced.Str("taxId"));
        Assert.Equal("äö-/.123", odd.Str("taxId"));
        Assert.Equal("HR 123", branch.Str("taxId"));
        Assert.NotEqual(spaced.Id(), branch.Id());
        Assert.Equal("äö-/.123", (await P.GetAsync(tenant.Client, odd.Id())).Str("taxId"));
        Assert.Equal(3, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "id": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "tenantId": "0199c0de-0000-7000-8000-000000000001" }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "address": { "city": "Zagreb" } }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "email": "a@example.com" }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "foo": 1 }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "IsCustomer": true }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true, "IsCustomer": true }""")]
    [InlineData("""{ "code": "P1", "name": "Acme", "isCustomer": true """)]
    [InlineData("")]
    public async Task AC32_Unknown_property_malformed_json_or_empty_body_is_rejected(string json)
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.PostAsync(P.Path, HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(response);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC33_Duplicate_code_in_any_case_is_taken()
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, "P-1", "First");

        using var same = await P.PostAsync(tenant.Client, P.Minimal("P-1", "Again"));
        using var lower = await P.PostAsync(tenant.Client, P.Minimal("p-1", "Lower"));

        await HttpAssert.CodeTakenAsync(same);
        await HttpAssert.CodeTakenAsync(lower);
        var list = await P.ListAsync(tenant.Client);
        Assert.Equal(["P-1"], list.Codes());
        Assert.Equal("First", list.GetProperty("items")[0].Str("name"));
    }

    [Fact]
    public async Task AC33_Ten_parallel_creates_of_one_code_give_one_201_and_nine_409()
    {
        var tenant = await app.NewTenantAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => P.PostAsync(tenant.Client, P.Minimal(i % 2 == 0 ? "race" : "RACE", $"Racer {i}"))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await HttpAssert.CodeTakenAsync(conflict);
        Assert.Equal(1, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC34_Unit_article_partner_and_warehouse_codes_are_separate_namespaces()
    {
        var tenant = await app.NewTenantAsync();

        var unit = await Uom.CreateAsync(tenant.Client, "X-1", "Unit");
        var article = await Art.CreateAsync(tenant.Client, "X-1", "Article", unit.Id());
        var partner = await P.CreateAsync(tenant.Client, "X-1", "Partner");
        var warehouse = await MasterApi.Warehouses.CreateAsync(tenant.Client, "X-1", "Warehouse");

        foreach (var (path, record) in new[]
                 {
                     (Uom.Path, unit), (Art.Path, article), (P.Path, partner), (MasterApi.Warehouses.Path, warehouse),
                 })
        {
            using var response = await tenant.Client.GetAsync($"{path}/by-code/X-1");
            McpAssert.JsonEqual(record, await HttpAssert.JsonAsync(response, HttpStatusCode.OK), $"{path}/by-code/X-1");
        }
        Assert.Equal(4, new[] { unit.Id(), article.Id(), partner.Id(), warehouse.Id() }.Distinct().Count());
    }

    // ---- replace ----

    private static JsonObject Changed(string code) => new()
    {
        ["code"] = code, ["name"] = "Beta GmbH", ["isCustomer"] = false, ["isSupplier"] = true,
        ["taxId"] = "DE999", ["addressLine1"] = "Hauptstrasse 5", ["addressLine2"] = "Hof",
        ["postalCode"] = "80331", ["city"] = "München", ["region"] = "Bayern", ["countryCode"] = "DE",
        ["isActive"] = false,
    };

    [Fact]
    public async Task AC40_Replace_changes_every_field_and_keeps_identity_and_creation_audit()
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, "P-OLD", "Acme");
        var body = Changed("P-NEW");

        var replaced = await P.ReplaceAsync(tenant.Client, created.Id(), body);

        JsonBody.AssertHasValues(body, replaced);
        Assert.Equal(created.Id(), replaced.Id());
        Assert.Equal(created.Str("createdAt"), replaced.Str("createdAt"));
        Assert.Equal(created.Str("createdBy"), replaced.Str("createdBy"));
        Assert.True(replaced.GetProperty("updatedAt").GetDateTimeOffset() >= created.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(Representation, replaced.PropertyNames());
        McpAssert.JsonEqual(replaced, await P.GetAsync(tenant.Client, created.Id()));
        using var oldCode = await P.ByCodeAsync(tenant.Client, "P-OLD");
        await HttpAssert.NotFoundAsync(oldCode);
        using var newCode = await P.ByCodeAsync(tenant.Client, "P-NEW");
        Assert.Equal(created.Id(), (await HttpAssert.JsonAsync(newCode, HttpStatusCode.OK)).Id());
    }

    public static TheoryData<string> PartnerFields() => new(MasterApi.Partners.ReplaceFields);

    [Theory]
    [MemberData(nameof(PartnerFields))]
    public async Task AC41_Replace_without_one_of_the_twelve_fields_is_rejected_and_changes_nothing(string field)
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, P.Full("P1"));

        using var response = await P.PutAsync(tenant.Client, created.Id(), Changed("P2").Without(field));

        await HttpAssert.ValidationAsync(response, field);
        await P.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC42_Replace_with_null_clears_tax_id_and_address()
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, P.Full("P1"));
        var body = P.Full("P1");
        foreach (var field in OptionalText)
            body.With(field, null);

        var replaced = await P.ReplaceAsync(tenant.Client, created.Id(), body);

        JsonBody.AssertNull(replaced, OptionalText);
        JsonBody.AssertNull(await P.GetAsync(tenant.Client, created.Id()), OptionalText);
        Assert.Equal("P1", replaced.Str("code"));
    }

    [Fact]
    public async Task AC43_Replace_may_keep_its_own_code_or_change_only_its_letter_case()
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, "Abc-1");

        var kept = await P.ReplaceAsync(tenant.Client, created.Id(), Changed("Abc-1"));
        var recased = await P.ReplaceAsync(tenant.Client, created.Id(), Changed("ABC-1"));

        Assert.Equal("Abc-1", kept.Str("code"));
        Assert.Equal("ABC-1", recased.Str("code"));
        Assert.Equal("ABC-1", (await P.GetAsync(tenant.Client, created.Id())).Str("code"));
        Assert.Equal(1, await TotalAsync(tenant));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("OTHER")]
    [InlineData("  other ")]
    public async Task AC43_Replace_with_the_code_of_another_partner_is_taken_and_changes_nothing(string code)
    {
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, "Other");
        var created = await P.CreateAsync(tenant.Client, P.Full("Mine"));

        using var response = await P.PutAsync(tenant.Client, created.Id(), Changed(code));

        await HttpAssert.CodeTakenAsync(response);
        await P.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC44_Replace_cannot_remove_the_last_role()
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, P.Full("P1"));

        using var response = await P.PutAsync(tenant.Client, created.Id(),
            P.Full("P1").With("isCustomer", false).With("isSupplier", false));

        await HttpAssert.ValidationAsync(response, "isCustomer", "isSupplier");
        await P.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC44_Replace_turns_a_customer_into_a_supplier_only()
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, "P1");
        Assert.True(created.Bool("isCustomer"));
        Assert.False(created.Bool("isSupplier"));

        var replaced = await P.ReplaceAsync(tenant.Client, created.Id(),
            P.Full("P1").With("isCustomer", false).With("isSupplier", true));

        Assert.False(replaced.Bool("isCustomer"));
        Assert.True(replaced.Bool("isSupplier"));
        Assert.Equal(["P1"], (await P.ListAsync(tenant.Client, "?isSupplier=true&isCustomer=false")).Codes());
    }

    public static TheoryData<string, string> InvalidReplaceValues() => new()
    {
        { "countryCode", "hr" },
        { "taxId", new string('t', 51) },
        { "city", "a\nb" },
    };

    [Theory]
    [MemberData(nameof(InvalidReplaceValues))]
    public async Task AC45_Replace_with_an_invalid_optional_field_is_rejected_and_changes_nothing(string field, string value)
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, P.Full("P1"));

        using var response = await P.PutAsync(tenant.Client, created.Id(), Changed("P2").With(field, value));

        await HttpAssert.ValidationAsync(response, field);
        await P.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC46_Unknown_partner_is_not_found_and_validation_precedes_existence()
    {
        var tenant = await app.NewTenantAsync();
        var id = Guid.NewGuid();

        using var put = await P.PutAsync(tenant.Client, id, P.Full("P1"));
        using var get = await tenant.Client.GetAsync($"{P.Path}/{id}");
        using var delete = await P.DeleteAsync(tenant.Client, id);
        using var invalidPut = await P.PutAsync(tenant.Client, id, P.Full("P1").With("name", ""));
        using var notUuid = await tenant.Client.GetAsync($"{P.Path}/not-a-uuid");
        using var byCode = await P.ByCodeAsync(tenant.Client, "nope");

        await HttpAssert.NotFoundAsync(put);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(delete);
        await HttpAssert.ValidationAsync(invalidPut, "name");
        await HttpAssert.NotFoundAsync(notUuid);
        await HttpAssert.NotFoundAsync(byCode);
        Assert.Equal(0, await TotalAsync(tenant));
    }

    [Fact]
    public async Task AC46_R11_Validation_precedes_the_code_check_too()
    {
        // R11: 400 -> 404 -> 409. An invalid body that also carries a taken code is a validation error.
        var tenant = await app.NewTenantAsync();
        await P.CreateAsync(tenant.Client, "Other");
        var created = await P.CreateAsync(tenant.Client, P.Full("Mine"));

        using var response = await P.PutAsync(tenant.Client, created.Id(),
            Changed("Other").With("isCustomer", false).With("isSupplier", false));

        await HttpAssert.ValidationAsync(response, "isCustomer", "isSupplier");
        await P.AssertUnchangedAsync(tenant.Client, created);
    }

    [Fact]
    public async Task AC47_Replace_by_a_second_key_sets_updatedBy_and_keeps_createdBy()
    {
        var tenant = await app.NewTenantAsync();
        var k2 = await Keys.CreateAsync(app, tenant.Client, "second", "human");
        var created = await P.CreateAsync(tenant.Client, "P1");

        var replaced = await P.ReplaceAsync(k2.Client, created.Id(), Changed("P1"));

        Assert.Equal(tenant.ApiKeyId, replaced.GetProperty("createdBy").GetGuid());
        Assert.Equal(k2.Id, replaced.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(replaced, await P.GetAsync(tenant.Client, created.Id()));
    }

    // ---- delete ----

    [Fact]
    public async Task AC50_Delete_removes_the_partner_and_frees_its_code()
    {
        var tenant = await app.NewTenantAsync();
        var created = await P.CreateAsync(tenant.Client, "P1");

        using var first = await P.DeleteAsync(tenant.Client, created.Id());
        using var get = await tenant.Client.GetAsync($"{P.Path}/{created.Id()}");
        using var second = await P.DeleteAsync(tenant.Client, created.Id());
        using var byCode = await P.ByCodeAsync(tenant.Client, "P1");
        var again = await P.CreateAsync(tenant.Client, "P1");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Empty(await first.Content.ReadAsByteArrayAsync());
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(second);
        await HttpAssert.NotFoundAsync(byCode);
        Assert.NotEqual(created.Id(), again.Id());
        Assert.Equal(1, await TotalAsync(tenant));
    }
}
