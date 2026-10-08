using System.Security.Cryptography;
using System.Text;

namespace Xerp.Application.Identity;

/// <summary>The platform admin key rule (spec 001, S3).</summary>
public static class AdminKey
{
    public const int MinLength = 32;

    /// <summary>
    /// True when <paramref name="configured"/> is a usable admin key (at least <see cref="MinLength"/>
    /// characters) and <paramref name="presented"/> equals it. Compared in constant time.
    /// </summary>
    public static bool Matches(string? configured, string? presented)
    {
        if (configured is null || configured.Length < MinLength || string.IsNullOrEmpty(presented))
            return false;
        // Hashing first makes both operands the same length, so the comparison time does not depend
        // on the length of the configured key either.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }
}
