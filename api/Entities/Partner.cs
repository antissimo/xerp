using System.ComponentModel.DataAnnotations;

namespace Xerp.Api.Entities;

/// <summary>Another company we buy from.</summary>
public class Partner : Entity
{
    /// <summary>OIB for Croatian partners.</summary>
    [MaxLength(20)]
    public string? TaxId { get; set; }

    [MaxLength(200)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2, e.g. "HR".</summary>
    [MaxLength(2)]
    public string? CountryCode { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }
}

public class PartnerInput : EntityInput
{
    [StringLength(20)]
    public string? TaxId { get; set; }

    [StringLength(200)]
    public string? Address { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(20)]
    public string? PostalCode { get; set; }

    [StringLength(2, MinimumLength = 2)]
    public string? CountryCode { get; set; }

    [EmailAddress, StringLength(200)]
    public string? Email { get; set; }

    [StringLength(50)]
    public string? Phone { get; set; }
}
