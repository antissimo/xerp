namespace Xerp.Domain.Common;

/// <summary>Rules for master-data codes (spec 001, R1-R3).</summary>
public static class CodeRules
{
    public const int MaxLength = 50;

    /// <summary>Trims the input and checks it against the code rules.</summary>
    public static bool TryNormalize(string? input, out string code) => throw new NotImplementedException();
}
