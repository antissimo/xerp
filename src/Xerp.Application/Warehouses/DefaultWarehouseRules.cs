using Xerp.Application.Common;

namespace Xerp.Application.Warehouses;

/// <summary>
/// The rules of the default warehouse (ADR-0019; spec 011, R2, R4-R6, R8-R10). No I/O: the operations read
/// the facts under the tenant's lock and ask here. A tenant always has exactly one default warehouse and it
/// is always active, so nothing may deactivate or delete it, and only an active warehouse may take its place.
/// </summary>
public static class DefaultWarehouseRules
{
    public const string WarehouseField = "warehouseId";
    private const string IsActiveField = "isActive";

    /// <summary>R4: null unless a replace would deactivate the default warehouse - then DEFAULT_WAREHOUSE with key <c>isActive</c>.</summary>
    /// <param name="isDefault">Whether the addressed warehouse is the default.</param>
    /// <param name="isActive">What the replace sets <c>isActive</c> to.</param>
    public static AppError? Replace(bool isDefault, bool isActive) =>
        isDefault && !isActive
            ? new AppError(ErrorCodes.DefaultWarehouse,
                "This is the tenant's default warehouse and cannot be deactivated: documents created without a warehouse go to it. "
                + "Make another warehouse the default first (`warehouse_set_default`, `POST /warehouses/{id}/set-default`), then deactivate this one. Nothing was changed.",
                new Dictionary<string, string[]> { [IsActiveField] = ["The default warehouse must stay active."] })
            : null;

    /// <summary>R5: null unless the default warehouse would be deleted - then DEFAULT_WAREHOUSE, whether or not anything uses it.</summary>
    public static AppError? Delete(bool isDefault) =>
        isDefault
            ? new AppError(ErrorCodes.DefaultWarehouse,
                "This is the tenant's default warehouse and cannot be deleted: a tenant always has one. "
                + "Make another warehouse the default first (`warehouse_set_default`, `POST /warehouses/{id}/set-default`), then delete this one.")
            : null;

    /// <summary>R6: null when the warehouse may become the default; an inactive one may not - DEFAULT_WAREHOUSE with key <c>isActive</c>.</summary>
    public static AppError? SetDefault(bool isActive) =>
        isActive
            ? null
            : new AppError(ErrorCodes.DefaultWarehouse,
                "The warehouse is inactive and cannot be the default warehouse: the default must be usable on new documents. "
                + "Reactivate it first (`warehouse_update` with `isActive` true), then make it the default. The default warehouse is unchanged.",
                new Dictionary<string, string[]> { [IsActiveField] = ["The warehouse is inactive."] });

    /// <summary>
    /// R8, R10: the warehouse a new document is on. The one the caller named; without one, the warehouse of
    /// the order the document is linked to - the only warehouse that link allows; otherwise the tenant's
    /// default warehouse at this moment. Resolved once and stored: afterwards nothing remembers that it was
    /// defaulted.
    /// </summary>
    /// <param name="named">The <c>warehouseId</c> of the request; null when omitted or <c>null</c>.</param>
    /// <param name="orderWarehouseId">The warehouse of the linked order; null for an unlinked document and for an order.</param>
    public static Guid ForNewDocument(Guid? named, Guid? orderWarehouseId, Guid defaultWarehouseId) =>
        named ?? orderWarehouseId ?? defaultWarehouseId;

    /// <summary>
    /// The form of <c>warehouseId</c> in a request (R8, R9, E8): a UUID names a warehouse; a missing or
    /// <c>null</c> one is "none named" where that is allowed (<paramref name="optional"/>) and an error where
    /// it is not; anything else - an empty string included - is malformed.
    /// </summary>
    /// <returns>The named warehouse; null when none was named or the value is invalid (then an error was added).</returns>
    public static Guid? Named(ValidationErrors errors, string? input, bool optional)
    {
        if (Guid.TryParse(input, out var id))
            return id;
        if (input is null && optional)
            return null;
        errors.Add(WarehouseField, string.IsNullOrEmpty(input)
            ? $"{WarehouseField} is required."
            : $"{WarehouseField} must be the id (UUID) of a warehouse, not its code.");
        return null;
    }
}
