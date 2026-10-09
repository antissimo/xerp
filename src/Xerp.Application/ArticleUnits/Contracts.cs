using Xerp.Application.Common;

namespace Xerp.Application.ArticleUnits;

/// <summary>
/// A unit conversion of an article (spec 007, 4.1), read as: 1 <c>Unit</c> = <c>Factor</c> × <c>BaseUnit</c>.
/// The summaries carry the current code and name of the article and of both units.
/// </summary>
public sealed record ArticleUnitDto(
    ReferenceSummary Article,
    ReferenceSummary Unit,
    decimal Factor,
    ReferenceSummary BaseUnit,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy);

/// <summary>The outcome of a Set (R3): the conversion, and whether this call created it (HTTP: 201) or replaced its factor (200).</summary>
public sealed record ArticleUnitSet(ArticleUnitDto ArticleUnit, bool Created);

/// <summary>The body of a Set: the article and the unit are in the address, never in the body (E1).</summary>
public sealed record SetArticleUnitInput(decimal? Factor = null);

public sealed record ListArticleUnitsInput(int? Limit = null, int? Offset = null);

/// <summary>How a client without a URL path (an MCP tool) names one conversion: by the ids of its article and its unit.</summary>
public sealed record ArticleUnitAddressInput(string? ArticleId = null, string? UnitId = null);

/// <summary>The arguments of a Set without a URL path: the address and the body in one object.</summary>
public sealed record SetArticleUnitArguments(string? ArticleId = null, string? UnitId = null, decimal? Factor = null);

/// <summary>The arguments of a List without a URL path.</summary>
public sealed record ListArticleUnitsArguments(string? ArticleId = null, int? Limit = null, int? Offset = null);

/// <summary>Validated paging of a list.</summary>
public sealed record ArticleUnitListQuery(int Limit, int Offset);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record ArticleUnitDeleted(bool Deleted = true);
