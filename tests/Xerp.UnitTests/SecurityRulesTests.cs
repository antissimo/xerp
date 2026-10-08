using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xerp.Application.Identity;
using Xerp.Infrastructure.Security;

namespace Xerp.UnitTests;

public class SecurityRulesTests
{
    private const string Key32 = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void S3_Admin_key_matches_only_the_exact_configured_value()
    {
        Assert.True(AdminKey.Matches(Key32, Key32));
        Assert.True(AdminKey.Matches(Key32 + "-longer", Key32 + "-longer"));
        Assert.False(AdminKey.Matches(Key32, Key32 + "x"));
        Assert.False(AdminKey.Matches(Key32, Key32[..31]));
        Assert.False(AdminKey.Matches(Key32, Key32.ToUpperInvariant()));
        Assert.False(AdminKey.Matches(Key32, ""));
        Assert.False(AdminKey.Matches(Key32, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")] // 31 characters
    public void S3_Admin_key_that_is_absent_empty_or_shorter_than_32_is_not_configured(string? configured)
    {
        Assert.False(AdminKey.Matches(configured, configured));
        Assert.False(AdminKey.Matches(configured, ""));
        Assert.False(AdminKey.Matches(configured, null));
    }

    [Fact]
    public void R13_Generated_key_has_the_documented_format_and_is_random()
    {
        var crypto = new ApiKeyCrypto();

        var keys = Enumerable.Range(0, 50).Select(_ => crypto.Generate()).ToList();

        Assert.All(keys, key => Assert.Matches(new Regex("^xerp_[A-Za-z0-9_-]{43}$"), key));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void S5_Hash_is_lower_case_hex_sha256_of_the_utf8_key()
    {
        var crypto = new ApiKeyCrypto();
        var key = crypto.Generate();
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

        Assert.Equal(expected, crypto.Hash(key));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", crypto.Hash("abc"));
    }
}
