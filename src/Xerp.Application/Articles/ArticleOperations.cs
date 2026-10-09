using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Domain.Common;

namespace Xerp.Application.Articles;

/// <summary>
/// The article operations of spec 002, section 4. One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant, for articles and for the units they
/// reference alike. Writes check in the order of ADR-0008, decision 7:
/// validation -> addressed article -> base unit -> code uniqueness.
/// </summary>
public sealed class ArticleOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    private const string BaseUnitField = "baseUnitId";

    /// <summary>An article with the current code and name of its base unit, read in one query.</summary>
    private sealed class Row
    {
        public required Article Article { get; init; }
        public required string BaseUnitCode { get; init; }
        public required string BaseUnitName { get; init; }
    }

    public async Task<Result<PagedResult<ArticleDto>>> ListAsync(ListArticlesInput input, CancellationToken cancellationToken = default)
    {
        var validated = ArticleValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var articles = db.Articles.AsNoTracking();
        if (query.IsActive is { } isActive)
            articles = articles.Where(a => a.IsActive == isActive);
        if (query.Type is { } type)
            articles = articles.Where(a => a.Type == type);
        if (query.BaseUnitId is { } baseUnitId)
            articles = articles.Where(a => a.BaseUnitId == baseUnitId);
        if (query.AlternativeUnitId is { } alternativeUnitId)
            // Spec 007, 4.2: articles that have a conversion for the unit; an article's base unit is not one.
            articles = articles.Where(a => db.ArticleUnits.Any(c => c.ArticleId == a.Id && c.UnitId == alternativeUnitId));
        if (query.Search is { } search)
        {
            var pattern = LikePattern.Contains(search);
            articles = articles.Where(a =>
                EF.Functions.Like(EF.Property<string>(a, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(a.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }

        var total = await articles.CountAsync(cancellationToken);
        var page = articles
            .OrderBy(a => EF.Property<string>(a, DbNames.CodeLower))
            .ThenBy(a => a.Id)
            .Skip(query.Offset)
            .Take(query.Limit);
        var rows = await WithBaseUnit(page)
            .OrderBy(r => EF.Property<string>(r.Article, DbNames.CodeLower))
            .ThenBy(r => r.Article.Id)
            .ToListAsync(cancellationToken);
        return new PagedResult<ArticleDto>(rows.Select(ToDto).ToList(), total, query.Limit, query.Offset);
    }

    public async Task<Result<ArticleDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await WithBaseUnit(db.Articles.AsNoTracking().Where(a => a.Id == id)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? NotFound() : ToDto(row);
    }

    public async Task<Result<ArticleDto>> GetByCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        // A string that cannot be a code cannot name an article (E11).
        if (!CodeRules.TryNormalize(code, out var normalized))
            return NotFound();
        var row = await WithBaseUnit(db.Articles.AsNoTracking()
                .Where(a => EF.Property<string>(a, DbNames.CodeLower) == DbText.Lower(normalized)))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? NotFound() : ToDto(row);
    }

    public async Task<Result<ArticleDto>> CreateAsync(CreateArticleInput input, CancellationToken cancellationToken = default)
    {
        var validated = ArticleValidation.Create(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        if (await CheckBaseUnitAsync(values.BaseUnitId, alreadyAssigned: false, cancellationToken) is { } referenceError)
            return referenceError;
        if (await CodeIsUsedAsync(values.Code, exceptId: null, cancellationToken))
            return CodeTaken(values.Code);

        var article = Article.Create(
            values.Code, values.Name, values.Description, values.Type, values.BaseUnitId, values.IsActive, clock.UtcNow, ActorKeyId());
        db.Articles.Add(article);
        return await SaveAndReadAsync(article, values, cancellationToken);
    }

    public async Task<Result<ArticleDto>> ReplaceAsync(Guid id, ReplaceArticleInput input, CancellationToken cancellationToken = default)
    {
        var validated = ArticleValidation.Replace(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        // Serialised with the writes of stock documents and of conversions: whether the article is used or has
        // conversions cannot change between the check below and the save, so a used article never changes its
        // type or base unit (spec 005, R26) and an article with conversions never its base unit (spec 007, R9).
        return await db.SerializedPerTenantAsync<Result<ArticleDto>>(async ct =>
        {
            var article = await db.Articles.SingleOrDefaultAsync(a => a.Id == id, ct);
            if (article is null)
                return NotFound();
            // Frozen fields are compared with the stored values before any reference is looked up (005-q, T-Q3).
            if (await CheckFrozenFieldsAsync(article, values, ct) is { } frozen)
                return frozen;
            // Keeping the current base unit is allowed even if that unit has been deactivated since (R9).
            var keepsBaseUnit = article.BaseUnitId == values.BaseUnitId;
            if (await CheckBaseUnitAsync(values.BaseUnitId, keepsBaseUnit, ct) is { } referenceError)
                return referenceError;
            if (await CodeIsUsedAsync(values.Code, exceptId: id, ct))
                return CodeTaken(values.Code);

            article.Replace(
                values.Code, values.Name, values.Description, values.Type, values.BaseUnitId, values.IsActive, clock.UtcNow, ActorKeyId());
            return await SaveAndReadAsync(article, values, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// What is frozen, compared with the stored values before any reference is looked up:
    /// <list type="bullet">
    /// <item>Spec 005, R24-R26: while a stock document line names the article - on a draft or a posted
    /// document - its type and base unit, because the quantities on those lines are in that unit.</item>
    /// <item>Spec 007, R9: while the article has at least one conversion, its base unit - the factors mean
    /// nothing against another base unit. Its type is not frozen by conversions.</item>
    /// </list>
    /// </summary>
    private async Task<AppError?> CheckFrozenFieldsAsync(Article article, ArticleValues values, CancellationToken cancellationToken)
    {
        var typeChanges = article.Type != values.Type;
        var baseUnitChanges = article.BaseUnitId != values.BaseUnitId;
        if (!typeChanges && !baseUnitChanges)
            return null;

        var used = await IsUsedAsync(article.Id, cancellationToken);
        var hasConversions = baseUnitChanges && await db.ArticleUnits.AnyAsync(c => c.ArticleId == article.Id, cancellationToken);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (typeChanges && used)
            errors["type"] = ["The type cannot change while stock documents or orders use the article."];
        if (baseUnitChanges && used)
            errors[BaseUnitField] = ["The base unit cannot change while stock documents or orders use the article: their quantities are in it."];
        else if (hasConversions)
            errors[BaseUnitField] = ["The base unit cannot change while the article has unit conversions: their factors are to this base unit."];
        if (errors.Count == 0)
            return null;
        return AppError.InUse(
            used
                ? "The article is used by stock documents or orders, so its type and base unit are fixed. Keep both values; "
                  + "name, code, description and isActive can still change."
                : "The article has unit conversions, so its base unit is fixed. Keep the base unit, or delete the article's "
                  + "conversions first, change the base unit and set the conversions again with factors to the new base unit.",
            errors);
    }

    /// <summary>Spec 005, R24-R26; spec 009, R37, R38: a line of a stock document or of an order, whatever its status, names the article.</summary>
    private async Task<bool> IsUsedAsync(Guid articleId, CancellationToken cancellationToken) =>
        await db.StockDocumentLines.AnyAsync(l => l.ArticleId == articleId, cancellationToken)
        || await db.PurchaseOrderLines.AnyAsync(l => l.ArticleId == articleId, cancellationToken);

    /// <summary>
    /// Deletes an article no stock document uses, together with its conversions (spec 007, R11): they are the
    /// article's own and are deleted here, not by a cascade. Serialised with the writes of conversions, so a
    /// conversion set at the same moment is either deleted with the article or finds no article.
    /// </summary>
    public async Task<Result<ArticleDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.SerializedPerTenantAsync<Result<ArticleDeleted>>(async ct =>
            {
                var article = await db.Articles.SingleOrDefaultAsync(a => a.Id == id, ct);
                if (article is null)
                    return NotFound();
                // A draft counts like a posted document (spec 005, R24, R25).
                if (await IsUsedAsync(id, ct))
                    return InUse();
                db.ArticleUnits.RemoveRange(await db.ArticleUnits.Where(c => c.ArticleId == id).ToListAsync(ct));
                db.Articles.Remove(article);
                await db.SaveChangesAsync(ct);
                return new ArticleDeleted();
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (ex.BlockedDelete)
        {
            // Something started to use the article after the check above: the foreign key is the authority.
            return InUse();
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
    }

    private static AppError InUse() =>
        AppError.InUse("The article is used by stock documents or orders and cannot be deleted. Deactivate it instead (isActive = false).");

    /// <summary>Saves the tracked article and returns its representation, translating what only the database can decide.</summary>
    private async Task<Result<ArticleDto>> SaveAndReadAsync(Article article, ArticleValues values, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (ex.ConstraintName == DbNames.ArticleBaseUnitForeignKey && !ex.BlockedDelete)
        {
            // The unit was deleted between the check and the write: the foreign key is the authority (E9).
            return AppError.ReferenceNotFound(BaseUnitField, values.BaseUnitId);
        }
        catch (UniqueConstraintViolationException ex) when (ex.IsCodeOf<Article>())
        {
            // Lost a race with a concurrent write of the same code: the unique index is the authority (E8).
            return CodeTaken(values.Code);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by someone else between the read and the write.
            return NotFound();
        }

        // The base unit cannot disappear any more: the saved article holds it.
        var unit = await db.UnitsOfMeasure.AsNoTracking()
            .Where(u => u.Id == article.BaseUnitId)
            .Select(u => new { u.Code, u.Name })
            .SingleAsync(cancellationToken);
        return ToDto(new Row { Article = article, BaseUnitCode = unit.Code, BaseUnitName = unit.Name });
    }

    private Task<AppError?> CheckBaseUnitAsync(Guid baseUnitId, bool alreadyAssigned, CancellationToken cancellationToken) =>
        ReferenceCheck.ValidateAsync(
            db.UnitsOfMeasure.Where(u => u.Id == baseUnitId).Select(u => u.IsActive),
            BaseUnitField, baseUnitId, alreadyAssigned, cancellationToken);

    private Task<bool> CodeIsUsedAsync(string code, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Articles.AnyAsync(
            a => EF.Property<string>(a, DbNames.CodeLower) == DbText.Lower(code) && (exceptId == null || a.Id != exceptId),
            cancellationToken);

    /// <summary>Joins each article with its base unit; both sets are filtered to the current tenant.</summary>
    private IQueryable<Row> WithBaseUnit(IQueryable<Article> articles) =>
        articles.Join(db.UnitsOfMeasure.AsNoTracking(), a => a.BaseUnitId, u => u.Id, (a, u) => new Row { Article = a, BaseUnitCode = u.Code, BaseUnitName = u.Name });

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("An article can only be written by a tenant API key.");

    /// <summary>By <c>id</c> or by <c>code</c>, exactly one (spec 003, R13) - for clients without a URL path.</summary>
    public Task<Result<ArticleDto>> FindAsync(RecordAddressInput address, CancellationToken cancellationToken = default)
    {
        if (RecordAddress.IdOrCode(address, NotFoundDetail, out var id, out var code) is { } error)
            return Task.FromResult<Result<ArticleDto>>(error);
        return id is { } byId ? GetAsync(byId, cancellationToken) : GetByCodeAsync(code, cancellationToken);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<ArticleDto>> ReplaceAsync(string? id, ReplaceArticleInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<ArticleDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<ArticleDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<ArticleDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    private const string NotFoundDetail = "Article not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private static AppError CodeTaken(string code) => AppError.CodeTaken($"An article with code '{code}' already exists.");

    private static ArticleDto ToDto(Row row)
    {
        var a = row.Article;
        return new ArticleDto(
            a.Id, a.Code, a.Name, a.Description, a.Type.ToName(),
            new ReferenceSummary(a.BaseUnitId, row.BaseUnitCode, row.BaseUnitName),
            a.IsActive, a.CreatedAt, a.UpdatedAt, a.CreatedBy, a.UpdatedBy);
    }
}
