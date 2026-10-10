using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;

namespace Xerp.Application.Warehouses;

/// <summary>Reads of warehouses that documents share. <see cref="IXerpDb"/> is already filtered to the current tenant.</summary>
internal static class WarehouseReads
{
    /// <summary>
    /// The current tenant's default warehouse (spec 011, R2: there is exactly one). Called under the tenant's
    /// lock, which set-default takes too, so it is the default of the moment the document is created (R8).
    /// </summary>
    public static async Task<Guid> DefaultIdAsync(IXerpDb db, CancellationToken cancellationToken)
    {
        var ids = await db.Warehouses.AsNoTracking().Where(w => w.IsDefault).Select(w => w.Id).ToListAsync(cancellationToken);
        return ids.Count == 1
            ? ids[0]
            : throw new InvalidOperationException($"The tenant has {ids.Count} default warehouses; it must have exactly one.");
    }
}
