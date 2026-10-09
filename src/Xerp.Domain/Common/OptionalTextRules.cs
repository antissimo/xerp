namespace Xerp.Domain.Common;

/// <summary>
/// Rule for optional single-line text (ADR-0011, decision 7; spec 004, R3): trimmed; nothing, empty and
/// whitespace-only all mean "no value" (null); otherwise at most the field's maximum length and no
/// control characters, which includes line breaks and tabs.
/// </summary>
public static class OptionalTextRules
{
    /// <summary>Trims the input; empty becomes null. False when the result breaks the rule.</summary>
    public static bool TryNormalize(string? input, int maxLength, out string? value)
    {
        var trimmed = input?.Trim();
        value = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        return value is null || (value.Length <= maxLength && !TextRules.HasControlCharacters(value));
    }

    /// <summary>The normalised text, or an <see cref="ArgumentException"/> when the input breaks the rule.</summary>
    public static string? Normalize(string? input, int maxLength, string paramName) =>
        TryNormalize(input, maxLength, out var value) ? value : throw new ArgumentException("Invalid text.", paramName);
}
