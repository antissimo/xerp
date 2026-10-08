using System.Text.RegularExpressions;

namespace Xerp.Domain.Common;

/// <summary>Rules for master-data codes (spec 001, R1-R3).</summary>
public static partial class CodeRules
{
    public const int MaxLength = 50;

    [GeneratedRegex(@"^[\p{L}\p{N}._-]{1,50}\z")]
    private static partial Regex Pattern();

    /// <summary>Trims the input and checks it against the code rules.</summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = input?.Trim() ?? "";
        return Pattern().IsMatch(code);
    }

    /// <summary>The normalised code, or an <see cref="ArgumentException"/> when the input breaks the rules.</summary>
    public static string Normalize(string? input, string paramName) =>
        TryNormalize(input, out var code) ? code : throw new ArgumentException("Invalid code.", paramName);
}
