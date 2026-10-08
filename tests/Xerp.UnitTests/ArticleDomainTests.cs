using Xerp.Domain.Catalog;

namespace Xerp.UnitTests;

public class ArticleDomainTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void R4_Type_names_are_exactly_stock_and_service()
    {
        Assert.Equal("stock", ArticleType.Stock.ToName());
        Assert.Equal("service", ArticleType.Service.ToName());
        Assert.True(ArticleTypeNames.TryParse("stock", out var stock));
        Assert.Equal(ArticleType.Stock, stock);
        Assert.True(ArticleTypeNames.TryParse("service", out var service));
        Assert.Equal(ArticleType.Service, service);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stock")]
    [InlineData("SERVICE")]
    [InlineData(" stock")]
    [InlineData("goods")]
    [InlineData("0")]
    public void R4_Any_other_type_name_is_rejected(string? name)
    {
        Assert.False(ArticleTypeNames.TryParse(name, out _));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" \n ", null)]
    [InlineData("zinc", "zinc")]
    [InlineData("  line1\nline2  ", "line1\nline2")]
    public void R3_Description_is_trimmed_and_empty_becomes_null(string? input, string? expected)
    {
        Assert.True(ArticleRules.TryNormalizeDescription(input, out var description));
        Assert.Equal(expected, description);
    }

    [Fact]
    public void R3_Description_limit_is_2000_after_trimming()
    {
        Assert.True(ArticleRules.TryNormalizeDescription(new string('d', 2000), out _));
        Assert.True(ArticleRules.TryNormalizeDescription("  " + new string('d', 2000) + "  ", out var trimmed));
        Assert.Equal(2000, trimmed!.Length);
        Assert.False(ArticleRules.TryNormalizeDescription(new string('d', 2001), out _));
    }

    [Fact]
    public void Created_article_has_v7_id_normalised_values_equal_timestamps_and_actor_in_both_audit_fields()
    {
        var actor = Guid.CreateVersion7();
        var unit = Guid.CreateVersion7();

        var article = Article.Create("  ART-001 ", " Steel bolt M8 ", "  ", ArticleType.Stock, unit, true, T0, actor);

        Assert.Equal(7, article.Id.Version);
        Assert.Equal("ART-001", article.Code);
        Assert.Equal("Steel bolt M8", article.Name);
        Assert.Null(article.Description);
        Assert.Equal(ArticleType.Stock, article.Type);
        Assert.Equal(unit, article.BaseUnitId);
        Assert.True(article.IsActive);
        Assert.Equal(T0, article.CreatedAt);
        Assert.Equal(T0, article.UpdatedAt);
        Assert.Equal(actor, article.CreatedBy);
        Assert.Equal(actor, article.UpdatedBy);
        Assert.Equal(Guid.Empty, article.TenantId); // stamped by the DbContext, not by callers
    }

    [Fact]
    public void R12_Replace_changes_every_field_and_update_audit_but_never_identity_or_creation_audit()
    {
        var creator = Guid.CreateVersion7();
        var editor = Guid.CreateVersion7();
        var unit2 = Guid.CreateVersion7();
        var article = Article.Create("ART-001", "Steel bolt M8", null, ArticleType.Stock, Guid.CreateVersion7(), true, T0, creator);
        var id = article.Id;
        var later = T0.AddMinutes(5);

        article.Replace("ART-002", "Bolt M10", " zinc ", ArticleType.Service, unit2, false, later, editor);

        Assert.Equal(id, article.Id);
        Assert.Equal("ART-002", article.Code);
        Assert.Equal("Bolt M10", article.Name);
        Assert.Equal("zinc", article.Description);
        Assert.Equal(ArticleType.Service, article.Type);
        Assert.Equal(unit2, article.BaseUnitId);
        Assert.False(article.IsActive);
        Assert.Equal(T0, article.CreatedAt);
        Assert.Equal(creator, article.CreatedBy);
        Assert.Equal(later, article.UpdatedAt);
        Assert.Equal(editor, article.UpdatedBy);
    }

    [Fact]
    public void Article_cannot_be_created_or_replaced_with_invalid_values()
    {
        var actor = Guid.CreateVersion7();
        var unit = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(() => Article.Create("a b", "Name", null, ArticleType.Stock, unit, true, T0, actor));
        Assert.Throws<ArgumentException>(() => Article.Create("A1", "  ", null, ArticleType.Stock, unit, true, T0, actor));
        Assert.Throws<ArgumentException>(() => Article.Create("A1", "Name", new string('d', 2001), ArticleType.Stock, unit, true, T0, actor));

        var article = Article.Create("A1", "Name", null, ArticleType.Stock, unit, true, T0, actor);
        Assert.Throws<ArgumentException>(() => article.Replace("A2", "Other", new string('d', 2001), ArticleType.Service, unit, true, T0, actor));
        Assert.Throws<ArgumentException>(() => article.Replace("a/b", "Other", null, ArticleType.Service, unit, true, T0, actor));
        Assert.Equal("A1", article.Code);
        Assert.Equal("Name", article.Name);
        Assert.Equal(ArticleType.Stock, article.Type);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u0007b")]
    [InlineData("a\u000bb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    public void R3_Description_with_a_control_character_other_than_line_break_or_tab_is_rejected(string input)
    {
        Assert.False(ArticleRules.TryNormalizeDescription(input, out _));
    }

    [Fact]
    public void R3_Description_keeps_line_feed_carriage_return_and_tab()
    {
        Assert.True(ArticleRules.TryNormalizeDescription("a\r\n\tb\nc", out var description));
        Assert.Equal("a\r\n\tb\nc", description);
    }
}
