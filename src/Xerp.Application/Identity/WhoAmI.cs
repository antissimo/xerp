using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Identity;

public sealed record WhoAmITenant(Guid Id, string Code, string Name);

public sealed record WhoAmIActor(Guid ApiKeyId, string Name, string ActorType);

public sealed record WhoAmIDto(WhoAmITenant Tenant, WhoAmIActor Actor);

/// <summary>Spec 001, 4.2: the tenant and actor behind the current credential.</summary>
public sealed class WhoAmIOperation(IXerpDb db, ITenantContext context)
{
    public async Task<Result<WhoAmIDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (context.TenantId is not { } tenantId || context.ApiKeyId is not { } apiKeyId)
            return AppError.Unauthenticated();

        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new WhoAmITenant(t.Id, t.Code, t.Name))
            .SingleOrDefaultAsync(cancellationToken);
        // ApiKeys is filtered to the current tenant, so a key id of another tenant is never found here.
        var key = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Id == apiKeyId)
            .Select(k => new { k.Id, k.Name, k.ActorType })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null || key is null)
            return AppError.Unauthenticated();

        return new WhoAmIDto(tenant, new WhoAmIActor(key.Id, key.Name, key.ActorType.ToName()));
    }
}
