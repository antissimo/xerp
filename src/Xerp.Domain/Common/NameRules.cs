namespace Xerp.Domain.Common;

/// <summary>Rules for names (spec 001, R4).</summary>
public static class NameRules
{
    public const int MaxLength = 200;

    /// <summary>Trims the input and checks it against the name rules.</summary>
    public static bool TryNormalize(string? input, out string name) => throw new NotImplementedException();
}
