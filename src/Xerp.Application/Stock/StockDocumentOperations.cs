using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Application.Stock;

/// <summary>
/// The stock document operations of spec 005, section 4.1, spec 006 (transfers, reversal) and spec 007 (lines
/// in a unit of the article). One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// Every write of a stock document runs inside <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>: one
/// transaction that holds the tenant's lock. That is what makes posting atomic and safe under concurrency
/// (R12, R16, R17): the stock a posting checks cannot change before it writes, the counter cannot be advanced
/// by anyone else, and a draft cannot be replaced or deleted while it is being posted. A reversal is a posting
/// too and takes the same lock (spec 006, R19). Each operation reads everything it decides on after it got
/// the lock and saves once, so it changes everything or nothing; one lock per tenant also means that two
/// postings can never wait for each other (spec 006, E5).
/// </para>
/// <para>
/// A receipt may be linked to a purchase order (spec 009). The order is read inside the same lock, which the
/// order's own writes take too: its status and its outstanding quantities cannot change between the check and
/// the save, so a line is never received above what was ordered (R26, R30).
/// </para>
/// </summary>
public sealed class StockDocumentOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    private const string WarehouseField = "warehouseId";
    private const string ToWarehouseField = StockDocumentValidation.ToWarehouseField;
    private const string PurchaseOrderField = OrderLinkChecks.PurchaseOrderField;

    public async Task<Result<PagedResult<StockDocumentSummaryDto>>> ListAsync(ListStockDocumentsInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var documents = db.StockDocuments.AsNoTracking();
        if (query.Type is { } type)
            documents = documents.Where(d => d.Type == type);
        if (query.Status is { } status)
            documents = documents.Where(d => d.Status == status);
        if (query.WarehouseId is { } warehouseId)
            // Spec 006, 4.1: the source or the destination.
            documents = documents.Where(d => d.WarehouseId == warehouseId || d.ToWarehouseId == warehouseId);
        if (query.PurchaseOrderId is { } purchaseOrderId)
            documents = documents.Where(d => d.PurchaseOrderId == purchaseOrderId);
        if (query.Search is { } search)
        {
            // Number or reference (R22); a draft has no number.
            var pattern = LikePattern.Contains(search);
            documents = documents.Where(d =>
                (d.Number != null && EF.Functions.Like(DbText.Lower(d.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter)) ||
                (d.Reference != null && EF.Functions.Like(DbText.Lower(d.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)));
        }

        var total = await documents.CountAsync(cancellationToken);
        // Newest first (R22).
        var page = await documents
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        var ids = page.Select(d => d.Id).ToList();
        var lineCounts = await db.StockDocumentLines
            .Where(l => ids.Contains(l.DocumentId))
            .GroupBy(l => l.DocumentId)
            .Select(g => new { DocumentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DocumentId, x => x.Count, cancellationToken);
        var related = await RelatedAsync(page, cancellationToken);

        var items = page.Select(d => new StockDocumentSummaryDto(
            d.Id, d.Type.ToName(), d.Status.ToName(), d.Number, d.DocumentDate,
            related.Warehouse(d.WarehouseId), related.Warehouse(d.ToWarehouseId), related.Link(d.ReversalOfId), related.Link(d.ReversedById),
            related.Order(d.PurchaseOrderId), d.Reference, d.Note, lineCounts.GetValueOrDefault(d.Id),
            d.CreatedAt, d.UpdatedAt, d.CreatedBy, d.UpdatedBy, d.PostedAt, d.PostedBy)).ToList();
        return new PagedResult<StockDocumentSummaryDto>(items, total, query.Limit, query.Offset);
    }

    public async Task<Result<StockDocumentDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return document is null ? NotFound() : await ToDtoAsync(document, cancellationToken);
    }

    /// <summary>Case-insensitive (R22). Anything that is not the number of a posted document is not found (E12).</summary>
    public async Task<Result<StockDocumentDto>> GetByNumberAsync(string? number, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(number) || number.Length > StockDocument.NumberMaxLength)
            return NotFound();
        var document = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.Number != null && DbText.Lower(d.Number) == DbText.Lower(number), cancellationToken);
        return document is null ? NotFound() : await ToDtoAsync(document, cancellationToken);
    }

    public async Task<Result<StockDocumentDto>> CreateAsync(CreateStockDocumentInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.Create(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var (type, values, purchaseOrderId) = (validated.Value.Type, validated.Value.Values, validated.Value.PurchaseOrderId);

        try
        {
            return await db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
            {
                var lines = await CheckReferencesAsync(type, values, stored: null, purchaseOrderId, ct);
                if (!lines.IsSuccess)
                    return lines.Error;

                // R9: a draft. It has no number and no effect on stock, and it reserves nothing - of stock or
                // of the order it is linked to (spec 009, R23).
                var document = StockDocument.Create(
                    type, values.DocumentDate, values.WarehouseId, values.ToWarehouseId, values.Reference, values.Note, lines.Value,
                    clock.UtcNow, ActorKeyId(), await BookQuantitiesAsync(type, values.WarehouseId, lines.Value, ct), purchaseOrderId);
                db.StockDocuments.Add(document);
                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(document, ct);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            // A master was deleted between the check and the write: the foreign key is the authority.
            return (await CheckReferencesAsync(type, values, stored: null, purchaseOrderId, cancellationToken)).Error ?? throw new InvalidOperationException("A reference was rejected by the database but is present.", ex);
        }
    }

    public async Task<Result<StockDocumentDto>> ReplaceAsync(Guid id, ReplaceStockDocumentInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.Replace(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;
        // What the foreign key handler below checks the references as; set once the document is read.
        var storedType = StockDocumentType.Receipt;
        Guid? storedOrderId = null;

        try
        {
            return await db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
            {
                // R8: exists -> is a draft -> references. What the type decides - the destination, a quantity
                // of zero, a repeated article - is a rule of form, but one only the stored type can apply, so
                // it comes as early as it can (spec 006, R4; spec 008, E10).
                var document = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
                if (document is null)
                    return StockDocumentValidation.WithoutDocument(values) ?? NotFound();
                (storedType, storedOrderId) = (document.Type, document.PurchaseOrderId);
                if (StockDocumentValidation.OfType(document.Type, values, document.IsLinked) is { } typeError)
                    return typeError;
                if (!document.IsDraft)
                    return NotADraft(document, "replaced");
                // Spec 009, R19: the link is the stored one; the order must still be open (R31).
                var lines = await CheckReferencesAsync(document.Type, values, document, document.PurchaseOrderId, ct);
                if (!lines.IsSuccess)
                    return lines.Error;

                // Spec 008, R6: every save of a count records the book quantity of all its lines anew.
                var removed = document.Replace(
                    values.DocumentDate, values.WarehouseId, values.ToWarehouseId, values.Reference, values.Note, lines.Value,
                    clock.UtcNow, ActorKeyId(), await BookQuantitiesAsync(document.Type, values.WarehouseId, lines.Value, ct));
                db.StockDocumentLines.RemoveRange(removed);
                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(document, ct);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            return (await CheckReferencesAsync(storedType, values, stored: null, storedOrderId, cancellationToken)).Error ?? throw new InvalidOperationException("A reference was rejected by the database but is present.", ex);
        }
    }

    public Task<Result<StockDocumentDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<StockDocumentDeleted>>(async ct =>
        {
            var document = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
            if (document is null)
                return NotFound();
            if (!document.IsDraft)
                return NotADraft(document, "deleted");

            // R10: the lines go with their draft, deleted here and not by a cascade.
            db.StockDocumentLines.RemoveRange(document.Lines);
            db.StockDocuments.Remove(document);
            await db.SaveChangesAsync(ct);
            return new StockDocumentDeleted();
        }, cancellationToken);

    /// <summary>
    /// Posts a draft (R12-R18; spec 006, R5-R9; spec 007, R17-R19): number, posting attribution, the factor
    /// and base quantity of every line and the ledger entries, in one transaction - or nothing at all. Order
    /// of checks (R13; spec 007, R18): exists -> is a draft -> warehouse(s) and articles active -> every line
    /// converts with the factors as they are now -> stock sufficient, counted in base quantities. Only then
    /// is the counter advanced, so a refused posting consumes no number (R17). A transfer writes both entries
    /// of every line in the same save: stock cannot leave the source without arriving at the destination
    /// (spec 006, R8). A count (spec 008, R9-R12) has no sufficiency check; instead the stock of every counted
    /// article must still equal the book quantity of its line, read here under the tenant's lock, and then the
    /// differences are written - so afterwards stock equals what was counted. A receipt linked to a purchase
    /// order (spec 009, R24-R26) is checked last against that order: it must be confirmed, and no order line
    /// may be taken above what is outstanding; then the received quantities of the order lines rise in the
    /// same save.
    /// </summary>
    public Task<Result<StockDocumentDto>> PostAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
        {
            var document = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
            if (document is null)
                return NotFound();
            if (!document.IsDraft)
                return NotADraft(document, "posted again");

            var lines = document.Lines.Select(l => l.Entry).ToList();
            // The factors are read here, inside the posting transaction and under the tenant's lock: a
            // conversion cannot be set or deleted between this read and the save (spec 007, section 11).
            var facts = await StockReads.LineFactsAsync(db, lines.Select(l => l.ArticleId), [], ct);
            if (StockLineChecks.ActiveForPosting(await InactiveWarehousesAsync(document, ct), lines, facts.Articles) is { } inactive)
                return inactive;

            // Spec 007, R17, R18: a draft follows the current factor, so a line that converted when it was
            // saved may not convert any more.
            var converted = StockLineChecks.Convert(lines, facts, document.Type);
            if (!converted.IsSuccess)
                return converted.Error;

            // Spec 008, R10: stock cannot change between this read and the save - the tenant's lock is held -
            // so the differences written below are exactly the differences to the stock of this moment.
            if (document.Type == StockDocumentType.Count)
            {
                var books = document.Lines.Select(l => new StockLineValues(l.ArticleId, l.BookQuantity!.Value)).ToList();
                if (StockLineChecks.Current(books, await OnHandInAsync(document.WarehouseId, lines.Select(l => l.ArticleId), ct)) is { } outdated)
                    return outdated;
            }

            // R15, R18; spec 006, R7; spec 007, R19: only what leaves the (source) warehouse is checked, in
            // base quantities; stock in the destination and the document date play no part.
            if (document.Type is StockDocumentType.Issue or StockDocumentType.Transfer)
            {
                var inSource = await OnHandInAsync(document.WarehouseId, lines.Select(l => l.ArticleId), ct);
                if (StockLineChecks.Sufficiency(converted.Value.Select(c => c.BaseValues).ToList(), inSource) is { } insufficient)
                    return insufficient;
            }

            // Spec 009, R24, R26: the order is read here, under the tenant's lock that confirm, close, reopen
            // and every other posting take too, so what is outstanding now is what this posting is held to.
            PurchaseOrder? order = null;
            if (document.PurchaseOrderId is { } orderId)
            {
                order = await db.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId, ct);
                if (OrderLinkChecks.Open(PurchaseOrderField, order.Status) is { } notOpen)
                    return notOpen;
                var fulfilment = converted.Value.Select((c, i) => new LineFulfilment(lines[i].OrderLineNo!.Value, c.BaseQuantity)).ToList();
                if (OrderLinkChecks.WithinOrder(fulfilment, order.Outstanding) is { } exceeds)
                    return exceeds;
            }

            var number = await StockReads.NextNumberAsync(db, TenantId(), DocumentSeries.Of(document.Type), ct);
            db.StockLedgerEntries.AddRange(
                document.Post(number, converted.Value.Select(c => c.Factor).ToList(), clock.UtcNow, ActorKeyId()));
            // R25: from the base quantities the lines were posted with.
            order?.Fulfil(document.Fulfilment);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(document, ct);
        }, cancellationToken);

    /// <summary>
    /// Reverses a posted document (ADR-0013; spec 006, R11-R19): in one transaction a reversing document is
    /// created already posted, with the original's ledger entries in the opposite sign, and the original
    /// becomes <c>reversed</c> - or nothing at all. Order of checks (R12): form -> exists -> can be reversed
    /// -> date not earlier than the original's -> stock. Only then is the counter advanced, so a refused
    /// reversal consumes no number. The masters need not be active (R17). Reversing a receipt linked to a
    /// purchase order gives the quantity back to the order's lines in the same save, whatever the order's
    /// status, and does not change that status (spec 009, R32-R34).
    /// </summary>
    public async Task<Result<StockDocumentDto>> ReverseAsync(Guid id, ReverseStockDocumentInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.Reverse(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
        {
            var original = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
            if (original is null)
                return NotFound();
            if (!original.CanBeReversed)
                return NotReversible(original);
            if (StockDocumentValidation.ReversalDate(values.DocumentDate, original.DocumentDate) is { } dateError)
                return dateError;

            // R14, R15: the reversing entries are the original's own entries, negated.
            var entries = await db.StockLedgerEntries.Where(e => e.DocumentId == original.Id).ToListAsync(ct);
            var movements = entries
                .Select(e => StockMovements.Opposite(new StockMovement(e.ArticleId, e.WarehouseId, e.Quantity)))
                .ToList();
            // R16: no pair may go below zero, whatever stock other warehouses hold.
            var onHand = await OnHandAsync(movements.Select(m => m.ArticleId), movements.Select(m => m.WarehouseId), ct);
            var lines = original.Lines.Select(l => l.BaseValues).ToList();
            if (StockLineChecks.ReversalSufficiency(lines, movements, onHand) is { } insufficient)
                return insufficient;

            if (original.PurchaseOrderId is { } orderId)
            {
                var order = await db.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId, ct);
                order.TakeBack(original.Fulfilment);
            }

            var number = await StockReads.NextNumberAsync(db, TenantId(), DocumentSeries.Of(original.Type), ct);
            var (reversal, reversing) = original.Reverse(entries, values.DocumentDate, values.Note, number, clock.UtcNow, ActorKeyId());
            db.StockDocuments.Add(reversal);
            db.StockLedgerEntries.AddRange(reversing);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(reversal, ct);
        }, cancellationToken);
    }

    /// <summary>The inactive warehouses of a document with their error keys: source, then destination.</summary>
    private async Task<List<(string Field, Guid Id)>> InactiveWarehousesAsync(StockDocument document, CancellationToken cancellationToken)
    {
        var ids = new[] { document.WarehouseId, document.ToWarehouseId ?? document.WarehouseId };
        var inactive = await db.Warehouses.AsNoTracking()
            .Where(w => ids.Contains(w.Id) && !w.IsActive)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        var result = new List<(string, Guid)>();
        if (inactive.Contains(document.WarehouseId))
            result.Add((WarehouseField, document.WarehouseId));
        if (document.ToWarehouseId is { } destination && inactive.Contains(destination))
            result.Add((ToWarehouseField, destination));
        return result;
    }

    /// <summary>Stock on hand of the given articles in the given warehouses: the sum of the ledger (R19). A pair without entries is absent.</summary>
    private async Task<Dictionary<(Guid ArticleId, Guid WarehouseId), decimal>> OnHandAsync(
        IEnumerable<Guid> articles, IEnumerable<Guid> warehouses, CancellationToken cancellationToken)
    {
        var articleIds = articles.Distinct().ToList();
        var warehouseIds = warehouses.Distinct().ToList();
        var sums = await db.StockLedgerEntries
            .Where(e => warehouseIds.Contains(e.WarehouseId) && articleIds.Contains(e.ArticleId))
            .GroupBy(e => new { e.ArticleId, e.WarehouseId })
            .Select(g => new { g.Key.ArticleId, g.Key.WarehouseId, Quantity = g.Sum(e => e.Quantity) })
            .ToListAsync(cancellationToken);
        return sums.ToDictionary(x => (x.ArticleId, x.WarehouseId), x => x.Quantity);
    }

    /// <summary>Stock on hand of the given articles in one warehouse, per article. An article without entries is absent.</summary>
    private async Task<Dictionary<Guid, decimal>> OnHandInAsync(Guid warehouseId, IEnumerable<Guid> articles, CancellationToken cancellationToken) =>
        (await OnHandAsync(articles, [warehouseId], cancellationToken)).ToDictionary(p => p.Key.ArticleId, p => p.Value);

    /// <summary>
    /// Spec 008, R6: for a count, the stock on hand of every line's article in the document's warehouse at
    /// this moment, in line order - 0 where there is none; null for every other type. Called by create and
    /// replace only, under the tenant's lock, so it is the stock the draft is saved against.
    /// </summary>
    private async Task<IReadOnlyList<decimal>?> BookQuantitiesAsync(
        StockDocumentType type, Guid warehouseId, IReadOnlyList<StockLineEntry> lines, CancellationToken cancellationToken)
    {
        if (type != StockDocumentType.Count)
            return null;
        var onHand = await OnHandInAsync(warehouseId, lines.Select(l => l.ArticleId), cancellationToken);
        return lines.Select(l => onHand.GetValueOrDefault(l.ArticleId)).ToList();
    }

    /// <summary>
    /// R8; spec 006, R4; spec 007, R16: the warehouse first, then the destination of a transfer, then the
    /// lines. A reference the stored draft already has in that place may stay although inactive. On success
    /// the lines to store, each with its unit.
    /// <para>
    /// A document linked to an order (spec 009, R21) is checked further, after its warehouse: the order exists
    /// and is confirmed; among the unknown references of the lines is an <c>orderLineNo</c> the order does not
    /// have; and last, once every line is acceptable by itself, the document agrees with the order.
    /// </para>
    /// </summary>
    private async Task<Result<IReadOnlyList<StockLineEntry>>> CheckReferencesAsync(
        StockDocumentType type, StockDocumentValues values, StockDocument? stored, Guid? purchaseOrderId, CancellationToken cancellationToken)
    {
        var warehouseError = await ReferenceCheck.ValidateAsync(
            db.Warehouses.Where(w => w.Id == values.WarehouseId).Select(w => w.IsActive),
            WarehouseField, values.WarehouseId, alreadyAssigned: stored?.WarehouseId == values.WarehouseId, cancellationToken);
        if (warehouseError is not null)
            return warehouseError;

        if (values.ToWarehouseId is { } destination)
        {
            var destinationError = await ReferenceCheck.ValidateAsync(
                db.Warehouses.Where(w => w.Id == destination).Select(w => w.IsActive),
                ToWarehouseField, destination, alreadyAssigned: stored?.ToWarehouseId == destination, cancellationToken);
            if (destinationError is not null)
                return destinationError;
        }

        LinkedOrderFacts? order = null;
        if (purchaseOrderId is { } orderId)
        {
            order = await OrderFactsAsync(orderId, cancellationToken);
            if (OrderLinkChecks.Open(PurchaseOrderField, orderId, order) is { } notOpen)
                return notOpen;
        }

        var articlesOnDocument = stored?.Lines.Select(l => l.ArticleId).ToHashSet() ?? [];
        var unitsOnDocument = stored?.Lines.Select(l => l.UnitId).ToHashSet() ?? [];
        var facts = await StockReads.LineFactsAsync(
            db, values.Lines.Select(l => l.ArticleId), values.Lines.Where(l => l.UnitId is not null).Select(l => l.UnitId!.Value), cancellationToken);
        var lines = StockLineChecks.References(values.Lines, facts, articlesOnDocument, unitsOnDocument, type, order?.LineArticles.Keys.ToList());
        if (lines.IsSuccess && order is not null && OrderLinkChecks.Agreement(values.WarehouseId, lines.Value, order) is { } mismatch)
            return mismatch;
        return lines;
    }

    /// <summary>The purchase order of the current tenant with this id as a linked document sees it; null when there is none.</summary>
    private async Task<LinkedOrderFacts?> OrderFactsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.Status, o.WarehouseId })
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null)
            return null;
        var lineArticles = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .ToDictionaryAsync(l => l.LineNo, l => l.ArticleId, cancellationToken);
        return new LinkedOrderFacts(orderId, order.Status, order.WarehouseId, lineArticles);
    }

    /// <summary>What documents show of other records: the current code and name of their warehouses, the number of the documents and orders they link to.</summary>
    private sealed record Related(
        IReadOnlyDictionary<Guid, ReferenceSummary> Warehouses, IReadOnlyDictionary<Guid, StockDocumentLinkDto> Documents,
        IReadOnlyDictionary<Guid, OrderLinkDto> Orders)
    {
        public OrderLinkDto? Order(Guid? id) => id is { } value ? Orders[value] : null;

        public ReferenceSummary Warehouse(Guid id) => Warehouses[id];

        public ReferenceSummary? Warehouse(Guid? id) => id is { } value ? Warehouses[value] : null;

        public StockDocumentLinkDto? Link(Guid? id) => id is { } value ? Documents[value] : null;
    }

    private async Task<Related> RelatedAsync(IReadOnlyCollection<StockDocument> documents, CancellationToken cancellationToken)
    {
        var warehouseIds = documents.Select(d => d.WarehouseId)
            .Concat(documents.Where(d => d.ToWarehouseId is not null).Select(d => d.ToWarehouseId!.Value))
            .Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => warehouseIds.Contains(w.Id))
            .Select(w => new ReferenceSummary(w.Id, w.Code, w.Name))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        var linkedIds = documents.SelectMany(d => new[] { d.ReversalOfId, d.ReversedById })
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var linked = new Dictionary<Guid, StockDocumentLinkDto>();
        if (linkedIds.Count > 0)
        {
            // Both ends of a reversal are posted, so both have a number.
            linked = await db.StockDocuments.AsNoTracking()
                .Where(d => linkedIds.Contains(d.Id))
                .Select(d => new StockDocumentLinkDto(d.Id, d.Number!))
                .ToDictionaryAsync(d => d.Id, cancellationToken);
        }

        var orderIds = documents.Where(d => d.PurchaseOrderId is not null).Select(d => d.PurchaseOrderId!.Value).Distinct().ToList();
        var orders = new Dictionary<Guid, OrderLinkDto>();
        if (orderIds.Count > 0)
        {
            // A document is linked to a confirmed order only, and an order never loses its number.
            orders = await db.PurchaseOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id))
                .Select(o => new OrderLinkDto(o.Id, o.Number!))
                .ToDictionaryAsync(o => o.Id, cancellationToken);
        }
        return new Related(warehouses, linked, orders);
    }

    /// <summary>
    /// The representation with the masters' current codes and names (R23). A posted line shows the factor and
    /// base quantity it was posted with; a draft line is converted here, with the article's factor as it is
    /// now, and nothing is written (spec 007, R17).
    /// </summary>
    private async Task<StockDocumentDto> ToDtoAsync(StockDocument document, CancellationToken cancellationToken)
    {
        var related = await RelatedAsync([document], cancellationToken);
        var articleIds = document.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking()
            .Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Code, a.Name, a.BaseUnitId })
            .ToDictionaryAsync(a => a.Id, cancellationToken);
        var unitIds = document.Lines.Select(l => l.UnitId).Concat(articles.Values.Select(a => a.BaseUnitId)).Distinct().ToList();
        var units = await db.UnitsOfMeasure.AsNoTracking()
            .Where(u => unitIds.Contains(u.Id))
            .Select(u => new ReferenceSummary(u.Id, u.Code, u.Name))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var currentFactors = document.IsDraft
            ? await db.ArticleUnits.AsNoTracking()
                .Where(c => articleIds.Contains(c.ArticleId))
                .Select(c => new { c.ArticleId, c.UnitId, c.Factor })
                .ToDictionaryAsync(c => (c.ArticleId, c.UnitId), c => c.Factor, cancellationToken)
            : [];

        var lines = document.Lines.Select(l =>
        {
            var a = articles[l.ArticleId];
            decimal factor, baseQuantity;
            if (l.Factor is { } postedFactor && l.BaseQuantity is { } postedBaseQuantity)
                (factor, baseQuantity) = (postedFactor, postedBaseQuantity);
            else
            {
                // A draft line is always in a unit of its article: a conversion a draft uses cannot be deleted (R8).
                factor = l.UnitId == a.BaseUnitId ? UnitConversion.BaseUnitFactor : currentFactors[(l.ArticleId, l.UnitId)];
                // Shown as it converts now, also when that is no longer acceptable for posting (R15, E8).
                baseQuantity = UnitConversion.ToBase(l.Quantity, factor);
            }
            // Spec 008, R6, R7: the book quantity is the stored one, never the stock of now; the difference of
            // a draft follows the current factor like its base quantity does (E9).
            decimal? book = l.BookQuantity is { } stored ? QuantityRules.Normalize(stored) : null;
            decimal? difference = l.BookQuantity is { } b ? QuantityRules.Normalize(CountRules.Difference(baseQuantity, b)) : null;
            return new StockDocumentLineDto(
                l.LineNo, new ReferenceSummary(a.Id, a.Code, a.Name), units[l.UnitId], QuantityRules.Normalize(l.Quantity),
                QuantityRules.Normalize(factor), units[a.BaseUnitId], QuantityRules.Normalize(baseQuantity), book, difference, l.OrderLineNo);
        }).ToList();
        return new StockDocumentDto(
            document.Id, document.Type.ToName(), document.Status.ToName(), document.Number, document.DocumentDate,
            related.Warehouse(document.WarehouseId), related.Warehouse(document.ToWarehouseId),
            related.Link(document.ReversalOfId), related.Link(document.ReversedById), related.Order(document.PurchaseOrderId),
            document.Reference, document.Note, lines,
            document.CreatedAt, document.UpdatedAt, document.CreatedBy, document.UpdatedBy, document.PostedAt, document.PostedBy);
    }

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A stock document can only be written by a tenant API key.");

    private Guid TenantId() =>
        context.TenantId ?? throw new InvalidOperationException("A stock document can only be posted for a tenant.");

    /// <summary>By <c>id</c> or by <c>number</c>, exactly one - for clients without a URL path (spec 005, section 5).</summary>
    public Task<Result<StockDocumentDto>> FindAsync(StockDocumentAddressInput address, CancellationToken cancellationToken = default)
    {
        if (address.Id is null == address.Number is null)
        {
            const string message = "Give exactly one of id and number.";
            return Task.FromResult<Result<StockDocumentDto>>(
                AppError.Validation(new Dictionary<string, string[]> { ["id"] = [message], ["number"] = [message] }));
        }
        if (address.Number is not null)
            return GetByNumberAsync(address.Number, cancellationToken);
        return Guid.TryParse(address.Id, out var id) ? GetAsync(id, cancellationToken) : Task.FromResult<Result<StockDocumentDto>>(NotFound());
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<StockDocumentDto>> ReplaceAsync(string? id, ReplaceStockDocumentInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<StockDocumentDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<StockDocumentDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<StockDocumentDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<StockDocumentDto>> PostAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<StockDocumentDto>>(error)
            : PostAsync(parsed, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<StockDocumentDto>> ReverseAsync(string? id, ReverseStockDocumentInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<StockDocumentDto>>(error)
            : ReverseAsync(parsed, input, cancellationToken);

    private const string NotFoundDetail = "Stock document not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    /// <summary>R11; spec 006, R18: a posted document is immutable, reversed or not.</summary>
    private static AppError NotADraft(StockDocument document, string attempted) =>
        AppError.InvalidState(
            $"The stock document is {document.Status.ToName()} and cannot be {attempted}; a posted document is permanent. "
            + (document.CanBeReversed
                ? "To correct it, reverse it and create a new document."
                : "To redo the movement, create a new document."));

    /// <summary>Spec 006, R11: what cannot be reversed, and why.</summary>
    private static AppError NotReversible(StockDocument document) =>
        AppError.InvalidState(document switch
        {
            { IsDraft: true } => "The stock document is a draft: it has not moved any stock, so there is nothing to reverse. Change or delete the draft instead.",
            { IsReversal: true } => "The stock document is itself a reversal, and a reversal is final. To redo the movement, create a new document.",
            _ => "The stock document is already reversed; a document can be reversed once. To redo the movement, create a new document.",
        });
}
