using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.Application.Common;
using Xerp.Application.Partners;
using Xerp.Application.Warehouses;
using Xerp.Domain.Common;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 004: the input rules of partners and warehouses live in Application, so they hold for HTTP and MCP
/// alike. Inputs are bound from JSON where "omitted" and "null" must differ (R7, R9, R21).
/// </summary>
public class PartnerWarehouseValidationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] AddressFields = ["addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    private static PartnerInput Partner(JsonObject json) => JsonSerializer.Deserialize<PartnerInput>(json.ToJsonString(), Web)!;

    private static WarehouseInput Warehouse(JsonObject json) => JsonSerializer.Deserialize<WarehouseInput>(json.ToJsonString(), Web)!;

    private static JsonObject FullPartner() => new()
    {
        ["code"] = "P-001", ["name"] = "Acme d.o.o.", ["isCustomer"] = true, ["isSupplier"] = false,
        ["taxId"] = "HR123", ["addressLine1"] = "Ilica 1", ["addressLine2"] = "2nd floor", ["postalCode"] = "10000",
        ["city"] = "Zagreb", ["region"] = "Grad Zagreb", ["countryCode"] = "HR", ["isActive"] = false,
    };

    private static JsonObject FullWarehouse() => new()
    {
        ["code"] = "WH-1", ["name"] = "Main", ["addressLine1"] = "Slavonska 6", ["addressLine2"] = "Hall B",
        ["postalCode"] = "10000", ["city"] = "Zagreb", ["region"] = "Grad Zagreb", ["countryCode"] = "HR", ["isActive"] = true,
    };

    private static JsonObject With(JsonObject json, string property, JsonNode? value)
    {
        json[property] = value;
        return json;
    }

    private static JsonObject Without(JsonObject json, string property)
    {
        json.Remove(property);
        return json;
    }

    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal(expectedKeys.Order(), result.Error.Errors!.Keys.Order());
        Assert.All(result.Error.Errors.Values, messages => Assert.NotEmpty(messages));
    }

    // ---- partners: create

    [Fact]
    public void R6_R14_Partner_create_trims_and_applies_defaults()
    {
        var result = PartnerValidation.Create(new PartnerInput { Code = "  P1 ", Name = " Acme ", IsCustomer = true });

        Assert.True(result.IsSuccess);
        Assert.Equal(new PartnerValues("P1", "Acme", true, false, null, Address.Empty, true), result.Value);
    }

    [Fact]
    public void Partner_create_keeps_explicit_values()
    {
        var result = PartnerValidation.Create(Partner(FullPartner()));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new PartnerValues("P-001", "Acme d.o.o.", true, false, "HR123",
                Address.Create("Ilica 1", "2nd floor", "10000", "Zagreb", "Grad Zagreb", "HR"), false),
            result.Value);
    }

    [Fact]
    public void E7_Empty_partner_reports_code_name_and_both_roles_together()
    {
        AssertInvalid(PartnerValidation.Create(new PartnerInput()), "code", "name", "isCustomer", "isSupplier");
    }

    [Fact]
    public void R15_E2_Partner_create_without_a_role_names_both_role_fields()
    {
        AssertInvalid(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x" }), "isCustomer", "isSupplier");
        AssertInvalid(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsCustomer = false, IsSupplier = false }), "isCustomer", "isSupplier");
        AssertInvalid(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsSupplier = false }), "isCustomer", "isSupplier");
        AssertInvalid(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsCustomer = false }), "isCustomer", "isSupplier");

        var supplierOnly = PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsSupplier = true });
        Assert.Equal((false, true), (supplierOnly.Value!.IsCustomer, supplierOnly.Value.IsSupplier));
        Assert.True(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsCustomer = true, IsSupplier = true }).IsSuccess);
    }

    [Fact]
    public void R9_A_boolean_given_as_null_is_a_wrong_value_not_an_omitted_one()
    {
        AssertInvalid(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsCustomer = null, IsSupplier = true }), "isCustomer");
        AssertInvalid(PartnerValidation.Create(new PartnerInput { Code = "P", Name = "x", IsCustomer = true, IsActive = null }), "isActive");
        AssertInvalid(WarehouseValidation.Create(new WarehouseInput { Code = "W", Name = "x", IsActive = null }), "isActive");
    }

    [Fact]
    public void R3_R4_Optional_text_given_as_empty_blank_or_null_is_no_value()
    {
        foreach (var empty in new JsonNode?[] { "", "   ", null })
        {
            var json = new JsonObject { ["code"] = "P", ["name"] = "x", ["isCustomer"] = true, ["taxId"] = empty?.DeepClone() };
            foreach (var field in AddressFields)
                json[field] = empty?.DeepClone();

            var result = PartnerValidation.Create(Partner(json));

            Assert.True(result.IsSuccess);
            Assert.Null(result.Value!.TaxId);
            Assert.Equal(Address.Empty, result.Value.Address);
        }
    }

    [Theory]
    [InlineData("taxId", 50)]
    [InlineData("addressLine1", 200)]
    [InlineData("addressLine2", 200)]
    [InlineData("postalCode", 20)]
    [InlineData("city", 100)]
    [InlineData("region", 100)]
    public void R3_Each_optional_text_has_its_own_limit_and_its_own_error_key(string field, int maxLength)
    {
        JsonObject Body(string value) => With(new JsonObject { ["code"] = "P", ["name"] = "x", ["isCustomer"] = true }, field, value);

        Assert.True(PartnerValidation.Create(Partner(Body(new string('x', maxLength)))).IsSuccess);
        AssertInvalid(PartnerValidation.Create(Partner(Body(new string('x', maxLength + 1)))), field);
        AssertInvalid(PartnerValidation.Create(Partner(Body("a\nb"))), field);
        AssertInvalid(PartnerValidation.Create(Partner(Body("a\u0000b"))), field);
    }

    [Fact]
    public void R4_Country_code_in_another_form_is_reported_under_countryCode_with_other_errors()
    {
        var input = new PartnerInput { Code = "P", Name = "x", IsCustomer = true, CountryCode = "hr", TaxId = new string('1', 51) };

        AssertInvalid(PartnerValidation.Create(input), "countryCode", "taxId");
    }

    // ---- partners: replace

    [Fact]
    public void R7_Partner_replace_accepts_all_twelve_fields()
    {
        var result = PartnerValidation.Replace(Partner(FullPartner()));

        Assert.True(result.IsSuccess);
        Assert.Equal("HR123", result.Value!.TaxId);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public void R7_R21_Partner_replace_requires_every_field_to_be_present()
    {
        foreach (var field in FullPartner().Select(p => p.Key).ToList())
            AssertInvalid(PartnerValidation.Replace(Partner(Without(FullPartner(), field))), field);
    }

    [Fact]
    public void R7_Partner_replace_with_null_optional_text_clears_it()
    {
        var json = With(FullPartner(), "taxId", null);
        foreach (var field in AddressFields)
            json[field] = null;

        var result = PartnerValidation.Replace(Partner(json));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.TaxId);
        Assert.Equal(Address.Empty, result.Value.Address);
    }

    [Fact]
    public void R15_Partner_replace_cannot_remove_the_last_role()
    {
        AssertInvalid(PartnerValidation.Replace(Partner(With(FullPartner(), "isCustomer", false))), "isCustomer", "isSupplier");
        Assert.True(PartnerValidation.Replace(Partner(With(With(FullPartner(), "isCustomer", false), "isSupplier", true))).IsSuccess);
    }

    [Fact]
    public void R9_Partner_replace_with_a_null_boolean_reports_that_field_only()
    {
        AssertInvalid(PartnerValidation.Replace(Partner(With(FullPartner(), "isSupplier", null))), "isSupplier");
        AssertInvalid(PartnerValidation.Replace(Partner(With(FullPartner(), "isActive", null))), "isActive");
    }

    // ---- partners: list

    [Fact]
    public void R13_R18_Partner_list_defaults_and_filters()
    {
        var defaults = PartnerValidation.List(new ListPartnersInput());
        var filtered = PartnerValidation.List(new ListPartnersInput("  acme ", true, false, true, 10, 20));

        Assert.Equal(new PartnerListQuery(null, null, null, null, 50, 0), defaults.Value);
        Assert.Equal(new PartnerListQuery("acme", true, false, true, 10, 20), filtered.Value);
        AssertInvalid(PartnerValidation.List(new ListPartnersInput(Search: "a\u0000b", Limit: 0, Offset: -1)), "search", "limit", "offset");
        AssertInvalid(PartnerValidation.List(new ListPartnersInput(Search: new string('s', 101), Limit: 501)), "search", "limit");
    }

    // ---- warehouses

    [Fact]
    public void Warehouse_create_trims_and_applies_defaults()
    {
        var result = WarehouseValidation.Create(new WarehouseInput { Code = " WH-1 ", Name = " Main ", City = " Zagreb " });

        Assert.True(result.IsSuccess);
        Assert.Equal(new WarehouseValues("WH-1", "Main", Address.Create(null, null, null, "Zagreb", null, null), true), result.Value);
    }

    [Fact]
    public void Warehouse_create_reports_all_invalid_fields_together()
    {
        AssertInvalid(WarehouseValidation.Create(new WarehouseInput()), "code", "name");
        AssertInvalid(
            WarehouseValidation.Create(new WarehouseInput { Code = "a b", Name = "a\nb", City = "a\u0000b", CountryCode = "HRV", PostalCode = new string('1', 21) }),
            "code", "name", "city", "countryCode", "postalCode");
    }

    [Fact]
    public void R7_Warehouse_replace_requires_all_nine_fields_and_accepts_null_addresses()
    {
        Assert.True(WarehouseValidation.Replace(Warehouse(FullWarehouse())).IsSuccess);
        foreach (var field in FullWarehouse().Select(p => p.Key).ToList())
            AssertInvalid(WarehouseValidation.Replace(Warehouse(Without(FullWarehouse(), field))), field);

        var cleared = FullWarehouse();
        foreach (var field in AddressFields)
            cleared[field] = null;
        Assert.Equal(Address.Empty, WarehouseValidation.Replace(Warehouse(cleared)).Value!.Address);
    }

    [Fact]
    public void R13_Warehouse_list_defaults_and_limits()
    {
        Assert.Equal(new WarehouseListQuery(null, null, 50, 0), WarehouseValidation.List(new ListWarehousesInput()).Value);
        AssertInvalid(WarehouseValidation.List(new ListWarehousesInput(Limit: 501, Offset: -1)), "limit", "offset");
    }
}
