using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.Application.ArticleUnits;

/// <summary>
/// The article unit operations of spec 007, section 4.1 (ADR-0014). One method = one HTTP endpoint = one MCP
/// tool. <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// Set and Delete run inside <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>, the lock every write of a
/// stock document and of an article holds (ADR-0012, amendment of 2026-10-09). That settles the races by
/// construction: two Sets of one new conversion cannot both create it (E3); a conversion cannot be deleted
/// while a draft that uses it is being saved, nor a draft saved in a unit whose conversion is being deleted
/// (R8); a factor cannot change between the moment a posting reads it and the moment it writes (R17); and an
/// article's base unit cannot change while a conversion for it is being created (R9).
/// </para>
/// </summary>
public sealed class ArticleUnitOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    private const string UnitField = "unitId";
    private const string ArticleArgument = "articleId";

    /// <summary>A conversion with the current codes and names of its article and both units, read in one query.</summary>
    private sealed class Row
    {
        public required ArticleUnit Conversion { get; init; }
        public required ReferenceSummary Article { get; init; }
        public required ReferenceSummary Unit { get; init; }
        public required ReferenceSummary BaseUnit { get; init; }
    }

    /// <summary>Ordered by unit code, case-insensitive. The base unit is not a conversion and is not listed (4.1).</summary>
    public async Task<Result<PagedResult<ArticleUnitDto>>> ListAsync(Guid articleId, ListArticleUnitsInput input, CancellationToken cancellationToken = default)
    {
        var validated = ArticleUnitValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;
        if (!await db.Articles.AnyAsync(a => a.Id == articleId, cancellationToken))
            return ArticleNotFound();

        var conversions = db.ArticleUnits.AsNoTracking().Where(c => c.ArticleId == articleId);
        var total = await conversions.CountAsync(cancellationToken);
        var rows = await Rows(conversions)
            .OrderBy(r => r.UnitCodeLower)
            .ThenBy(r => r.Row.Conversion.UnitId)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(r => r.Row)
            .ToListAsync(cancellationToken);
        return new PagedResult<ArticleUnitDto>(rows.Select(ToDto).ToList(), total, query.Limit, query.Offset);
    }

    public async Task<Result<ArticleUnitDto>> GetAsync(Guid articleId, Guid unitId, CancellationToken cancellationToken = default)
    {
        var row = await ReadAsync(articleId, unitId, cancellationToken);
        return row is null ? NotFound() : ToDto(row);
    }

    /// <summary>
    /// Creates the conversion or replaces its factor (R3). Order of checks (R6): validation -> article exists
    /// -> unit exists -> unit is not the article's base unit -> unit active, when the conversion is being
    /// created. A factor may be replaced at any time, used or not (R7): drafts follow it from now on, posted
    /// lines and the ledger keep what they were posted with (R17).
    /// </summary>
    public async Task<Result<ArticleUnitSet>> SetAsync(Guid articleId, Guid unitId, SetArticleUnitInput input, CancellationToken cancellationToken = default)
    {
        var validated = ArticleUnitValidation.Set(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var factor = validated.Value.Factor;

        try
        {
            return await db.SerializedPerTenantAsync<Result<ArticleUnitSet>>(async ct =>
            {
                var article = await db.Articles.AsNoTracking()
                    .Where(a => a.Id == articleId)
                    .Select(a => new { a.BaseUnitId })
                    .SingleOrDefaultAsync(ct);
                if (article is null)
                    return ArticleNotFound();
                var unitIsActive = await db.UnitsOfMeasure
                    .Where(u => u.Id == unitId)
                    .Select(u => (bool?)u.IsActive)
                    .SingleOrDefaultAsync(ct);
                if (unitIsActive is null)
                    return AppError.ReferenceNotFound(UnitField, unitId);
                if (unitId == article.BaseUnitId)
                    return UnitIsBaseUnit(unitId);

                var conversion = await db.ArticleUnits.SingleOrDefaultAsync(c => c.ArticleId == articleId && c.UnitId == unitId, ct);
                var created = conversion is null;
                if (conversion is null)
                {
                    // R4: an inactive unit cannot be newly assigned; an existing conversion keeps its unit.
                    if (unitIsActive == false)
                        return AppError.ReferenceInactive(UnitField, unitId);
                    conversion = ArticleUnit.Create(articleId, article.BaseUnitId, unitId, factor, clock.UtcNow, ActorKeyId());
                    db.ArticleUnits.Add(conversion);
                }
                else
                    conversion.SetFactor(factor, clock.UtcNow, ActorKeyId());

                await db.SaveChangesAsync(ct);
                var row = await ReadAsync(articleId, unitId, ct)
                    ?? throw new InvalidOperationException("The conversion that was just saved cannot be read.");
                return new ArticleUnitSet(ToDto(row), created);
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            // The article or the unit was deleted between the check and the write (their deletes of unused
            // records need no lock): the foreign key is the authority.
            if (!await db.Articles.AnyAsync(a => a.Id == articleId, cancellationToken))
                return ArticleNotFound();
            return AppError.ReferenceNotFound(UnitField, unitId);
        }
    }

    /// <summary>
    /// Removes the conversion (R8). A conversion that a line of a draft uses stays: the draft would be left
    /// with a line in a unit that is not a unit of its article. Lines of posted documents carry their own
    /// factor and do not block it.
    /// </summary>
    public Task<Result<ArticleUnitDeleted>> DeleteAsync(Guid articleId, Guid unitId, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<ArticleUnitDeleted>>(async ct =>
        {
            var conversion = await db.ArticleUnits.SingleOrDefaultAsync(c => c.ArticleId == articleId && c.UnitId == unitId, ct);
            if (conversion is null)
                return NotFound();
            var drafts = await db.StockDocumentLines
                .Where(l => l.ArticleId == articleId && l.UnitId == unitId)
                .Join(db.StockDocuments.Where(d => d.Status == StockDocumentStatus.Draft), l => l.DocumentId, d => d.Id, (l, d) => d.Id)
                .Distinct()
                .CountAsync(ct);
            if (drafts > 0)
                return AppError.InUse(
                    $"The conversion is used by lines of {drafts} draft stock document(s) and cannot be deleted. "
                    + "Post or delete those drafts, or change their lines to another unit of the article, then delete it again.");

            db.ArticleUnits.Remove(conversion);
            await db.SaveChangesAsync(ct);
            return new ArticleUnitDeleted();
        }, cancellationToken);

    private Task<Row?> ReadAsync(Guid articleId, Guid unitId, CancellationToken cancellationToken) =>
        Rows(db.ArticleUnits.AsNoTracking().Where(c => c.ArticleId == articleId && c.UnitId == unitId))
            .Select(r => r.Row)
            .SingleOrDefaultAsync(cancellationToken);

    private sealed class Sortable
    {
        public required Row Row { get; init; }
        public required string UnitCodeLower { get; init; }
    }

    /// <summary>Joins each conversion with its article and with its own and the article's base unit; all sets are filtered to the current tenant.</summary>
    private IQueryable<Sortable> Rows(IQueryable<ArticleUnit> conversions) =>
        from c in conversions
        join a in db.Articles.AsNoTracking() on c.ArticleId equals a.Id
        join u in db.UnitsOfMeasure.AsNoTracking() on c.UnitId equals u.Id
        join b in db.UnitsOfMeasure.AsNoTracking() on a.BaseUnitId equals b.Id
        select new Sortable
        {
            Row = new Row
            {
                Conversion = c,
                Article = new ReferenceSummary(a.Id, a.Code, a.Name),
                Unit = new ReferenceSummary(u.Id, u.Code, u.Name),
                BaseUnit = new ReferenceSummary(b.Id, b.Code, b.Name),
            },
            UnitCodeLower = EF.Property<string>(u, DbNames.CodeLower),
        };

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("An article unit can only be written by a tenant API key.");

    /// <summary>As the overload with a <see cref="Guid"/>; an <c>articleId</c> that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PagedResult<ArticleUnitDto>>> ListAsync(ListArticleUnitsArguments arguments, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(arguments.ArticleId, ArticleNotFoundDetail, out var articleId, ArticleArgument) is { } error
            ? Task.FromResult<Result<PagedResult<ArticleUnitDto>>>(error)
            : ListAsync(articleId, new ListArticleUnitsInput(arguments.Limit, arguments.Offset), cancellationToken);

    /// <summary>As the overload with two <see cref="Guid"/>s; each id is addressed under its own name (spec 007, section 5).</summary>
    public Task<Result<ArticleUnitDto>> GetAsync(ArticleUnitAddressInput address, CancellationToken cancellationToken = default) =>
        Address(address.ArticleId, address.UnitId, out var articleId, out var unitId) is { } error
            ? Task.FromResult<Result<ArticleUnitDto>>(error)
            : GetAsync(articleId, unitId, cancellationToken);

    /// <summary>As the overload with two <see cref="Guid"/>s; each id is addressed under its own name (spec 007, section 5).</summary>
    public Task<Result<ArticleUnitSet>> SetAsync(SetArticleUnitArguments arguments, CancellationToken cancellationToken = default) =>
        Address(arguments.ArticleId, arguments.UnitId, out var articleId, out var unitId) is { } error
            ? Task.FromResult<Result<ArticleUnitSet>>(error)
            : SetAsync(articleId, unitId, new SetArticleUnitInput(arguments.Factor), cancellationToken);

    /// <summary>As the overload with two <see cref="Guid"/>s; each id is addressed under its own name (spec 007, section 5).</summary>
    public Task<Result<ArticleUnitDeleted>> DeleteAsync(ArticleUnitAddressInput address, CancellationToken cancellationToken = default) =>
        Address(address.ArticleId, address.UnitId, out var articleId, out var unitId) is { } error
            ? Task.FromResult<Result<ArticleUnitDeleted>>(error)
            : DeleteAsync(articleId, unitId, cancellationToken);

    private static AppError? Address(string? article, string? unit, out Guid articleId, out Guid unitId) =>
        RecordAddress.Ids(article, ArticleArgument, out articleId, unit, UnitField, out unitId, NotFoundDetail);

    private const string ArticleNotFoundDetail = "Article not found.";
    private const string NotFoundDetail = "Article unit not found: no such article, or the article has no conversion for this unit.";

    private static AppError ArticleNotFound() => AppError.NotFound(ArticleNotFoundDetail);

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private static AppError UnitIsBaseUnit(Guid unitId) =>
        new(ErrorCodes.UnitIsBaseUnit,
            "This unit is the article's base unit: it is always a unit of the article, with factor 1, and cannot be given a conversion.",
            new Dictionary<string, string[]> { [UnitField] = [$"The unit with id '{unitId}' is the base unit of the article."] });

    private static ArticleUnitDto ToDto(Row row)
    {
        var c = row.Conversion;
        return new ArticleUnitDto(
            row.Article, row.Unit, QuantityRules.Normalize(c.Factor), row.BaseUnit, c.CreatedAt, c.UpdatedAt, c.CreatedBy, c.UpdatedBy);
    }
}
