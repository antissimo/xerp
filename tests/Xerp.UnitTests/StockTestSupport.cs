using Xerp.Application.Common;
using Xerp.Application.Stock;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

internal static class StockTestSupport
{
    /// <summary>The warehouse the issues of <see cref="IssueSufficiency"/> and <see cref="ShortIssueLines"/> leave.</summary>
    public static readonly Guid Source = Guid.CreateVersion7();

    /// <summary>
    /// Posts a document whose lines are all in the base unit of their article: factor 1 on every line
    /// (spec 007, R14) - what posting was before lines could name a unit.
    /// </summary>
    public static IReadOnlyList<StockLedgerEntry> PostInBaseUnits(this StockDocument document, string number, DateTime now, Guid actorKeyId) =>
        document.Post(number, Enumerable.Repeat(UnitConversion.BaseUnitFactor, document.Lines.Count).ToList(), now, actorKeyId);

    /// <summary>What an issue of the lines moves out of <see cref="Source"/>.</summary>
    public static List<StockMovement> IssueMovements(IReadOnlyList<StockLineValues> lines) =>
        lines.SelectMany(l => StockMovements.OfLine(StockDocumentType.Issue, Source, null, l)).ToList();

    /// <summary>Stock per article in <see cref="Source"/>, as the checks take it: per (article, warehouse) pair.</summary>
    public static Dictionary<(Guid ArticleId, Guid WarehouseId), decimal> InSource(IReadOnlyDictionary<Guid, decimal> onHand) =>
        onHand.ToDictionary(p => (p.Key, Source), p => p.Value);

    /// <summary>
    /// The stock check of posting an issue of the lines from one warehouse with the given stock per article -
    /// in a tenant that does not allow negative stock (the default of spec 012), unless said otherwise.
    /// </summary>
    public static AppError? IssueSufficiency(
        IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, decimal> onHand, bool negativeStockAllowed = false) =>
        StockLineChecks.Sufficiency(lines, IssueMovements(lines), InSource(onHand), negativeStockAllowed);

    /// <summary>The zero-based positions of the lines of every article such an issue is short of, at the default of the rule.</summary>
    public static IReadOnlyList<int> ShortIssueLines(IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, decimal> onHand)
    {
        var shortArticles = StockMovements.ShortPairs(IssueMovements(lines), InSource(onHand), negativeStockAllowed: false)
            .Select(p => p.ArticleId).ToHashSet();
        return Enumerable.Range(0, lines.Count).Where(i => shortArticles.Contains(lines[i].ArticleId)).ToList();
    }
}
