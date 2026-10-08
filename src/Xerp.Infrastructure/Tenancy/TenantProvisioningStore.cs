using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Tenancy;
using Xerp.Infrastructure.Persistence;

namespace Xerp.Infrastructure.Tenancy;

public sealed class TenantProvisioningStore(DbContextOptions<XerpDbContext> options, XerpDbContext db) : ITenantProvisioningStore
{
    private sealed record NewTenantContext(Guid? TenantId) : ITenantContext
    {
        public Guid? ApiKeyId => null;
    }

    public async Task AddAsync(Tenant tenant, ApiKey firstKey, CancellationToken cancellationToken = default)
    {
        // The admin has no tenant. The first key is written through a context whose tenant is the new
        // tenant, so the usual cross-tenant write check applies to it instead of being bypassed.
        await using var scoped = new XerpDbContext(options, new NewTenantContext(tenant.Id));
        scoped.Tenants.Add(tenant);
        scoped.ApiKeys.Add(firstKey);
        await scoped.SaveChangesAsync(cancellationToken); // one SaveChanges = one transaction (R12)
    }

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) =>
        db.Tenants.AnyAsync(t => EF.Property<string>(t, DbNames.CodeLower) == DbText.Lower(code), cancellationToken);
}
