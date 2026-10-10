using Xerp.Application.Common;
using Xerp.Domain.Common;

namespace Xerp.Application.Warehouses;

public sealed record WarehouseDto(
    Guid Id,
    string Code,
    string Name,
    string? AddressLine1,
    string? AddressLine2,
    string? PostalCode,
    string? City,
    string? Region,
    string? CountryCode,
    bool IsActive,
    bool IsDefault,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy);

/// <summary>
/// The fields of a warehouse as a create or a replace carries them (nine with the address). Which of them
/// may be omitted differs between the two operations and is decided by <see cref="WarehouseValidation"/>.
/// </summary>
public sealed record WarehouseInput : AddressInput
{
    private readonly bool? _isActive;

    public string? Code { get; init; }
    public string? Name { get; init; }
    public bool? IsActive { get => _isActive; init => _isActive = Given(value); }
}

/// <param name="IsDefault">Spec 011: <c>true</c> returns exactly the tenant's default warehouse, <c>false</c> every other one.</param>
public sealed record ListWarehousesInput(
    string? Search = null, bool? IsActive = null, int? Limit = null, int? Offset = null, bool? IsDefault = null);

/// <summary>Validated field values of a warehouse.</summary>
public sealed record WarehouseValues(string Code, string Name, Address Address, bool IsActive);

/// <summary>Validated list query. <c>Search</c> is null when there is no text filter.</summary>
public sealed record WarehouseListQuery(string? Search, bool? IsActive, int Limit, int Offset, bool? IsDefault = null);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record WarehouseDeleted(bool Deleted = true);
