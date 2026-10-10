using Xerp.Domain.Inventory;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Ports;

public interface IApiKeyGenerator
{
    /// <summary>A new secret: <c>xerp_</c> + 43 base64url characters (32 random bytes).</summary>
    string Generate();
}

public interface IApiKeyHasher
{
    /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of the full key string.</summary>
    string Hash(string key);
}

/// <summary>What the authentication lookup returns for a stored key hash.</summary>
public sealed record ApiKeyRecord(
    Guid ApiKeyId,
    string Name,
    ActorType ActorType,
    bool IsActive,
    Guid TenantId,
    bool TenantIsActive);

/// <summary>
/// The one place that reads API keys without a tenant filter: authentication, before a tenant is known.
/// </summary>
public interface IApiKeyLookup
{
    Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken = default);
}

/// <summary>Writes a new tenant together with its first API key and its default warehouse in one transaction.</summary>
public interface ITenantProvisioningStore
{
    /// <exception cref="UniqueConstraintViolationException">The tenant code is already used.</exception>
    Task AddAsync(Tenant tenant, ApiKey firstKey, Warehouse defaultWarehouse, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default);
}
