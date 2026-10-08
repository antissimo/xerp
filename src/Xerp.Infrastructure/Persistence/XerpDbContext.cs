using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Tenancy;

namespace Xerp.Infrastructure.Persistence;

public sealed class XerpDbContext(DbContextOptions<XerpDbContext> options, ITenantContext tenant)
    : DbContext(options), IXerpDb
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    private Guid? CurrentTenantId => tenant.TenantId;
}
