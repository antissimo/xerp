using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Infrastructure.Persistence;

namespace Xerp.Infrastructure.Tenancy;

public sealed class CurrentTenantReader(XerpDbContext db, ITenantContext context) : ICurrentTenantReader
{
    public async Task<CurrentTenant?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (context.TenantId is not { } tenantId)
            return null;
        return await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new CurrentTenant(t.Id, t.Code, t.Name))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
