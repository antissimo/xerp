using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Partners;

namespace Xerp.UnitTests;

/// <summary>Spec 004, AC-04: the optional-text rule (R3), the country-code rule (R4) and the role rule (R15) in Domain.</summary>
public class AddressAndPartnerRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.CreateVersion7();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t ")]
    public void R3_No_value_is_null(string? input)
    {
        Assert.True(OptionalTextRules.TryNormalize(input, 50, out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("  X1  ", "X1")]
    [InlineData("HR 123", "HR 123")]
    [InlineData("äö-/.123", "äö-/.123")]
    public void R3_Text_is_trimmed_and_otherwise_kept_as_entered(string input, string expected)
    {
        Assert.True(OptionalTextRules.TryNormalize(input, 50, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void R3_Length_is_counted_after_trimming()
    {
        Assert.True(OptionalTextRules.TryNormalize("  " + new string('x', 50) + "  ", 50, out var value));
        Assert.Equal(50, value!.Length);
        Assert.False(OptionalTextRules.TryNormalize(new string('x', 51), 50, out _));
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\rb")]
    [InlineData("a\u007Fb")]
    public void R3_Control_characters_are_rejected_also_line_breaks_and_tabs(string input)
    {
        Assert.False(OptionalTextRules.TryNormalize(input, 50, out _));
    }

    [Theory]
    [InlineData("HR", "HR")]
    [InlineData(" HR ", "HR")]
    [InlineData("XX", "XX")] // the form is checked, not the list of countries
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    public void R4_Country_code_is_two_upper_case_ascii_letters_or_nothing(string? input, string? expected)
    {
        Assert.True(CountryCodeRules.TryNormalize(input, out var countryCode));
        Assert.Equal(expected, countryCode);
    }

    [Theory]
    [InlineData("hr")]
    [InlineData("Hr")]
    [InlineData("H")]
    [InlineData("HRV")]
    [InlineData("H1")]
    [InlineData("12")]
    [InlineData("ÅB")]
    [InlineData("H R")]
    [InlineData("Croatia")]
    public void R4_Any_other_form_is_rejected_not_converted(string input)
    {
        Assert.False(CountryCodeRules.TryNormalize(input, out _));
    }

    [Fact]
    public void R3_R4_R5_Address_normalises_each_field_independently()
    {
        var address = Address.Create("  Ilica 1 ", "", null, " Zagreb ", "   ", " HR ");

        Assert.Equal("Ilica 1", address.Line1);
        Assert.Null(address.Line2);
        Assert.Null(address.PostalCode);
        Assert.Equal("Zagreb", address.City);
        Assert.Null(address.Region);
        Assert.Equal("HR", address.CountryCode);
        Assert.Equal(Address.Empty, Address.Create(null, " ", "", null, null, ""));
    }

    [Fact]
    public void R3_R4_Address_enforces_the_limits_of_its_fields()
    {
        Assert.Equal(200, Address.Create(new string('a', 200), null, null, null, null, null).Line1!.Length);
        Assert.Throws<ArgumentException>(() => Address.Create(new string('a', 201), null, null, null, null, null));
        Assert.Throws<ArgumentException>(() => Address.Create(null, new string('a', 201), null, null, null, null));
        Assert.Throws<ArgumentException>(() => Address.Create(null, null, new string('1', 21), null, null, null));
        Assert.Throws<ArgumentException>(() => Address.Create(null, null, null, new string('c', 101), null, null));
        Assert.Throws<ArgumentException>(() => Address.Create(null, null, null, null, new string('r', 101), null));
        Assert.Throws<ArgumentException>(() => Address.Create(null, null, null, "a\nb", null, null));
        Assert.Throws<ArgumentException>(() => Address.Create(null, null, null, null, null, "hr"));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void R15_A_partner_has_at_least_one_role(bool isCustomer, bool isSupplier, bool allowed)
    {
        Assert.Equal(allowed, PartnerRules.HasRole(isCustomer, isSupplier));
    }

    [Fact]
    public void R15_A_partner_cannot_be_created_without_a_role()
    {
        Assert.Throws<ArgumentException>(() => Partner.Create("P1", "Acme", false, false, null, Address.Empty, true, T0, Actor));
    }

    [Fact]
    public void Created_partner_holds_normalised_values_and_the_actor_in_both_audit_fields()
    {
        var address = Address.Create("Ilica 1", null, "10000", "Zagreb", null, "HR");

        var partner = Partner.Create(" P-001 ", " Acme d.o.o. ", true, false, "  HR123  ", address, true, T0, Actor);

        Assert.Equal(7, partner.Id.Version);
        Assert.Equal("P-001", partner.Code);
        Assert.Equal("Acme d.o.o.", partner.Name);
        Assert.True(partner.IsCustomer);
        Assert.False(partner.IsSupplier);
        Assert.Equal("HR123", partner.TaxId);
        Assert.Equal(address, partner.Address);
        Assert.True(partner.IsActive);
        Assert.Equal((T0, T0, Actor, Actor), (partner.CreatedAt, partner.UpdatedAt, partner.CreatedBy, partner.UpdatedBy));
        Assert.Equal(Guid.Empty, partner.TenantId); // stamped by the DbContext, not by callers
    }

    [Fact]
    public void R3_Partner_tax_id_follows_the_optional_text_rule()
    {
        Assert.Null(Partner.Create("P1", "Acme", true, false, "   ", Address.Empty, true, T0, Actor).TaxId);
        Assert.Throws<ArgumentException>(() => Partner.Create("P1", "Acme", true, false, new string('1', 51), Address.Empty, true, T0, Actor));
        Assert.Throws<ArgumentException>(() => Partner.Create("P1", "Acme", true, false, "a\nb", Address.Empty, true, T0, Actor));
    }

    [Fact]
    public void R8_R15_Partner_replace_changes_everything_but_identity_and_creation_audit_and_keeps_a_role()
    {
        var editor = Guid.CreateVersion7();
        var later = T0.AddMinutes(5);
        var partner = Partner.Create("P1", "Acme", true, false, "T1", Address.Create("Ilica 1", null, null, null, null, null), true, T0, Actor);
        var id = partner.Id;

        partner.Replace("P2", "Acme 2", false, true, null, Address.Empty, false, later, editor);

        Assert.Equal((id, "P2", "Acme 2", false, true), (partner.Id, partner.Code, partner.Name, partner.IsCustomer, partner.IsSupplier));
        Assert.Null(partner.TaxId);
        Assert.Equal(Address.Empty, partner.Address);
        Assert.False(partner.IsActive);
        Assert.Equal((T0, later, Actor, editor), (partner.CreatedAt, partner.UpdatedAt, partner.CreatedBy, partner.UpdatedBy));

        // A rejected replace leaves the partner untouched.
        Assert.Throws<ArgumentException>(() => partner.Replace("P3", "Acme 3", false, false, null, Address.Empty, true, later, editor));
        Assert.Throws<ArgumentException>(() => partner.Replace("bad code", "Acme 3", true, true, null, Address.Empty, true, later, editor));
        Assert.Equal(("P2", "Acme 2", false), (partner.Code, partner.Name, partner.IsActive));
    }

    [Fact]
    public void Warehouse_create_and_replace()
    {
        var editor = Guid.CreateVersion7();
        var later = T0.AddMinutes(5);
        var address = Address.Create(null, null, null, "Zagreb", null, null);

        var warehouse = Warehouse.Create(" WH-1 ", " Main ", address, true, T0, Actor);

        Assert.Equal(7, warehouse.Id.Version);
        Assert.Equal(("WH-1", "Main", address, true), (warehouse.Code, warehouse.Name, warehouse.Address, warehouse.IsActive));
        Assert.Equal((T0, T0, Actor, Actor), (warehouse.CreatedAt, warehouse.UpdatedAt, warehouse.CreatedBy, warehouse.UpdatedBy));

        warehouse.Replace("WH-2", "Second", Address.Empty, false, later, editor);

        Assert.Equal(("WH-2", "Second", Address.Empty, false), (warehouse.Code, warehouse.Name, warehouse.Address, warehouse.IsActive));
        Assert.Equal((T0, later, Actor, editor), (warehouse.CreatedAt, warehouse.UpdatedAt, warehouse.CreatedBy, warehouse.UpdatedBy));
        Assert.Throws<ArgumentException>(() => warehouse.Replace("", "x", Address.Empty, true, later, editor));
        Assert.Equal("WH-2", warehouse.Code);
    }
}
