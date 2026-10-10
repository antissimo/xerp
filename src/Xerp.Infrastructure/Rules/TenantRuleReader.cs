using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Infrastructure.Persistence;

namespace Xerp.Infrastructure.Rules;

/// <summary>
/// Reads the rules of the current tenant (ADR-0020, decision 10): one query for all of its values, every time
/// it is asked - no cache, so a value committed before the query is the value read. It uses the request's
/// <see cref="XerpDbContext"/>: the read is filtered to the tenant and, called inside a transaction that holds
/// the tenant's lock, is part of it.
/// </summary>
public sealed class TenantRuleReader(XerpDbContext db) : IRules
{
    public async Task<TenantRules> ReadAsync(CancellationToken cancellationToken = default) =>
        new(await db.RuleValues.AsNoTracking().ToDictionaryAsync(v => v.Key, v => v.Value, cancellationToken));
}
