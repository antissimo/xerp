using System.Globalization;
using Xerp.Application.Common;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>What a stock document needs to know about an article it names.</summary>
public readonly record struct ArticleFacts(bool IsActive, ArticleType Type);

/// <summary>
/// The rules that decide, from facts already read, whether the lines of a stock document are acceptable
/// (spec 005, R5, R8, R13, R15). No I/O: the operations read the facts and ask here.
/// </summary>
public static class StockLineChecks
{
    /// <summary>
    /// The line references of a draft being saved (R5): the first failing kind is reported for all lines that
    /// have it - unknown article, then inactive and newly assigned, then not a stock article (R8).
    /// </summary>
    /// <param name="articles">The articles of the current tenant among those the lines name; a missing id does not exist.</param>
    /// <param name="alreadyOnDocument">The articles the stored draft already has a line for: these may stay although inactive.</param>
    public static AppError? References(
        IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, ArticleFacts> articles, IReadOnlySet<Guid> alreadyOnDocument)
    {
        var unknown = Where(lines, l => !articles.ContainsKey(l.ArticleId));
        if (unknown.Count > 0)
            return Error(ErrorCodes.ReferenceNotFound, "A line names an article that does not exist.",
                unknown, lines, id => $"No record with id '{id}' exists.");

        var inactive = Where(lines, l => !articles[l.ArticleId].IsActive && !alreadyOnDocument.Contains(l.ArticleId));
        if (inactive.Count > 0)
            return Error(ErrorCodes.ReferenceInactive, "A line names an inactive article, which cannot be newly assigned.",
                inactive, lines, id => $"The record with id '{id}' is inactive.");

        var notStocked = Where(lines, l => articles[l.ArticleId].Type != ArticleType.Stock);
        if (notStocked.Count > 0)
            return Error(ErrorCodes.ArticleNotStocked,
                "Only articles of type \"stock\" can be on a stock document; a service has no stock.",
                notStocked, lines, id => $"The article with id '{id}' is a service.");
        return null;
    }

    /// <summary>Posting needs every article active, also those assigned while they still were (R13).</summary>
    public static AppError? ActiveForPosting(IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, ArticleFacts> articles)
    {
        var inactive = Where(lines, l => !articles.TryGetValue(l.ArticleId, out var facts) || !facts.IsActive);
        return inactive.Count == 0
            ? null
            : Error(ErrorCodes.ReferenceInactive,
                "A line names an article that is inactive; reactivate it or remove the line, then post again. Nothing was posted.",
                inactive, lines, id => $"The record with id '{id}' is inactive.");
    }

    /// <summary>
    /// No negative stock (R15): null when stock covers the outgoing lines, otherwise INSUFFICIENT_STOCK with the
    /// quantity key of every line of every short article.
    /// </summary>
    public static AppError? Sufficiency(IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, decimal> onHand)
    {
        var shortLines = StockSufficiency.ShortLines(lines, onHand);
        if (shortLines.Count == 0)
            return null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var index in shortLines)
        {
            var article = lines[index].ArticleId;
            var requested = lines.Where(l => l.ArticleId == article).Sum(l => l.Quantity);
            errors[StockDocumentValidation.LineKey(index, "quantity")] =
                [$"The document takes {Text(requested)} of this article in total; {Text(onHand.GetValueOrDefault(article))} is on hand in the warehouse."];
        }
        return new AppError(ErrorCodes.InsufficientStock,
            "Stock on hand does not cover the document; nothing was posted and the draft is unchanged. "
            + "Check stock on hand, then lower the quantities or receive stock first, and post again.",
            errors);
    }

    private static string Text(decimal quantity) => QuantityRules.Normalize(quantity).ToString(CultureInfo.InvariantCulture);

    private static List<int> Where(IReadOnlyList<StockLineValues> lines, Func<StockLineValues, bool> predicate) =>
        Enumerable.Range(0, lines.Count).Where(i => predicate(lines[i])).ToList();

    private static AppError Error(
        string code, string detail, List<int> indexes, IReadOnlyList<StockLineValues> lines, Func<Guid, string> message) =>
        new(code, detail, indexes.ToDictionary(
            i => StockDocumentValidation.LineKey(i, "articleId"), i => new[] { message(lines[i].ArticleId) }, StringComparer.Ordinal));
}
