namespace Xerp.Domain.Common;

/// <summary>
/// Marker for every entity that belongs to exactly one tenant. The DbContext filters, stamps and
/// checks <see cref="TenantId"/> for every entity that implements it (docs/architecture.md section 3).
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
