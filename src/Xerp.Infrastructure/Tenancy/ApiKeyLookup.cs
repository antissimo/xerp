using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Infrastructure.Persistence;

namespace Xerp.Infrastructure.Tenancy;

public sealed class ApiKeyLookup(XerpDbContext db) : IApiKeyLookup
{
    public Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken = default) =>
        // The only IgnoreQueryFilters() in the code base (spec 001, T3): the tenant is what this lookup finds out.
        db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.KeyHash == keyHash)
            .Join(db.Tenants, k => k.TenantId, t => t.Id,
                (k, t) => new ApiKeyRecord(k.Id, k.Name, k.ActorType, k.IsActive, k.TenantId, t.IsActive))
            .SingleOrDefaultAsync(cancellationToken);
}
