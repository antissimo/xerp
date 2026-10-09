using Xerp.Application.Common;
using Xerp.Domain.Common;

namespace Xerp.Application.Partners;

public sealed record PartnerDto(
    Guid Id,
    string Code,
    string Name,
    bool IsCustomer,
    bool IsSupplier,
    string? TaxId,
    string? AddressLine1,
    string? AddressLine2,
    string? PostalCode,
    string? City,
    string? Region,
    string? CountryCode,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid CreatedBy,
    Guid UpdatedBy);

/// <summary>
/// The fields of a partner as a create or a replace carries them (twelve with the address). Which of them
/// may be omitted differs between the two operations and is decided by <see cref="PartnerValidation"/>.
/// </summary>
public sealed record PartnerInput : AddressInput
{
    private readonly bool? _isCustomer;
    private readonly bool? _isSupplier;
    private readonly string? _taxId;
    private readonly bool? _isActive;

    public string? Code { get; init; }
    public string? Name { get; init; }
    public bool? IsCustomer { get => _isCustomer; init => _isCustomer = Given(value); }
    public bool? IsSupplier { get => _isSupplier; init => _isSupplier = Given(value); }
    public string? TaxId { get => _taxId; init => _taxId = Given(value); }
    public bool? IsActive { get => _isActive; init => _isActive = Given(value); }
}

public sealed record ListPartnersInput(
    string? Search = null, bool? IsCustomer = null, bool? IsSupplier = null, bool? IsActive = null,
    int? Limit = null, int? Offset = null);

/// <summary>Validated field values of a partner.</summary>
public sealed record PartnerValues(
    string Code, string Name, bool IsCustomer, bool IsSupplier, string? TaxId, Address Address, bool IsActive);

/// <summary>Validated list query. <c>Search</c> is null when there is no text filter.</summary>
public sealed record PartnerListQuery(string? Search, bool? IsCustomer, bool? IsSupplier, bool? IsActive, int Limit, int Offset);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record PartnerDeleted(bool Deleted = true);
