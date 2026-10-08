using Xerp.Application.Common;

namespace Xerp.Application.Articles;

/// <summary>Input rules for articles (spec 002, R1-R5, R10, R11, R18, R19). No I/O.</summary>
public static class ArticleValidation
{
    public static Result<ArticleValues> Create(CreateArticleInput input) => throw new NotImplementedException();

    public static Result<ArticleValues> Replace(ReplaceArticleInput input) => throw new NotImplementedException();

    public static Result<ArticleListQuery> List(ListArticlesInput input) => throw new NotImplementedException();
}
