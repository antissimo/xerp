using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>A master the header of a document names, as it was read: where the request names it, and whether it exists and is active.</summary>
/// <param name="Field">The request field that carries the id; it keys <c>errors</c>.</param>
/// <param name="IsActive">Null when the tenant has no such record.</param>
/// <param name="AlreadyAssigned">True when the stored draft already names this record in that place: it may stay although inactive.</param>
public readonly record struct HeaderReference(string Field, Guid Id, bool? IsActive, bool AlreadyAssigned = false);

/// <summary>
/// The rules of the partner of a stock document (spec 011a): which types have one, the header references of
/// an unlinked document and the role its partner needs. No I/O: the operations read the facts and ask here.
/// The partner of a linked document is its order's and is only compared: <see cref="OrderLinkChecks.Agreement"/>.
/// </summary>
public static class StockPartnerChecks
{
    public const string PartnerField = "partnerId";

    /// <summary>
    /// R1, R3: null when a document of <paramref name="type"/> may be saved with this <c>partnerId</c>, otherwise
    /// the message for key <c>partnerId</c>. A transfer and a count have no partner; a receipt and an issue
    /// must say whether they have one when they are replaced.
    /// </summary>
    /// <param name="given">False when the request left the property out; a create, where that equals null, passes true.</param>
    public static string? OfType(StockDocumentType type, Guid? partnerId, bool given)
    {
        if (!StockDocument.CanNamePartner(type))
            return partnerId is null
                ? null
                : $"partnerId is only for a receipt (its supplier) or an issue (its customer); for a {type.ToName()} leave it out or pass null.";
        return given
            ? null
            : $"partnerId is required when {(type == StockDocumentType.Issue ? "an" : "a")} {type.ToName()} is replaced (it may be null): null means no partner - on a document linked to an order, the order's partner.";
    }

    /// <summary>The kind of order on the same side as the type: its partner word and role are the document's (R6). Only for a type that can name a partner.</summary>
    public static OrderKind Side(StockDocumentType type) =>
        OrderKind.All.Single(k => k.FulfilledBy == type);

    /// <summary>
    /// R7, the header stage of saving: null when every master the header names may be assigned; otherwise
    /// REFERENCE_NOT_FOUND with the keys of all that do not exist, and only when all exist REFERENCE_INACTIVE
    /// with the keys of all that are inactive and newly assigned (ADR-0008).
    /// </summary>
    public static AppError? HeaderReferences(params HeaderReference[] references)
    {
        var missing = references.Where(r => r.IsActive is null).ToList();
        if (missing.Count == 1)
            return AppError.ReferenceNotFound(missing[0].Field, missing[0].Id);
        if (missing.Count > 1)
            return new AppError(ErrorCodes.ReferenceNotFound,
                $"The records referenced by {Names(missing)} do not exist.",
                missing.ToDictionary(r => r.Field, r => new[] { $"No record with id '{r.Id}' exists." }, StringComparer.Ordinal));

        var inactive = references.Where(r => r.IsActive == false && !r.AlreadyAssigned).ToList();
        if (inactive.Count == 1)
            return AppError.ReferenceInactive(inactive[0].Field, inactive[0].Id);
        if (inactive.Count > 1)
            return new AppError(ErrorCodes.ReferenceInactive,
                $"The records referenced by {Names(inactive)} are inactive and cannot be newly assigned.",
                inactive.ToDictionary(r => r.Field, r => new[] { $"The record with id '{r.Id}' is inactive." }, StringComparer.Ordinal));
        return null;
    }

    /// <summary>
    /// R6, R12: null when the partner of an unlinked receipt is a supplier, of an unlinked issue a customer;
    /// otherwise PARTNER_ROLE_MISSING with key <c>partnerId</c>. Asked on every save and at posting, also when
    /// the partner is kept.
    /// </summary>
    public static AppError? Role(StockDocumentType type, Guid partnerId, bool hasRole) =>
        hasRole
            ? null
            : PartnerChecks.RoleMissing(Side(type), PartnerField, $"{(type == StockDocumentType.Issue ? "an" : "a")} {type.ToName()}", partnerId);

    private static string Names(IEnumerable<HeaderReference> references) => string.Join(" and ", references.Select(r => r.Field));
}
