namespace Xerp.Application.UnitsOfMeasure;

public sealed record UnitOfMeasureDto(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy);

/// <summary><c>IsActive</c> is optional: absent means active.</summary>
public sealed record CreateUnitOfMeasureInput(string? Code, string? Name, bool? IsActive);

/// <summary>All three fields are required (a replace never silently re-activates a unit).</summary>
public sealed record ReplaceUnitOfMeasureInput(string? Code, string? Name, bool? IsActive);

public sealed record ListUnitsOfMeasureInput(string? Search = null, bool? IsActive = null, int? Limit = null, int? Offset = null);

/// <summary>Validated field values of a unit of measure.</summary>
public sealed record UnitOfMeasureValues(string Code, string Name, bool IsActive);

/// <summary>Validated list query. <c>Search</c> is null when there is no text filter.</summary>
public sealed record UnitOfMeasureListQuery(string? Search, bool? IsActive, int Limit, int Offset);
