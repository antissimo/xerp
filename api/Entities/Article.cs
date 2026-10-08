using System.ComponentModel.DataAnnotations;

namespace Xerp.Api.Entities;

public class Article : Entity
{
    [MaxLength(2000)]
    public string? Description { get; set; }
}

public class ArticleInput : EntityInput
{
    [StringLength(2000)]
    public string? Description { get; set; }
}
