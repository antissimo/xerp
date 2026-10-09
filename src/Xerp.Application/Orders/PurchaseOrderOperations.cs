using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Application.Stock;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Orders;

/// <summary>
/// The purchase order operations of spec 009, section 4.1. One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// Every write of an order runs inside <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>, the lock every
/// posting and reversal of a stock document takes too (ADR-0012, amendment of 2026-10-09). So a confirmation
/// freezes the factors it read and takes the next number without a gap; a posting that races with a close
/// either precedes it or finds the order closed (R30); and a conversion a draft line uses cannot be deleted
/// while the draft is being saved.
/// </para>
/// <para>
/// An order never writes the stock ledger. What a receipt posted against it changes is the received quantity
/// of its lines, written by <see cref="StockDocumentOperations"/> in the posting and reversal transactions.
/// </para>
/// </summary>
public sealed class PurchaseOrderOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    private static readonly OrderKind Kind = OrderKind.Purchase;
    private const string WarehouseField = "warehouseId";

    public async Task<Result<PagedResult<PurchaseOrderSummaryDto>>> ListAsync(ListPurchaseOrdersInput input, CancellationToken cancellationToken = default)
    {
        var validated = OrderValidation.List(
            Kind, input.Status, input.ReceiptStatus, input.SupplierId, input.WarehouseId, input.Search, input.Limit, input.Offset);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var orders = db.PurchaseOrders.AsNoTracking();
        if (query.Status is { } status)
            orders = orders.Where(o => o.Status == status);
        if (query.PartnerId is { } supplierId)
            orders = orders.Where(o => o.PartnerId == supplierId);
        if (query.WarehouseId is { } warehouseId)
            orders = orders.Where(o => o.WarehouseId == warehouseId);
        // R29, decided in the database from the received quantity each line carries. A draft has nothing
        // received and no base quantity, so it is "none" and never "full".
        orders = query.Fulfilment switch
        {
            FulfilmentStatus.None => orders.Where(o =>
                !db.PurchaseOrderLines.Any(l => l.OrderId == o.Id && l.FulfilledBaseQuantity > 0)),
            FulfilmentStatus.Full => orders.Where(o =>
                o.Status != OrderStatus.Draft
                && !db.PurchaseOrderLines.Any(l => l.OrderId == o.Id && l.FulfilledBaseQuantity != l.BaseQuantity)),
            FulfilmentStatus.Partial => orders.Where(o =>
                db.PurchaseOrderLines.Any(l => l.OrderId == o.Id && l.FulfilledBaseQuantity > 0)
                && db.PurchaseOrderLines.Any(l => l.OrderId == o.Id && l.FulfilledBaseQuantity != l.BaseQuantity)),
            _ => orders,
        };
        if (query.Search is { } search)
        {
            // Number or reference (R36); a draft has no number.
            var pattern = LikePattern.Contains(search);
            orders = orders.Where(o =>
                (o.Number != null && EF.Functions.Like(DbText.Lower(o.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter)) ||
                (o.Reference != null && EF.Functions.Like(DbText.Lower(o.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)));
        }

        var total = await orders.CountAsync(cancellationToken);
        // Newest first (R36). The lines come along: count, amounts and progress of an item are theirs.
        var page = await orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Include(o => o.Lines)
            .ToListAsync(cancellationToken);
        var related = await RelatedAsync(page, cancellationToken);

        var items = page.Select(o => new PurchaseOrderSummaryDto(
            o.Id, o.Status.ToName(), o.Number, o.OrderDate, o.DueDate,
            related.Partners[o.PartnerId], related.Warehouses[o.WarehouseId], o.Reference, o.Note,
            o.FulfilmentStatus.ToName(), o.Lines.Count, TotalAmount(o),
            o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy, o.ConfirmedAt, o.ConfirmedBy, o.ClosedAt, o.ClosedBy)).ToList();
        return new PagedResult<PurchaseOrderSummaryDto>(items, total, query.Limit, query.Offset);
    }

    public async Task<Result<PurchaseOrderDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? NotFound() : await ToDtoAsync(order, cancellationToken);
    }

    /// <summary>Case-insensitive (R36). Anything that is not the number of a confirmed order is not found.</summary>
    public async Task<Result<PurchaseOrderDto>> GetByNumberAsync(string? number, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(number) || number.Length > PurchaseOrder.NumberMaxLength)
            return NotFound();
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Number != null && DbText.Lower(o.Number) == DbText.Lower(number), cancellationToken);
        return order is null ? NotFound() : await ToDtoAsync(order, cancellationToken);
    }

    public async Task<Result<PurchaseOrderDto>> CreateAsync(CreatePurchaseOrderInput input, CancellationToken cancellationToken = default)
    {
        var validated = OrderValidation.Values(
            Kind, input.OrderDate, input.ExpectedDate, input.SupplierId, input.WarehouseId, input.Reference, input.Note, input.Lines);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        try
        {
            return await db.SerializedPerTenantAsync<Result<PurchaseOrderDto>>(async ct =>
            {
                var lines = await CheckReferencesAsync(values, stored: null, ct);
                if (!lines.IsSuccess)
                    return lines.Error;

                // R11: a draft. It has no number, orders nothing and cannot be received against.
                var order = PurchaseOrder.Create(
                    values.OrderDate, values.DueDate, values.PartnerId, values.WarehouseId, values.Reference, values.Note, lines.Value,
                    clock.UtcNow, ActorKeyId());
                db.PurchaseOrders.Add(order);
                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(order, ct);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            // A master was deleted between the check and the write: the foreign key is the authority.
            return (await CheckReferencesAsync(values, stored: null, cancellationToken)).Error
                ?? throw new InvalidOperationException("A reference was rejected by the database but is present.", ex);
        }
    }

    public async Task<Result<PurchaseOrderDto>> ReplaceAsync(Guid id, ReplacePurchaseOrderInput input, CancellationToken cancellationToken = default)
    {
        var validated = OrderValidation.Values(
            Kind, input.OrderDate, input.ExpectedDate, input.SupplierId, input.WarehouseId, input.Reference, input.Note, input.Lines,
            input.Has(nameof(input.ExpectedDate)), input.Has(nameof(input.Reference)), input.Has(nameof(input.Note)));
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        try
        {
            return await db.SerializedPerTenantAsync<Result<PurchaseOrderDto>>(async ct =>
            {
                // R10: exists -> is a draft -> references.
                var order = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
                if (order is null)
                    return NotFound();
                if (!order.IsDraft)
                    return NotADraft(order, "replaced");
                var lines = await CheckReferencesAsync(values, order, ct);
                if (!lines.IsSuccess)
                    return lines.Error;

                var removed = order.Replace(
                    values.OrderDate, values.DueDate, values.PartnerId, values.WarehouseId, values.Reference, values.Note, lines.Value,
                    clock.UtcNow, ActorKeyId());
                db.PurchaseOrderLines.RemoveRange(removed);
                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(order, ct);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            return (await CheckReferencesAsync(values, stored: null, cancellationToken)).Error
                ?? throw new InvalidOperationException("A reference was rejected by the database but is present.", ex);
        }
    }

    public Task<Result<PurchaseOrderDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<PurchaseOrderDeleted>>(async ct =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (!order.IsDraft)
                return NotADraft(order, "deleted");

            // The lines go with their draft, deleted here and not by a cascade.
            db.PurchaseOrderLines.RemoveRange(order.Lines);
            db.PurchaseOrders.Remove(order);
            await db.SaveChangesAsync(ct);
            return new PurchaseOrderDeleted();
        }, cancellationToken);

    /// <summary>
    /// Confirms a draft (R12, R13): number, attribution and the factor and base quantity of every line, in one
    /// transaction - or nothing at all. Order of checks: exists -> is a draft -> supplier, warehouse and every
    /// article active (header keys together and alone, then the lines) -> the supplier still has the role ->
    /// every line converts with the factors as they are now. Only then is the counter advanced, so a refused
    /// confirmation consumes no number.
    /// </summary>
    public Task<Result<PurchaseOrderDto>> ConfirmAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<PurchaseOrderDto>>(async ct =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (!order.IsDraft)
                return NotADraft(order, "confirmed again");

            var supplier = await db.Partners.AsNoTracking()
                .Where(p => p.Id == order.PartnerId)
                .Select(p => new { p.IsActive, p.IsSupplier })
                .SingleAsync(ct);
            var warehouseIsActive = await db.Warehouses.AsNoTracking()
                .Where(w => w.Id == order.WarehouseId)
                .Select(w => w.IsActive)
                .SingleAsync(ct);
            var inactiveHeader = new List<(string, Guid)>();
            if (!supplier.IsActive)
                inactiveHeader.Add((Kind.PartnerField, order.PartnerId));
            if (!warehouseIsActive)
                inactiveHeader.Add((WarehouseField, order.WarehouseId));

            var lines = order.Lines.Select(l => new StockLineEntry(l.ArticleId, l.UnitId, l.Quantity)).ToList();
            // The factors are read here, under the tenant's lock: a conversion cannot be set or deleted between
            // this read and the save.
            var facts = await StockReads.LineFactsAsync(db, lines.Select(l => l.ArticleId), [], ct);
            if (StockLineChecks.ActiveMasters(inactiveHeader, lines, facts.Articles,
                    "The supplier, the warehouse or an article of the order is inactive; reactivate it or change the draft, then confirm again. Nothing was confirmed.") is { } inactive)
                return inactive;
            if (!supplier.IsSupplier)
                return RoleMissing(order.PartnerId);

            // A draft follows the current factor (R11), so a line that converted when it was saved may not convert any more.
            var converted = StockLineChecks.Convert(lines, facts, StockDocumentType.Receipt);
            if (!converted.IsSuccess)
                return converted.Error;

            var number = await StockReads.NextNumberAsync(db, TenantId(), Kind.Series, ct);
            order.Confirm(number, converted.Value.Select(c => c.Factor).ToList(), clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);

    /// <summary>R15: <c>confirmed -> closed</c>, whatever was received. Draft receipts against the order stay, and cannot be posted until a reopen.</summary>
    public Task<Result<PurchaseOrderDto>> CloseAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<PurchaseOrderDto>>(async ct =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (order.Status != OrderStatus.Confirmed)
                return AppError.InvalidState(order.IsDraft
                    ? "The purchase order is a draft: only a confirmed order can be closed. Delete the draft if it is not needed."
                    : "The purchase order is already closed.");
            order.Close(clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);

    /// <summary>R16: <c>closed -> confirmed</c>; number, lines and received quantities are untouched.</summary>
    public Task<Result<PurchaseOrderDto>> ReopenAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<PurchaseOrderDto>>(async ct =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (order.Status != OrderStatus.Closed)
                return AppError.InvalidState(order.IsDraft
                    ? "The purchase order is a draft: only a closed order can be reopened. Confirm the draft to order its goods."
                    : "The purchase order is confirmed, not closed: it is already open.");
            order.Reopen();
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);

    /// <summary>
    /// R10: the supplier first - exists, is active or already the order's supplier, has the role - then the
    /// warehouse, then the lines by the kinds of a stock document line (spec 007, R16). Each stage answers
    /// alone. A reference the stored draft already has in that place may stay although inactive; the role is
    /// asked for on every save. On success the lines to store, each with its unit.
    /// </summary>
    private async Task<Result<IReadOnlyList<OrderLineEntry>>> CheckReferencesAsync(OrderValues values, PurchaseOrder? stored, CancellationToken cancellationToken)
    {
        var supplier = await db.Partners.AsNoTracking()
            .Where(p => p.Id == values.PartnerId)
            .Select(p => new { p.IsActive, p.IsSupplier })
            .SingleOrDefaultAsync(cancellationToken);
        if (supplier is null)
            return AppError.ReferenceNotFound(Kind.PartnerField, values.PartnerId);
        if (!supplier.IsActive && stored?.PartnerId != values.PartnerId)
            return AppError.ReferenceInactive(Kind.PartnerField, values.PartnerId);
        if (!supplier.IsSupplier)
            return RoleMissing(values.PartnerId);

        var warehouseError = await ReferenceCheck.ValidateAsync(
            db.Warehouses.Where(w => w.Id == values.WarehouseId).Select(w => w.IsActive),
            WarehouseField, values.WarehouseId, alreadyAssigned: stored?.WarehouseId == values.WarehouseId, cancellationToken);
        if (warehouseError is not null)
            return warehouseError;

        var articlesOnOrder = stored?.Lines.Select(l => l.ArticleId).ToHashSet() ?? [];
        var unitsOnOrder = stored?.Lines.Select(l => l.UnitId).ToHashSet() ?? [];
        var facts = await StockReads.LineFactsAsync(
            db, values.Lines.Select(l => l.ArticleId), values.Lines.Where(l => l.UnitId is not null).Select(l => l.UnitId!.Value), cancellationToken);
        // The lines of an order are judged like those of a receipt: a quantity greater than zero that converts.
        var requests = values.Lines.Select(l => new StockLineRequest(l.ArticleId, l.Quantity, l.UnitId)).ToList();
        var lines = StockLineChecks.References(requests, facts, articlesOnOrder, unitsOnOrder, StockDocumentType.Receipt);
        if (!lines.IsSuccess)
            return lines.Error;
        return lines.Value.Select((l, i) => new OrderLineEntry(l.ArticleId, l.UnitId, l.Quantity, values.Lines[i].UnitPrice)).ToList();
    }

    /// <summary>What orders show of other records: the current code and name of their suppliers and warehouses (R36).</summary>
    private sealed record Related(IReadOnlyDictionary<Guid, ReferenceSummary> Partners, IReadOnlyDictionary<Guid, ReferenceSummary> Warehouses);

    private async Task<Related> RelatedAsync(IReadOnlyCollection<PurchaseOrder> orders, CancellationToken cancellationToken)
    {
        var partnerIds = orders.Select(o => o.PartnerId).Distinct().ToList();
        var partners = await db.Partners.AsNoTracking()
            .Where(p => partnerIds.Contains(p.Id))
            .Select(p => new ReferenceSummary(p.Id, p.Code, p.Name))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var warehouseIds = orders.Select(o => o.WarehouseId).Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => warehouseIds.Contains(w.Id))
            .Select(w => new ReferenceSummary(w.Id, w.Code, w.Name))
            .ToDictionaryAsync(w => w.Id, cancellationToken);
        return new Related(partners, warehouses);
    }

    /// <summary>
    /// The representation with the masters' current codes and names. A confirmed line shows the factor and
    /// base quantity it was confirmed with; a draft line is converted here, with the article's factor as it is
    /// now, and nothing is written (R11).
    /// </summary>
    private async Task<PurchaseOrderDto> ToDtoAsync(PurchaseOrder order, CancellationToken cancellationToken)
    {
        var related = await RelatedAsync([order], cancellationToken);
        var articleIds = order.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking()
            .Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Code, a.Name, a.BaseUnitId })
            .ToDictionaryAsync(a => a.Id, cancellationToken);
        var unitIds = order.Lines.Select(l => l.UnitId).Concat(articles.Values.Select(a => a.BaseUnitId)).Distinct().ToList();
        var units = await db.UnitsOfMeasure.AsNoTracking()
            .Where(u => unitIds.Contains(u.Id))
            .Select(u => new ReferenceSummary(u.Id, u.Code, u.Name))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var currentFactors = order.IsDraft
            ? await db.ArticleUnits.AsNoTracking()
                .Where(c => articleIds.Contains(c.ArticleId))
                .Select(c => new { c.ArticleId, c.UnitId, c.Factor })
                .ToDictionaryAsync(c => (c.ArticleId, c.UnitId), c => c.Factor, cancellationToken)
            : [];

        var lines = order.Lines.Select(l =>
        {
            var a = articles[l.ArticleId];
            decimal factor, baseQuantity;
            if (l.Factor is { } confirmedFactor && l.BaseQuantity is { } confirmedBaseQuantity)
                (factor, baseQuantity) = (confirmedFactor, confirmedBaseQuantity);
            else
            {
                // A draft line is always in a unit of its article: a conversion a draft uses cannot be deleted (R38).
                factor = l.UnitId == a.BaseUnitId ? UnitConversion.BaseUnitFactor : currentFactors[(l.ArticleId, l.UnitId)];
                baseQuantity = UnitConversion.ToBase(l.Quantity, factor);
            }
            return new PurchaseOrderLineDto(
                l.LineNo, new ReferenceSummary(a.Id, a.Code, a.Name), units[l.UnitId], QuantityRules.Normalize(l.Quantity),
                QuantityRules.Normalize(l.UnitPrice), QuantityRules.Normalize(l.LineAmount),
                QuantityRules.Normalize(factor), units[a.BaseUnitId], QuantityRules.Normalize(baseQuantity),
                QuantityRules.Normalize(l.FulfilledBaseQuantity),
                QuantityRules.Normalize(OrderProgress.Outstanding(order.Status, l.BaseQuantity, l.FulfilledBaseQuantity)));
        }).ToList();
        return new PurchaseOrderDto(
            order.Id, order.Status.ToName(), order.Number, order.OrderDate, order.DueDate,
            related.Partners[order.PartnerId], related.Warehouses[order.WarehouseId], order.Reference, order.Note,
            order.FulfilmentStatus.ToName(), lines, TotalAmount(order),
            order.CreatedAt, order.UpdatedAt, order.CreatedBy, order.UpdatedBy,
            order.ConfirmedAt, order.ConfirmedBy, order.ClosedAt, order.ClosedBy);
    }

    private static decimal TotalAmount(PurchaseOrder order) =>
        QuantityRules.Normalize(OrderAmounts.Total(order.Lines.Select(l => l.LineAmount)));

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A purchase order can only be written by a tenant API key.");

    private Guid TenantId() =>
        context.TenantId ?? throw new InvalidOperationException("A purchase order can only be confirmed for a tenant.");

    /// <summary>By <c>id</c> or by <c>number</c>, exactly one - for clients without a URL path (spec 009, section 5).</summary>
    public Task<Result<PurchaseOrderDto>> FindAsync(OrderAddressInput address, CancellationToken cancellationToken = default)
    {
        if (address.Id is null == address.Number is null)
        {
            const string message = "Give exactly one of id and number.";
            return Task.FromResult<Result<PurchaseOrderDto>>(
                AppError.Validation(new Dictionary<string, string[]> { ["id"] = [message], ["number"] = [message] }));
        }
        if (address.Number is not null)
            return GetByNumberAsync(address.Number, cancellationToken);
        return Guid.TryParse(address.Id, out var id) ? GetAsync(id, cancellationToken) : Task.FromResult<Result<PurchaseOrderDto>>(NotFound());
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PurchaseOrderDto>> ReplaceAsync(string? id, ReplacePurchaseOrderInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PurchaseOrderDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PurchaseOrderDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PurchaseOrderDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PurchaseOrderDto>> ConfirmAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PurchaseOrderDto>>(error)
            : ConfirmAsync(parsed, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PurchaseOrderDto>> CloseAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PurchaseOrderDto>>(error)
            : CloseAsync(parsed, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PurchaseOrderDto>> ReopenAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PurchaseOrderDto>>(error)
            : ReopenAsync(parsed, cancellationToken);

    private const string NotFoundDetail = "Purchase order not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    /// <summary>R2: the partner exists and may be used, but the tenant does not buy from it.</summary>
    private static AppError RoleMissing(Guid partnerId) =>
        new(ErrorCodes.PartnerRoleMissing,
            $"The partner named by {Kind.PartnerField} is not a supplier: a {Kind.Name} needs a partner with {Kind.PartnerRole} = true. "
            + $"Choose a supplier (partner_list with {Kind.PartnerRole} = true), or give this partner the role with partner_update.",
            new Dictionary<string, string[]> { [Kind.PartnerField] = [$"The partner with id '{partnerId}' does not have {Kind.PartnerRole}."] });

    /// <summary>R14: a confirmed order is immutable, closed or not.</summary>
    private static AppError NotADraft(PurchaseOrder order, string attempted) =>
        AppError.InvalidState(
            $"The purchase order is {order.Status.ToName()} and cannot be {attempted}; confirmation is permanent. "
            + "To change what was ordered, close this order and create a new one.");
}
