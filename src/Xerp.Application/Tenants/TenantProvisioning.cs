using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Tenants;

public sealed record CreateTenantInput(string? Code, string? Name);

public sealed record TenantDto(Guid Id, string Code, string Name, bool IsActive, DateTime CreatedAt);

/// <summary><c>Key</c> is the plaintext secret. This is the only place it is ever returned (R13).</summary>
public sealed record IssuedApiKeyDto(Guid Id, string Name, string ActorType, string Key);

public sealed record TenantCreatedDto(TenantDto Tenant, IssuedApiKeyDto ApiKey);

/// <summary>Spec 001, 4.1: creates a tenant together with its first API key (R12).</summary>
public sealed class TenantProvisioning(
    ITenantProvisioningStore store,
    IApiKeyGenerator keyGenerator,
    IApiKeyHasher keyHasher,
    IClock clock)
{
    public const string InitialKeyName = "initial";

    public async Task<Result<TenantCreatedDto>> CreateAsync(CreateTenantInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        if (errors.Any)
            return errors.ToError();

        if (await store.CodeExistsAsync(code, cancellationToken))
            return CodeTaken(code);

        var now = clock.UtcNow;
        var tenant = Tenant.Create(code, name, now);
        var secret = keyGenerator.Generate();
        var key = ApiKey.Create(tenant.Id, InitialKeyName, ActorType.Human, keyHasher.Hash(secret), now);
        try
        {
            await store.AddAsync(tenant, key, cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.ConstraintName == DbNames.TenantCodeIndex)
        {
            // Lost a race with a concurrent create of the same code: the unique index is the authority.
            return CodeTaken(code);
        }

        return new TenantCreatedDto(
            new TenantDto(tenant.Id, tenant.Code, tenant.Name, tenant.IsActive, tenant.CreatedAt),
            new IssuedApiKeyDto(key.Id, key.Name, key.ActorType.ToName(), secret));
    }

    private static AppError CodeTaken(string code) => AppError.CodeTaken($"A tenant with code '{code}' already exists.");
}
