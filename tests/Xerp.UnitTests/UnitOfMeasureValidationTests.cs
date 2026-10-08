using Xerp.Application.Common;
using Xerp.Application.UnitsOfMeasure;

namespace Xerp.UnitTests;

/// <summary>The input rules live in Application, so they are testable without HTTP and reusable by MCP.</summary>
public class UnitOfMeasureValidationTests
{
    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.NotNull(result.Error.Errors);
        Assert.Equal(expectedKeys.Order(), result.Error.Errors!.Keys.Order());
        Assert.All(result.Error.Errors.Values, messages => Assert.NotEmpty(messages));
    }

    [Fact]
    public void R1_R4_R5_Create_trims_and_defaults_isActive_to_true()
    {
        var result = UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput("  pcs  ", "  Piece  ", null));

        Assert.True(result.IsSuccess);
        Assert.Equal(new UnitOfMeasureValues("pcs", "Piece", true), result.Value);
    }

    [Fact]
    public void R5_Create_keeps_explicit_isActive_false()
    {
        var result = UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput("kg", "Kilogram", false));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsActive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a%")]
    [InlineData("a?")]
    public void E1_E3_Create_with_bad_code_reports_code(string? code)
    {
        AssertInvalid(UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput(code, "Name", null)), "code");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void E1_Create_with_bad_name_reports_name(string? name)
    {
        AssertInvalid(UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput("kg", name, null)), "name");
    }

    [Fact]
    public void E1_Create_failing_both_reports_both()
    {
        AssertInvalid(UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput(null, " ", null)), "code", "name");
    }

    [Fact]
    public void E2_Length_limits()
    {
        Assert.True(UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput(new string('c', 50), new string('n', 200), null)).IsSuccess);
        AssertInvalid(UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput(new string('c', 51), "Name", null)), "code");
        AssertInvalid(UnitOfMeasureValidation.Create(new CreateUnitOfMeasureInput("kg", new string('n', 201), null)), "name");
    }

    [Fact]
    public void R5_Replace_requires_all_three_fields()
    {
        Assert.True(UnitOfMeasureValidation.Replace(new ReplaceUnitOfMeasureInput(" kgm ", " Kilogramme ", false)).IsSuccess);
        Assert.Equal(
            new UnitOfMeasureValues("kgm", "Kilogramme", false),
            UnitOfMeasureValidation.Replace(new ReplaceUnitOfMeasureInput(" kgm ", " Kilogramme ", false)).Value);

        AssertInvalid(UnitOfMeasureValidation.Replace(new ReplaceUnitOfMeasureInput("kg", "Kilogram", null)), "isActive");
        AssertInvalid(UnitOfMeasureValidation.Replace(new ReplaceUnitOfMeasureInput(null, "Kilogram", true)), "code");
        AssertInvalid(UnitOfMeasureValidation.Replace(new ReplaceUnitOfMeasureInput("kg", null, true)), "name");
        AssertInvalid(UnitOfMeasureValidation.Replace(new ReplaceUnitOfMeasureInput(null, null, null)), "code", "isActive", "name");
    }

    [Fact]
    public void R9_List_defaults()
    {
        var result = UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput());

        Assert.True(result.IsSuccess);
        Assert.Equal(new UnitOfMeasureListQuery(null, null, 50, 0), result.Value);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" kg ", "kg")]
    [InlineData("%", "%")]
    public void R9_List_search_is_trimmed_and_empty_means_no_filter(string? search, string? expected)
    {
        var result = UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Search: search));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value!.Search);
    }

    [Fact]
    public void R9_E5_List_search_longer_than_100_is_rejected()
    {
        Assert.True(UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Search: new string('x', 100))).IsSuccess);
        AssertInvalid(UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Search: new string('x', 101))), "search");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void R9_List_limit_in_range_is_applied_not_clamped(int limit)
    {
        var result = UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Limit: limit, Offset: 7, IsActive: false));

        Assert.True(result.IsSuccess);
        Assert.Equal(new UnitOfMeasureListQuery(null, false, limit, 7), result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(-1)]
    public void R9_E5_List_limit_out_of_range_is_rejected(int limit)
    {
        AssertInvalid(UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Limit: limit)), "limit");
    }

    [Fact]
    public void R9_E5_List_negative_offset_is_rejected_and_errors_accumulate()
    {
        AssertInvalid(UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Offset: -1)), "offset");
        AssertInvalid(UnitOfMeasureValidation.List(new ListUnitsOfMeasureInput(Limit: 0, Offset: -1)), "limit", "offset");
    }
}
