using System.Text.Json.Serialization;
using Xerp.Application.Common;
using Xerp.Domain.Catalog;

namespace Xerp.Application.Articles;

public sealed record ArticleDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string Type,
    ReferenceSummary BaseUnit,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy);

/// <summary>
/// <c>Type</c> and <c>BaseUnitId</c> are the raw strings of the request, so that a wrong value is reported
/// together with the other invalid fields. <c>Description</c> and <c>IsActive</c> are optional.
/// </summary>
public sealed record CreateArticleInput(
    string? Code, string? Name, string? Type, string? BaseUnitId, string? Description = null, bool? IsActive = null);

/// <summary>
/// All six fields must be present. <c>Description</c> may be null, but it must have been given:
/// setting it (to a text or to null) is what marks it as provided.
/// </summary>
public sealed record ReplaceArticleInput(string? Code, string? Name, string? Type, string? BaseUnitId, bool? IsActive)
{
    private readonly string? _description;

    public string? Description
    {
        get => _description;
        init
        {
            _description = value;
            DescriptionProvided = true;
        }
    }

    [JsonIgnore]
    public bool DescriptionProvided { get; private init; }
}

public sealed record ListArticlesInput(
    string? Search = null, string? Type = null, string? BaseUnitId = null, bool? IsActive = null,
    int? Limit = null, int? Offset = null, string? AlternativeUnitId = null);

/// <summary>Validated field values of an article. The base unit is well-formed, not yet known to exist.</summary>
public sealed record ArticleValues(string Code, string Name, string? Description, ArticleType Type, Guid BaseUnitId, bool IsActive);

/// <summary>Validated list query. <c>Search</c> is null when there is no text filter.</summary>
public sealed record ArticleListQuery(
    string? Search, ArticleType? Type, Guid? BaseUnitId, bool? IsActive, int Limit, int Offset, Guid? AlternativeUnitId = null);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record ArticleDeleted(bool Deleted = true);
