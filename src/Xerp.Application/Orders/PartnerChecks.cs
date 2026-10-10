using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>What a document needs to know about the partner it names: whether it is active and has the role the document asks for.</summary>
public sealed record PartnerState(bool IsActive, bool HasRole);

/// <summary>
/// The role rule of a partner on a document (spec 009, R2; spec 010, R3; spec 011a, R6), written once: the
/// purchase side - a purchase order and a receipt - needs a supplier, the sales side - a sales order and an
/// issue - a customer. Orders and stock documents both ask here.
/// </summary>
public static class PartnerChecks
{
    /// <summary>The partner of the current tenant with this id, as the given side sees it; empty when there is none.</summary>
    public static IQueryable<PartnerState> State(IXerpDb db, Guid partnerId, OrderSide side)
    {
        var customer = side == OrderSide.Sales;
        return db.Partners.AsNoTracking()
            .Where(p => p.Id == partnerId)
            .Select(p => new PartnerState(p.IsActive, customer ? p.IsCustomer : p.IsSupplier));
    }

    /// <summary>The partner exists and may be used, but does not have the role this side needs.</summary>
    /// <param name="field">The request field that names the partner; it keys <c>errors</c>.</param>
    /// <param name="document">What needs the role, with its article: <c>a purchase order</c>, <c>an issue</c>.</param>
    public static AppError RoleMissing(OrderKind kind, string field, string document, Guid partnerId) =>
        new(ErrorCodes.PartnerRoleMissing,
            $"The partner named by {field} is not a {kind.PartnerWord}: {document} needs a partner with {kind.PartnerRole} = true. "
            + $"Choose a {kind.PartnerWord} (partner_list with {kind.PartnerRole} = true), or give this partner the role with partner_update.",
            new Dictionary<string, string[]> { [field] = [$"The partner with id '{partnerId}' does not have {kind.PartnerRole}."] });
}
