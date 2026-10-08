using Microsoft.AspNetCore.Mvc;
using Xerp.Api.Data;
using Xerp.Api.Entities;

namespace Xerp.Api.Controllers;

[Route("articles")]
public class ArticlesController(AppDb db) : CrudController<Article, ArticleInput>(db)
{
    protected override void Apply(ArticleInput input, Article entity)
    {
        base.Apply(input, entity);
        entity.Description = input.Description;
    }
}
