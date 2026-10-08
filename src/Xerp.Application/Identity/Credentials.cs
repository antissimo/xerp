using Xerp.Application.Ports;

namespace Xerp.Application.Identity;

/// <summary>The configured platform admin key (<c>Xerp:AdminKey</c>); null or too short means "not configured".</summary>
public sealed record AdminKeySetting(string? Value)
{
    public bool IsConfigured => Value is not null && Value.Length >= AdminKey.MinLength;
}

/// <summary>Who a bearer token turned out to be.</summary>
public abstract record Credential;

/// <summary>The platform admin: no tenant, admin routes only.</summary>
public sealed record AdminCredential : Credential;

/// <summary>An active API key of an active tenant.</summary>
public sealed record TenantCredential(ApiKeyRecord Key) : Credential;

/// <summary>
/// Turns a bearer token into a credential (spec 001, S2-S3). Shared by every transport (HTTP now, MCP later),
/// so they cannot disagree about who is authenticated.
/// </summary>
public sealed class CredentialResolver(AdminKeySetting adminKey, IApiKeyHasher hasher, IApiKeyLookup lookup)
{
    // Longer than any key this system issues or accepts as admin key in practice; avoids hashing junk.
    private const int MaxTokenLength = 512;

    /// <returns>Null when the token is missing, unknown, or belongs to a disabled key or tenant.</returns>
    public async Task<Credential?> ResolveAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
            return null;
        if (AdminKey.Matches(adminKey.Value, token))
            return new AdminCredential();

        var key = await lookup.FindByHashAsync(hasher.Hash(token), cancellationToken);
        if (key is null || !key.IsActive || !key.TenantIsActive)
            return null;
        return new TenantCredential(key);
    }
}
