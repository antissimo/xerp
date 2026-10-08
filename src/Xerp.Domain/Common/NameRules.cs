namespace Xerp.Domain.Common;

/// <summary>Rules for names (spec 001, R4).</summary>
public static class NameRules
{
    public const int MaxLength = 200;

    /// <summary>Trims the input and checks it against the name rules.</summary>
    public static bool TryNormalize(string? input, out string name) => TryNormalize(input, MaxLength, out name);

    public static bool TryNormalize(string? input, int maxLength, out string name)
    {
        name = input?.Trim() ?? "";
        return name.Length >= 1 && name.Length <= maxLength;
    }

    /// <summary>The normalised name, or an <see cref="ArgumentException"/> when the input breaks the rules.</summary>
    public static string Normalize(string? input, string paramName, int maxLength = MaxLength) =>
        TryNormalize(input, maxLength, out var name) ? name : throw new ArgumentException("Invalid name.", paramName);
}
