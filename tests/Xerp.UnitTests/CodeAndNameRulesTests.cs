using Xerp.Domain.Common;

namespace Xerp.UnitTests;

public class CodeAndNameRulesTests
{
    [Theory]
    [InlineData("kg", "kg")]
    [InlineData("KG", "KG")]
    [InlineData("  pcs  ", "pcs")]
    [InlineData("\tpcs\n", "pcs")]
    [InlineData("m²", "m²")]
    [InlineData("kom.", "kom.")]
    [InlineData("box-10", "box-10")]
    [InlineData("l_1", "l_1")]
    [InlineData("čšž", "čšž")]
    public void R1_R2_R3_Valid_code_is_trimmed_and_kept_as_entered(string input, string expected)
    {
        Assert.True(CodeRules.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a%")]
    [InlineData("a?")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void R2_Invalid_code_is_rejected(string? input)
    {
        Assert.False(CodeRules.TryNormalize(input, out _));
    }

    [Fact]
    public void R2_Code_length_limit_is_50_after_trimming()
    {
        Assert.True(CodeRules.TryNormalize(new string('a', 50), out _));
        Assert.True(CodeRules.TryNormalize("  " + new string('a', 50) + "  ", out var trimmed));
        Assert.Equal(50, trimmed.Length);
        Assert.False(CodeRules.TryNormalize(new string('a', 51), out _));
    }

    [Theory]
    [InlineData("Kilogram", "Kilogram")]
    [InlineData("  Piece  ", "Piece")]
    [InlineData("100% cotton", "100% cotton")]
    public void R4_Valid_name_is_trimmed(string input, string expected)
    {
        Assert.True(NameRules.TryNormalize(input, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void R4_Empty_name_is_rejected(string? input)
    {
        Assert.False(NameRules.TryNormalize(input, out _));
    }

    [Fact]
    public void R4_Name_length_limit_is_200_after_trimming()
    {
        Assert.True(NameRules.TryNormalize(new string('n', 200), out _));
        Assert.True(NameRules.TryNormalize(" " + new string('n', 200) + " ", out _));
        Assert.False(NameRules.TryNormalize(new string('n', 201), out _));
    }
}
