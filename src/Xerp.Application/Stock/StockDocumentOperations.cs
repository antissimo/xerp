using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>
/// The stock document operations of spec 005, section 4.1. One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// Every write of a stock document runs inside <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>: one
/// transaction that holds the tenant's lock. That is what makes posting atomic and safe under concurrency
/// (R12, R16, R17): the stock a posting checks cannot change before it writes, the counter cannot be advanced
/// by anyone else, and a draft cannot be replaced or deleted while it is being posted. Each operation reads
/// everything it decides on after it got the lock and saves once, so it changes everything or nothing.
/// </para>
/// </summary>
public sealed class StockDocumentOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    private const string WarehouseField = "warehouseId";

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
            documents = documents.Where(d => d.WarehouseId == warehouseId);
        if (query.Search is { } search)
        {
            // Number or reference (R22); a draft has no number.
            var pattern = LikePattern.Contains(search);
            documents = documents.Where(d =>
                (d.Number != null && EF.Functions.Like(DbText.Lower(d.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter)) ||
                (d.Reference != null && EF.Functions.Like(DbText.Lower(d.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)));
        }

        var total = await documents.CountAsync(cancellationToken);
        // Newest first (R22). The page is cut before the join; the join keeps no order, so it is ordered again.
        var rows = await documents
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Join(db.Warehouses.AsNoTracking(), d => d.WarehouseId, w => w.Id, (d, w) => new
            {
                Document = d,
                WarehouseCode = w.Code,
                WarehouseName = w.Name,
                LineCount = db.StockDocumentLines.Count(l => l.DocumentId == d.Id),
            })
            .OrderByDescending(r => r.Document.CreatedAt)
            .ThenByDescending(r => r.Document.Id)
            .ToListAsync(cancellationToken);

        var items = rows.Select(r =>
        {
            var d = r.Document;
            return new StockDocumentSummaryDto(
                d.Id, d.Type.ToName(), d.Status.ToName(), d.Number, d.DocumentDate,
                new ReferenceSummary(d.WarehouseId, r.WarehouseCode, r.WarehouseName), d.Reference, d.Note, r.LineCount,
                d.CreatedAt, d.UpdatedAt, d.CreatedBy, d.UpdatedBy, d.PostedAt, d.PostedBy);
        }).ToList();
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
        var (type, values) = (validated.Value.Type, validated.Value.Values);

        try
        {
            return await db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
            {
                if (await CheckReferencesAsync(values, stored: null, ct) is { } referenceError)
                    return referenceError;

                // R9: a draft. It has no number and no effect on stock, and it reserves nothing.
                var document = StockDocument.Create(
                    type, values.DocumentDate, values.WarehouseId, values.Reference, values.Note, values.Lines,
                    clock.UtcNow, ActorKeyId());
                db.StockDocuments.Add(document);
                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(document, ct);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            // A master was deleted between the check and the write: the foreign key is the authority.
            return await CheckReferencesAsync(values, stored: null, cancellationToken) ?? throw new InvalidOperationException("A reference was rejected by the database but is present.", ex);
        }
    }

    public async Task<Result<StockDocumentDto>> ReplaceAsync(Guid id, ReplaceStockDocumentInput input, CancellationToken cancellationToken = default)
    {
        var validated = StockDocumentValidation.Replace(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        try
        {
            return await db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
            {
                // R8: exists -> is a draft -> references.
                var document = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
                if (document is null)
                    return NotFound();
                if (!document.IsDraft)
                    return Posted("replaced");
                if (await CheckReferencesAsync(values, document, ct) is { } referenceError)
                    return referenceError;

                var removed = document.Replace(
                    values.DocumentDate, values.WarehouseId, values.Reference, values.Note, values.Lines,
                    clock.UtcNow, ActorKeyId());
                db.StockDocumentLines.RemoveRange(removed);
                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(document, ct);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            return await CheckReferencesAsync(values, stored: null, cancellationToken) ?? throw new InvalidOperationException("A reference was rejected by the database but is present.", ex);
        }
    }

    public Task<Result<StockDocumentDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<StockDocumentDeleted>>(async ct =>
        {
            var document = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
            if (document is null)
                return NotFound();
            if (!document.IsDraft)
                return Posted("deleted");

            // R10: the lines go with their draft, deleted here and not by a cascade.
            db.StockDocumentLines.RemoveRange(document.Lines);
            db.StockDocuments.Remove(document);
            await db.SaveChangesAsync(ct);
            return new StockDocumentDeleted();
        }, cancellationToken);

    /// <summary>
    /// Posts a draft (R12-R18): number, posting attribution and one ledger entry per line, in one transaction -
    /// or nothing at all. Order of checks (R13): exists -> is a draft -> warehouse and articles active ->
    /// stock sufficient. Only then is the counter advanced, so a refused posting consumes no number (R17).
    /// </summary>
    public Task<Result<StockDocumentDto>> PostAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<StockDocumentDto>>(async ct =>
        {
            var document = await db.StockDocuments.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct);
            if (document is null)
                return NotFound();
            if (!document.IsDraft)
                return Posted("posted again");

            var warehouseIsActive = await db.Warehouses.Where(w => w.Id == document.WarehouseId).Select(w => w.IsActive).SingleAsync(ct);
            if (!warehouseIsActive)
                return AppError.ReferenceInactive(WarehouseField, document.WarehouseId);
            var lines = document.Lines.Select(l => l.Values).ToList();
            if (StockLineChecks.ActiveForPosting(lines, await ArticleFactsAsync(lines, ct)) is { } inactive)
                return inactive;

            // R15, R18: only what leaves the warehouse is checked; the document date plays no part.
            if (document.Type == StockDocumentType.Issue
                && StockLineChecks.Sufficiency(lines, await OnHandAsync(document.WarehouseId, lines, ct)) is { } insufficient)
                return insufficient;

            var typeName = document.Type.ToName();
            var counter = await db.DocumentCounters.SingleOrDefaultAsync(c => c.DocumentType == typeName, ct);
            if (counter is null)
            {
                counter = DocumentCounter.Start(TenantId(), document.Type);
                db.DocumentCounters.Add(counter);
            }

            var entries = document.Post(DocumentNumber.Format(document.Type, counter.Next()), clock.UtcNow, ActorKeyId());
            db.StockLedgerEntries.AddRange(entries);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(document, ct);
        }, cancellationToken);

    /// <summary>Stock on hand of the lines' articles in one warehouse: the sum of the ledger (R19).</summary>
    private async Task<Dictionary<Guid, decimal>> OnHandAsync(Guid warehouseId, List<StockLineValues> lines, CancellationToken cancellationToken)
    {
        var articleIds = lines.Select(l => l.ArticleId).Distinct().ToList();
        return await db.StockLedgerEntries
            .Where(e => e.WarehouseId == warehouseId && articleIds.Contains(e.ArticleId))
            .GroupBy(e => e.ArticleId)
            .Select(g => new { ArticleId = g.Key, Quantity = g.Sum(e => e.Quantity) })
            .ToDictionaryAsync(x => x.ArticleId, x => x.Quantity, cancellationToken);
    }

    private async Task<Dictionary<Guid, ArticleFacts>> ArticleFactsAsync(IReadOnlyList<StockLineValues> lines, CancellationToken cancellationToken)
    {
        var articleIds = lines.Select(l => l.ArticleId).Distinct().ToList();
        return await db.Articles.AsNoTracking()
            .Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.IsActive, a.Type })
            .ToDictionaryAsync(a => a.Id, a => new ArticleFacts(a.IsActive, a.Type), cancellationToken);
    }

    /// <summary>R8: the warehouse first, then the lines. A reference the stored draft already has may stay although inactive.</summary>
    private async Task<AppError?> CheckReferencesAsync(StockDocumentValues values, StockDocument? stored, CancellationToken cancellationToken)
    {
        var warehouseError = await ReferenceCheck.ValidateAsync(
            db.Warehouses.Where(w => w.Id == values.WarehouseId).Select(w => w.IsActive),
            WarehouseField, values.WarehouseId, alreadyAssigned: stored?.WarehouseId == values.WarehouseId, cancellationToken);
        if (warehouseError is not null)
            return warehouseError;

        var alreadyOnDocument = stored?.Lines.Select(l => l.ArticleId).ToHashSet() ?? [];
        return StockLineChecks.References(values.Lines, await ArticleFactsAsync(values.Lines, cancellationToken), alreadyOnDocument);
    }

    /// <summary>The representation with the masters' current codes and names (R23).</summary>
    private async Task<StockDocumentDto> ToDtoAsync(StockDocument document, CancellationToken cancellationToken)
    {
        var warehouse = await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == document.WarehouseId)
            .Select(w => new { w.Code, w.Name })
            .SingleAsync(cancellationToken);
        var articleIds = document.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking()
            .Where(a => articleIds.Contains(a.Id))
            .Join(db.UnitsOfMeasure.AsNoTracking(), a => a.BaseUnitId, u => u.Id,
                (a, u) => new { a.Id, a.Code, a.Name, UnitId = u.Id, UnitCode = u.Code, UnitName = u.Name })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var lines = document.Lines.Select(l =>
        {
            var a = articles[l.ArticleId];
            return new StockDocumentLineDto(
                l.LineNo, new ReferenceSummary(a.Id, a.Code, a.Name), new ReferenceSummary(a.UnitId, a.UnitCode, a.UnitName),
                QuantityRules.Normalize(l.Quantity));
        }).ToList();
        return new StockDocumentDto(
            document.Id, document.Type.ToName(), document.Status.ToName(), document.Number, document.DocumentDate,
            new ReferenceSummary(document.WarehouseId, warehouse.Code, warehouse.Name), document.Reference, document.Note, lines,
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

    private const string NotFoundDetail = "Stock document not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    /// <summary>R11: a posted document is immutable.</summary>
    private static AppError Posted(string attempted) =>
        AppError.InvalidState($"The stock document is posted and cannot be {attempted}; a posted document is permanent.");
}
