using Xerp.Domain.Common;

namespace Xerp.Application.Common;

/// <summary>
/// The six address fields as a request carries them: flat, each a text or null (ADR-0011, decision 3).
/// Inputs of records that have an address derive from this, so the fields exist once.
/// </summary>
public abstract record AddressInput : TrackedInput
{
    private readonly string? _addressLine1;
    private readonly string? _addressLine2;
    private readonly string? _postalCode;
    private readonly string? _city;
    private readonly string? _region;
    private readonly string? _countryCode;

    public string? AddressLine1 { get => _addressLine1; init => _addressLine1 = Given(value); }
    public string? AddressLine2 { get => _addressLine2; init => _addressLine2 = Given(value); }
    public string? PostalCode { get => _postalCode; init => _postalCode = Given(value); }
    public string? City { get => _city; init => _city = Given(value); }
    public string? Region { get => _region; init => _region = Given(value); }
    public string? CountryCode { get => _countryCode; init => _countryCode = Given(value); }
}

/// <summary>Field rules shared by the masters of spec 004 (R3, R4, R7, R9). No I/O.</summary>
public static class FieldRules
{
    /// <summary>The name of a property as requests and <c>errors</c> spell it.</summary>
    public static string FieldName(string property) => char.ToLowerInvariant(property[0]) + property[1..];

    /// <summary>
    /// An optional text (R3). On replace the field must be present, though it may be null (R7): an omitted
    /// one is an error under its own name, not a silent clearing.
    /// </summary>
    public static string? OptionalText(ValidationErrors errors, TrackedInput input, string property, string? value, int maxLength, bool replace)
    {
        var field = FieldName(property);
        if (replace && !input.Has(property))
            errors.Add(field, $"{field} is required (it may be null).");
        else if (!OptionalTextRules.TryNormalize(value, maxLength, out var text))
            errors.Add(field, $"{field} must be at most {maxLength} characters on a single line, without control characters.");
        else
            return text;
        return null;
    }

    /// <summary>
    /// A boolean (R9): required on replace; on create an omitted one takes <paramref name="defaultValue"/>.
    /// Given as null it is a wrong value, never "omitted".
    /// </summary>
    public static bool Flag(ValidationErrors errors, TrackedInput input, string property, bool? value, bool defaultValue, bool replace)
    {
        var field = FieldName(property);
        if (!input.Has(property))
        {
            if (replace)
                errors.Add(field, $"{field} is required.");
            return defaultValue;
        }
        if (value is null)
            errors.Add(field, $"{field} must be true or false.");
        return value ?? defaultValue;
    }

    /// <summary>The six address fields (R3-R5); each reports under its own name.</summary>
    public static Address AddressOf(ValidationErrors errors, AddressInput input, bool replace)
    {
        var line1 = OptionalText(errors, input, nameof(input.AddressLine1), input.AddressLine1, Address.LineMaxLength, replace);
        var line2 = OptionalText(errors, input, nameof(input.AddressLine2), input.AddressLine2, Address.LineMaxLength, replace);
        var postalCode = OptionalText(errors, input, nameof(input.PostalCode), input.PostalCode, Address.PostalCodeMaxLength, replace);
        var city = OptionalText(errors, input, nameof(input.City), input.City, Address.CityMaxLength, replace);
        var region = OptionalText(errors, input, nameof(input.Region), input.Region, Address.RegionMaxLength, replace);

        const string field = "countryCode";
        string? countryCode = null;
        if (replace && !input.Has(nameof(input.CountryCode)))
            errors.Add(field, $"{field} is required (it may be null).");
        else if (!CountryCodeRules.TryNormalize(input.CountryCode, out countryCode))
        {
            errors.Add(field, $"{field} must be two upper-case letters (ISO 3166-1 alpha-2, for example \"HR\"), not a country name.");
            countryCode = null;
        }
        return Address.Create(line1, line2, postalCode, city, region, countryCode);
    }
}
