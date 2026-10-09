using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Domain.Catalog;

namespace Xerp.UnitTests;

/// <summary>The input rules live in Application, so they are testable without HTTP and reusable by MCP.</summary>
public class ArticleValidationTests
{
    private static readonly Guid Unit = Guid.CreateVersion7();
    private static readonly string U = Unit.ToString();

    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.NotNull(result.Error.Errors);
        Assert.Equal(expectedKeys.Order(), result.Error.Errors!.Keys.Order());
        Assert.All(result.Error.Errors.Values, messages => Assert.NotEmpty(messages));
    }

    [Fact]
    public void R1_R2_R3_R10_Create_trims_and_applies_defaults()
    {
        var result = ArticleValidation.Create(new CreateArticleInput("  A1  ", "  Bolt  ", "stock", U));

        Assert.True(result.IsSuccess);
        Assert.Equal(new ArticleValues("A1", "Bolt", null, ArticleType.Stock, Unit, true), result.Value);
    }

    [Fact]
    public void R3_R10_Create_keeps_explicit_values()
    {
        var result = ArticleValidation.Create(new CreateArticleInput("A1", "Bolt", "service", U, "  line1\nline2  ", false));

        Assert.True(result.IsSuccess);
        Assert.Equal(new ArticleValues("A1", "Bolt", "line1\nline2", ArticleType.Service, Unit, false), result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void R3_Empty_description_becomes_null(string description)
    {
        var result = ArticleValidation.Create(new CreateArticleInput("A1", "Bolt", "stock", U, description));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Description);
    }

    [Fact]
    public void E4_Length_limits()
    {
        Assert.True(ArticleValidation.Create(new CreateArticleInput(
            new string('c', 50), new string('n', 200), "stock", U, new string('d', 2000))).IsSuccess);
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput(new string('c', 51), "Name", "stock", U)), "code");
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput("A1", new string('n', 201), "stock", U)), "name");
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput("A1", "Name", "stock", U, new string('d', 2001))), "description");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a%")]
    public void E1_Create_with_bad_code_reports_code(string? code)
    {
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput(code, "Name", "stock", U)), "code");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void E1_Create_with_bad_name_reports_name(string? name)
    {
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput("A1", name, "stock", U)), "name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stock")]
    [InlineData("goods")]
    [InlineData(" stock ")]
    public void E2_Create_with_bad_type_reports_type(string? type)
    {
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput("A1", "Name", type, U)), "type");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("kg")]
    public void E3_Create_with_bad_baseUnitId_reports_baseUnitId(string? baseUnitId)
    {
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput("A1", "Name", "stock", baseUnitId)), "baseUnitId");
    }

    [Fact]
    public void R13_Create_reports_all_invalid_fields_at_once()
    {
        AssertInvalid(ArticleValidation.Create(new CreateArticleInput(null, null, null, null)), "baseUnitId", "code", "name", "type");
        AssertInvalid(
            ArticleValidation.Create(new CreateArticleInput("a b", " ", "goods", "abc", new string('d', 2001))),
            "baseUnitId", "code", "description", "name", "type");
    }

    [Fact]
    public void R11_Replace_accepts_a_complete_input_with_a_text_or_null_description()
    {
        var withText = ArticleValidation.Replace(new ReplaceArticleInput(" ART-002 ", " Bolt M10 ", "service", U, false) { Description = " zinc " });
        var withNull = ArticleValidation.Replace(new ReplaceArticleInput("ART-002", "Bolt M10", "stock", U, true) { Description = null });

        Assert.True(withText.IsSuccess);
        Assert.Equal(new ArticleValues("ART-002", "Bolt M10", "zinc", ArticleType.Service, Unit, false), withText.Value);
        Assert.True(withNull.IsSuccess);
        Assert.Equal(new ArticleValues("ART-002", "Bolt M10", null, ArticleType.Stock, Unit, true), withNull.Value);
    }

    [Fact]
    public void R11_Replace_requires_all_six_fields_to_be_present()
    {
        AssertInvalid(ArticleValidation.Replace(new ReplaceArticleInput("A1", "Name", "stock", U, true)), "description");
        AssertInvalid(ArticleValidation.Replace(new ReplaceArticleInput(null, "Name", "stock", U, true) { Description = null }), "code");
        AssertInvalid(ArticleValidation.Replace(new ReplaceArticleInput("A1", null, "stock", U, true) { Description = null }), "name");
        AssertInvalid(ArticleValidation.Replace(new ReplaceArticleInput("A1", "Name", null, U, true) { Description = null }), "type");
        AssertInvalid(ArticleValidation.Replace(new ReplaceArticleInput("A1", "Name", "stock", null, true) { Description = null }), "baseUnitId");
        AssertInvalid(ArticleValidation.Replace(new ReplaceArticleInput("A1", "Name", "stock", U, null) { Description = null }), "isActive");
        AssertInvalid(
            ArticleValidation.Replace(new ReplaceArticleInput(null, null, null, null, null)),
            "baseUnitId", "code", "description", "isActive", "name", "type");
        AssertInvalid(
            ArticleValidation.Replace(new ReplaceArticleInput("A1", "Name", "stock", U, true) { Description = new string('d', 2001) }),
            "description");
    }

    [Fact]
    public void R18_List_defaults()
    {
        var result = ArticleValidation.List(new ListArticlesInput());

        Assert.True(result.IsSuccess);
        Assert.Equal(new ArticleListQuery(null, null, null, null, 50, 0), result.Value);
    }

    [Fact]
    public void R18_R19_List_applies_every_filter()
    {
        var result = ArticleValidation.List(new ListArticlesInput(" bolt ", "service", U, false, 500, 7));

        Assert.True(result.IsSuccess);
        Assert.Equal(new ArticleListQuery("bolt", ArticleType.Service, Unit, false, 500, 7), result.Value);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("%", "%")]
    public void R18_List_search_is_trimmed_and_empty_means_no_filter(string search, string? expected)
    {
        var result = ArticleValidation.List(new ListArticlesInput(Search: search));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value!.Search);
    }

    [Fact]
    public void R19_List_empty_type_or_baseUnitId_means_no_filter()
    {
        var result = ArticleValidation.List(new ListArticlesInput(Type: "", BaseUnitId: ""));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Type);
        Assert.Null(result.Value.BaseUnitId);
    }

    [Fact]
    public void E10_List_rejects_bad_filters_and_paging_together()
    {
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(Type: "goods")), "type");
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(Type: "Stock")), "type");
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(BaseUnitId: "abc")), "baseUnitId");
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(Search: new string('x', 101))), "search");
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(Limit: 0)), "limit");
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(Limit: 501)), "limit");
        AssertInvalid(ArticleValidation.List(new ListArticlesInput(Offset: -1)), "offset");
        AssertInvalid(
            ArticleValidation.List(new ListArticlesInput(new string('x', 101), "goods", "abc", null, 0, -1)),
            "baseUnitId", "limit", "offset", "search", "type");
    }
}
