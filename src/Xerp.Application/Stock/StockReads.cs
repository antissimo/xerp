using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>
/// Reads that stock documents and orders share. <see cref="IXerpDb"/> is already filtered to the current tenant.
/// </summary>
internal static class StockReads
{
    /// <summary>
    /// The masters the lines of a document or an order name, as they are now: the articles, the given units
    /// and the conversions of those articles. Inside a write this runs under the tenant's lock, so the
    /// conversions it returns are the ones the write is saved against.
    /// </summary>
    public static async Task<StockLineFacts> LineFactsAsync(IXerpDb db, IEnumerable<Guid> articles, IEnumerable<Guid> units, CancellationToken cancellationToken)
    {
        var articleIds = articles.Distinct().ToList();
        var unitIds = units.Distinct().ToList();
        var articleFacts = await db.Articles.AsNoTracking()
            .Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.IsActive, a.Type, a.BaseUnitId })
            .ToDictionaryAsync(a => a.Id, a => new ArticleFacts(a.IsActive, a.Type, a.BaseUnitId), cancellationToken);
        var unitFacts = unitIds.Count == 0
            ? []
            : await db.UnitsOfMeasure.AsNoTracking()
                .Where(u => unitIds.Contains(u.Id))
                .Select(u => new { u.Id, u.IsActive })
                .ToDictionaryAsync(u => u.Id, u => u.IsActive, cancellationToken);
        var factors = await db.ArticleUnits.AsNoTracking()
            .Where(c => articleIds.Contains(c.ArticleId))
            .Select(c => new { c.ArticleId, c.UnitId, c.Factor })
            .ToDictionaryAsync(c => (c.ArticleId, c.UnitId), c => c.Factor, cancellationToken);
        return new StockLineFacts(articleFacts, unitFacts, factors);
    }

    /// <summary>
    /// The next number of a series (spec 005, R17; spec 009, R13). Called last, under the tenant's lock, when
    /// nothing can refuse the posting or the confirmation any more - so numbers are gapless.
    /// </summary>
    public static async Task<string> NextNumberAsync(IXerpDb db, Guid tenantId, DocumentSeries series, CancellationToken cancellationToken)
    {
        var counter = await db.DocumentCounters.SingleOrDefaultAsync(c => c.DocumentType == series.Key, cancellationToken);
        if (counter is null)
        {
            counter = DocumentCounter.Start(tenantId, series);
            db.DocumentCounters.Add(counter);
        }
        return DocumentNumber.Format(series, counter.Next());
    }
}
