using Microsoft.EntityFrameworkCore;

namespace Xerp.Application.Common;

/// <summary>
/// The reference rule of ADR-0008 (decisions 3 and 4), shared by every record that points at another one.
/// </summary>
public static class ReferenceCheck
{
    /// <summary>
    /// Checks that the record a request field points at may be assigned.
    /// </summary>
    /// <param name="isActiveOfTarget">
    /// The <c>IsActive</c> flag of the referenced record, selected through the tenant-filtered context
    /// (for example <c>db.UnitsOfMeasure.Where(u => u.Id == id).Select(u => u.IsActive)</c>). A record of
    /// another tenant is therefore simply absent.
    /// </param>
    /// <param name="field">The request field that carries the id (<c>baseUnitId</c>); it keys <c>errors</c>.</param>
    /// <param name="id">The referenced id.</param>
    /// <param name="alreadyAssigned">True when the addressed record already points at this id: an inactive target is then kept.</param>
    /// <returns>Null when the reference is acceptable; otherwise REFERENCE_NOT_FOUND or REFERENCE_INACTIVE.</returns>
    public static async Task<AppError?> ValidateAsync(
        IQueryable<bool> isActiveOfTarget, string field, Guid id, bool alreadyAssigned, CancellationToken cancellationToken)
    {
        var isActive = await isActiveOfTarget.Select(active => (bool?)active).SingleOrDefaultAsync(cancellationToken);
        if (isActive is null)
            return AppError.ReferenceNotFound(field, id);
        if (isActive == false && !alreadyAssigned)
            return AppError.ReferenceInactive(field, id);
        return null;
    }
}
