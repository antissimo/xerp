namespace Xerp.Application.Identity;

/// <summary>The platform admin key rule (spec 001, S3).</summary>
public static class AdminKey
{
    public const int MinLength = 32;

    /// <summary>
    /// True when <paramref name="configured"/> is a usable admin key (at least <see cref="MinLength"/>
    /// characters) and <paramref name="presented"/> equals it. Compared in constant time.
    /// </summary>
    public static bool Matches(string? configured, string? presented) => throw new NotImplementedException();
}
