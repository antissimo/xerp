using System.Text.RegularExpressions;

namespace Xerp.Domain.Common;

/// <summary>
/// Rule for a country code (ADR-0011, decision 4; spec 004, R4): the form of ISO 3166-1 alpha-2, exactly two
/// upper-case ASCII letters. It is not checked against a list of countries and lower case is not converted.
/// </summary>
public static partial class CountryCodeRules
{
    public const int Length = 2;

    [GeneratedRegex(@"^[A-Z]{2}\z")]
    private static partial Regex Pattern();

    /// <summary>Trims the input; empty becomes null. False when the result is not two upper-case ASCII letters.</summary>
    public static bool TryNormalize(string? input, out string? countryCode)
    {
        var trimmed = input?.Trim();
        countryCode = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        return countryCode is null || Pattern().IsMatch(countryCode);
    }
}

/// <summary>
/// The postal address of a record (ADR-0011, decision 3): six optional, independent fields. An instance
/// always satisfies the rules of its fields; the same type is used wherever a record has an address.
/// </summary>
public sealed record Address
{
    public const int LineMaxLength = 200;
    public const int PostalCodeMaxLength = 20;
    public const int CityMaxLength = 100;
    public const int RegionMaxLength = 100;

    private Address() { }

    /// <summary>No field has a value.</summary>
    public static Address Empty { get; } = new();

    public string? Line1 { get; private init; }
    public string? Line2 { get; private init; }
    public string? PostalCode { get; private init; }
    public string? City { get; private init; }
    public string? Region { get; private init; }
    public string? CountryCode { get; private init; }

    /// <summary>The address with every field normalised, or an <see cref="ArgumentException"/> naming the field that breaks its rule.</summary>
    public static Address Create(string? line1, string? line2, string? postalCode, string? city, string? region, string? countryCode) =>
        new()
        {
            Line1 = OptionalTextRules.Normalize(line1, LineMaxLength, nameof(line1)),
            Line2 = OptionalTextRules.Normalize(line2, LineMaxLength, nameof(line2)),
            PostalCode = OptionalTextRules.Normalize(postalCode, PostalCodeMaxLength, nameof(postalCode)),
            City = OptionalTextRules.Normalize(city, CityMaxLength, nameof(city)),
            Region = OptionalTextRules.Normalize(region, RegionMaxLength, nameof(region)),
            CountryCode = CountryCodeRules.TryNormalize(countryCode, out var normalized)
                ? normalized
                : throw new ArgumentException("Invalid country code.", nameof(countryCode)),
        };
}
