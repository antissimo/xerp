using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Tenancy;
using Xerp.Infrastructure.Persistence;

namespace Xerp.Infrastructure.Tenancy;

public sealed class TenantProvisioningStore(DbContextOptions<XerpDbContext> options, XerpDbContext db) : ITenantProvisioningStore
{
    private sealed record NewTenantContext(Guid? TenantId) : ITenantContext
    {
        public Guid? ApiKeyId => null;
    }

    public async Task AddAsync(Tenant tenant, ApiKey firstKey, Warehouse defaultWarehouse, CancellationToken cancellationToken = default)
    {
        // The admin has no tenant. The first key and the default warehouse are written through a context whose
        // tenant is the new tenant, so the usual stamping and cross-tenant write check apply to them instead
        // of being bypassed.
        await using var scoped = new XerpDbContext(options, new NewTenantContext(tenant.Id));
        scoped.Tenants.Add(tenant);
        scoped.ApiKeys.Add(firstKey);
        scoped.Warehouses.Add(defaultWarehouse);
        await scoped.SaveChangesAsync(cancellationToken); // one SaveChanges = one transaction (R12; spec 011, R1)
    }

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) =>
        db.Tenants.AnyAsync(t => EF.Property<string>(t, DbNames.CodeLower) == DbText.Lower(code), cancellationToken);
}
