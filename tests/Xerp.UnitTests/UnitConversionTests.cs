using System.Globalization;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 007, AC-02: the conversion rule (R14, R15) and the factor rule (R2) as Domain rules, without HTTP or
/// a database - including half-way values, zero results and the maximum.
/// </summary>
public class UnitConversionTests
{
    private static decimal Dec(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    // ---- R2 factor

    [Theory]
    [InlineData("0.000001")]
    [InlineData("0.5")]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("12.000000")]
    [InlineData("999999.999999")]
    public void R2_Valid_factors(string factor)
    {
        Assert.True(UnitConversion.IsValidFactor(Dec(factor)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.000001")]
    [InlineData("0.0000001")]
    [InlineData("1.0000001")]
    [InlineData("1000000")]
    [InlineData("999999.9999991")]
    public void R2_Invalid_factors_are_rejected_never_rounded(string factor)
    {
        Assert.False(UnitConversion.IsValidFactor(Dec(factor)));
    }

    // ---- R14 conversion

    [Theory]
    [InlineData("5", "12", "60")]
    [InlineData("7.5", "1", "7.5")]                    // the base unit: factor 1, base quantity equals quantity
    [InlineData("0.000001", "1", "0.000001")]
    [InlineData("999999999.999999", "1", "999999999.999999")]
    [InlineData("2.5", "8", "20")]
    [InlineData("0.2", "50", "10")]
    [InlineData("3", "0.5", "1.5")]
    [InlineData("0.4", "0.333333", "0.133333")]        // 0.1333332: below the half
    [InlineData("0.2", "0.333333", "0.066667")]        // 0.0666666: above the half
    [InlineData("0.000001", "12", "0.000012")]
    public void R14_Base_quantity_is_quantity_times_factor(string quantity, string factor, string expected)
    {
        Assert.Equal(Dec(expected), UnitConversion.ToBase(Dec(quantity), Dec(factor)));
    }

    [Theory]
    // Exactly half-way in the seventh decimal: away from zero, never to even.
    [InlineData("0.5", "0.333333", "0.166667")]        // 0.1666665
    [InlineData("1.5", "0.333333", "0.5")]             // 0.4999995
    [InlineData("1.5", "1.111111", "1.666667")]        // 1.6666665
    [InlineData("2.5", "1.111111", "2.777778")]        // 2.7777775
    [InlineData("1.5", "0.000001", "0.000002")]        // 0.0000015: to even would also give 0.000002
    [InlineData("2.5", "0.000001", "0.000003")]        // 0.0000025: to even would give 0.000002
    [InlineData("4.5", "0.000001", "0.000005")]        // 0.0000045: to even would give 0.000004
    [InlineData("0.000001", "0.5", "0.000001")]        // 0.0000005: to even would give 0
    public void R14_A_half_way_value_rounds_away_from_zero(string quantity, string factor, string expected)
    {
        Assert.Equal(Dec(expected), UnitConversion.ToBase(Dec(quantity), Dec(factor)));
        Assert.True(UnitConversion.TryToBase(Dec(quantity), Dec(factor), out var baseQuantity));
        Assert.Equal(Dec(expected), baseQuantity);
    }

    [Fact]
    public void R14_The_result_never_has_more_than_six_decimal_places_and_obeys_the_quantity_rule()
    {
        foreach (var (quantity, factor) in new[] { (1.234567m, 7.654321m), (0.000123m, 999.999999m), (123456.654321m, 0.000007m) })
        {
            Assert.True(UnitConversion.TryToBase(quantity, factor, out var baseQuantity));
            Assert.Equal(decimal.Round(baseQuantity, 6), baseQuantity);
            Assert.True(QuantityRules.IsValid(baseQuantity));
        }
    }

    // ---- R15 not convertible

    [Theory]
    [InlineData("0.000001", "0.4")]        // 0.0000004 rounds to zero
    [InlineData("0.000001", "0.499999")]   // just below the half
    [InlineData("0.000002", "0.2")]        // 0.0000004
    [InlineData("0.000001", "0.000001")]   // the smallest of both
    public void R15_A_quantity_that_converts_to_zero_is_not_convertible(string quantity, string factor)
    {
        Assert.False(UnitConversion.TryToBase(Dec(quantity), Dec(factor), out var baseQuantity));
        Assert.Equal(0m, baseQuantity);
    }

    [Theory]
    [InlineData("999999.999999", "1000", "999999999.999")]        // within the maximum
    [InlineData("999999999.999999", "1", "999999999.999999")]     // the maximum itself
    [InlineData("1000", "999999.999999", "999999999.999")]
    [InlineData("500000000", "1.999999", "999999500")]
    public void R15_A_quantity_that_converts_to_at_most_the_maximum_is_convertible(string quantity, string factor, string expected)
    {
        Assert.True(UnitConversion.TryToBase(Dec(quantity), Dec(factor), out var baseQuantity));
        Assert.Equal(Dec(expected), baseQuantity);
    }

    [Theory]
    [InlineData("999999999", "999999.999999")]
    [InlineData("1000000", "1000")]                    // 1 000 000 000: just above the maximum
    [InlineData("999999999.999999", "1.000001")]
    [InlineData("999999999.999999", "999999.999999")]  // the largest of both: no overflow, a plain refusal
    [InlineData("500000000", "2")]
    public void R15_A_quantity_that_converts_to_more_than_the_maximum_is_not_convertible(string quantity, string factor)
    {
        Assert.False(UnitConversion.TryToBase(Dec(quantity), Dec(factor), out var baseQuantity));
        Assert.True(baseQuantity > QuantityRules.Max);
    }

    [Fact]
    public void R15_Rounding_decides_at_the_edge_of_the_maximum()
    {
        // 999999.999999 × 1000 = 999999999.999 is within the maximum; one millionth more on the factor is not.
        Assert.True(UnitConversion.TryToBase(999999.999999m, 1000m, out var within));
        Assert.Equal(999999999.999m, within);
        Assert.False(UnitConversion.TryToBase(999999.999999m, 1000.000001m, out var above));
        Assert.Equal(1000000000.999m, above); // 1000000000.998999999999 rounded
    }

    // ---- the conversion record

    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Article = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();
    private static readonly Guid Box = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();

    [Fact]
    public void R1_R3_A_conversion_gives_one_unit_of_one_article_its_factor_and_is_attributed()
    {
        var conversion = ArticleUnit.Create(Article, Pcs, Box, 12m, T0, Actor);

        Assert.Equal((Article, Box, 12m), (conversion.ArticleId, conversion.UnitId, conversion.Factor));
        Assert.Equal((T0, T0, Actor, Actor), (conversion.CreatedAt, conversion.UpdatedAt, conversion.CreatedBy, conversion.UpdatedBy));
    }

    [Fact]
    public void R5_The_base_unit_cannot_be_given_a_conversion()
    {
        Assert.Throws<ArgumentException>(() => ArticleUnit.Create(Article, Pcs, Pcs, 1m, T0, Actor));
        Assert.Throws<ArgumentException>(() => ArticleUnit.Create(Article, Pcs, Pcs, 12m, T0, Actor));
    }

    [Fact]
    public void R2_A_factor_of_one_is_allowed_and_an_invalid_factor_is_refused()
    {
        Assert.Equal(1m, ArticleUnit.Create(Article, Pcs, Box, 1m, T0, Actor).Factor);
        Assert.Throws<ArgumentException>(() => ArticleUnit.Create(Article, Pcs, Box, 0m, T0, Actor));
        Assert.Throws<ArgumentException>(() => ArticleUnit.Create(Article, Pcs, Box, 1.0000001m, T0, Actor));
        Assert.Throws<ArgumentException>(() => ArticleUnit.Create(Article, Pcs, Box, 1000000m, T0, Actor));
    }

    [Fact]
    public void R3_R7_Replacing_the_factor_keeps_the_creation_and_names_who_changed_it()
    {
        var editor = Guid.CreateVersion7();
        var conversion = ArticleUnit.Create(Article, Pcs, Box, 12m, T0, Actor);

        conversion.SetFactor(10m, T0.AddHours(1), editor);

        Assert.Equal((Article, Box, 10m), (conversion.ArticleId, conversion.UnitId, conversion.Factor));
        Assert.Equal((T0, T0.AddHours(1), Actor, editor), (conversion.CreatedAt, conversion.UpdatedAt, conversion.CreatedBy, conversion.UpdatedBy));

        // A refused factor leaves the conversion untouched.
        Assert.Throws<ArgumentException>(() => conversion.SetFactor(-1m, T0.AddHours(2), Actor));
        Assert.Equal((10m, T0.AddHours(1), editor), (conversion.Factor, conversion.UpdatedAt, conversion.UpdatedBy));
    }
}
