using System.Globalization;
using Xerp.Application.Common;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>What a stock document needs to know about an article it names.</summary>
public readonly record struct ArticleFacts(bool IsActive, ArticleType Type, Guid BaseUnitId);

/// <summary>
/// What the lines of a stock document need to know about the masters they name, read in one place and at one
/// moment (spec 007, R17): the articles, the units of measure and the articles' conversions as they are now.
/// </summary>
/// <param name="Articles">The articles of the current tenant among those the lines name; a missing id does not exist.</param>
/// <param name="Units">Whether each unit the lines name is active; a missing id does not exist in the tenant.</param>
/// <param name="Factors">The conversions of those articles: (article, alternative unit) -> factor.</param>
public sealed record StockLineFacts(
    IReadOnlyDictionary<Guid, ArticleFacts> Articles,
    IReadOnlyDictionary<Guid, bool> Units,
    IReadOnlyDictionary<(Guid ArticleId, Guid UnitId), decimal> Factors)
{
    /// <summary>
    /// The factor of a unit on an article (spec 007, R5, R14): 1 for the article's base unit, the conversion's
    /// factor for an alternative unit, null when the unit is not a unit of the article.
    /// </summary>
    public decimal? Factor(Guid articleId, Guid unitId)
    {
        if (Articles.TryGetValue(articleId, out var article) && article.BaseUnitId == unitId)
            return UnitConversion.BaseUnitFactor;
        return Factors.TryGetValue((articleId, unitId), out var factor) ? factor : null;
    }
}

/// <summary>A line converted with the factor of one moment: what posting stores on the line and writes to the ledger.</summary>
public readonly record struct ConvertedLine(Guid ArticleId, decimal Factor, decimal BaseQuantity)
{
    /// <summary>What the line means for stock (R19).</summary>
    public StockLineValues BaseValues => new(ArticleId, BaseQuantity);
}

/// <summary>
/// The rules that decide, from facts already read, whether the lines of a stock document are acceptable
/// (spec 005, R5, R8, R13, R15; spec 007, R12-R18). No I/O: the operations read the facts and ask here.
/// </summary>
public static class StockLineChecks
{
    private const string ArticleField = "articleId";
    private const string UnitField = StockDocumentValidation.UnitField;
    private const string QuantityField = "quantity";

    /// <summary>
    /// The line references of a draft being saved (R5; spec 007, R12, R16): the first failing kind is reported
    /// for all lines that have it - unknown article or unit, then inactive and newly assigned, then not a stock
    /// article, then a unit that is not a unit of the line's article, then a quantity that does not convert.
    /// On success the lines as they are stored: every line with its unit, the base unit where none was given.
    /// <para>
    /// The article's base unit is never checked for being active, given or omitted: it is the article's own
    /// unit (R12). Any other inactive unit is refused unless the stored draft already has a line in it.
    /// </para>
    /// </summary>
    /// <param name="articlesOnDocument">The articles the stored draft already has a line for: these may stay although inactive.</param>
    /// <param name="unitsOnDocument">The units the stored draft already has a line in: these may stay although inactive.</param>
    public static Result<IReadOnlyList<StockLineEntry>> References(
        IReadOnlyList<StockLineRequest> lines, StockLineFacts facts, IReadOnlySet<Guid> articlesOnDocument, IReadOnlySet<Guid> unitsOnDocument)
    {
        var articles = facts.Articles;

        var unknown = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!articles.ContainsKey(lines[i].ArticleId))
                unknown.Add(i, ArticleField, $"No record with id '{lines[i].ArticleId}' exists.");
            if (lines[i].UnitId is { } unit && !facts.Units.ContainsKey(unit))
                unknown.Add(i, UnitField, $"No record with id '{unit}' exists.");
        }
        if (unknown.Any)
            return unknown.ToError(ErrorCodes.ReferenceNotFound, "A line names an article or a unit of measure that does not exist.");

        var inactive = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            var article = articles[lines[i].ArticleId];
            if (!article.IsActive && !articlesOnDocument.Contains(lines[i].ArticleId))
                inactive.Add(i, ArticleField, $"The record with id '{lines[i].ArticleId}' is inactive.");
            if (lines[i].UnitId is { } unit && unit != article.BaseUnitId && !facts.Units[unit] && !unitsOnDocument.Contains(unit))
                inactive.Add(i, UnitField, $"The record with id '{unit}' is inactive.");
        }
        if (inactive.Any)
            return inactive.ToError(ErrorCodes.ReferenceInactive, "A line names an inactive article or unit of measure, which cannot be newly assigned.");

        var notStocked = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            if (articles[lines[i].ArticleId].Type != ArticleType.Stock)
                notStocked.Add(i, ArticleField, $"The article with id '{lines[i].ArticleId}' is a service.");
        }
        if (notStocked.Any)
            return notStocked.ToError(ErrorCodes.ArticleNotStocked,
                "Only articles of type \"stock\" can be on a stock document; a service has no stock.");

        var notOnArticle = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].UnitId is { } unit && facts.Factor(lines[i].ArticleId, unit) is null)
                notOnArticle.Add(i, UnitField, $"The unit with id '{unit}' is neither the base unit nor an alternative unit of the article with id '{lines[i].ArticleId}'.");
        }
        if (notOnArticle.Any)
            return notOnArticle.ToError(ErrorCodes.UnitNotOnArticle,
                "A line is in a unit that its article is not handled in. Use the article's base unit (leave unitId out) or one of its "
                + "alternative units, or first give the article a conversion for that unit.");

        var entries = lines
            .Select(l => new StockLineEntry(l.ArticleId, l.UnitId ?? articles[l.ArticleId].BaseUnitId, l.Quantity))
            .ToList();
        var converted = Convert(entries, facts);
        if (!converted.IsSuccess)
            return converted.Error;
        return entries;
    }

    /// <summary>
    /// Converts every line with the factors of this moment (spec 007, R14, R15, R17): the factor and the base
    /// quantity per line, in line order, or QUANTITY_NOT_CONVERTIBLE with the quantity key of every line whose
    /// base quantity would be zero or above the maximum.
    /// </summary>
    /// <exception cref="InvalidOperationException">A line is in a unit that is not a unit of its article; saving never allows that.</exception>
    public static Result<IReadOnlyList<ConvertedLine>> Convert(IReadOnlyList<StockLineEntry> lines, StockLineFacts facts)
    {
        var converted = new List<ConvertedLine>(lines.Count);
        var notConvertible = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var factor = facts.Factor(line.ArticleId, line.UnitId)
                ?? throw new InvalidOperationException($"Line {i + 1} is in a unit that is not a unit of its article.");
            if (!UnitConversion.TryToBase(line.Quantity, factor, out var baseQuantity))
                notConvertible.Add(i, QuantityField,
                    $"{Text(line.Quantity)} × factor {Text(factor)} is {Text(baseQuantity)} in the article's base unit; "
                    + $"a line must convert to more than 0 and at most {Text(QuantityRules.Max)}.");
            converted.Add(new ConvertedLine(line.ArticleId, factor, baseQuantity));
        }
        if (notConvertible.Any)
            return notConvertible.ToError(ErrorCodes.QuantityNotConvertible,
                "A line's quantity does not convert to a quantity in the article's base unit with the article's current factor: "
                + "the result rounds to zero or exceeds the maximum. Nothing was saved or posted. Change the quantity or the unit of the line, "
                + "or correct the conversion factor.");
        return converted;
    }

    /// <summary>
    /// Posting needs every master of the document active, also those assigned while they still were
    /// (R13; spec 006, R5): the warehouse(s) and every article. All inactive ones are reported together.
    /// Units are not re-checked (spec 007, R18).
    /// </summary>
    /// <param name="inactiveWarehouses">The error key and id of each inactive warehouse of the document.</param>
    public static AppError? ActiveForPosting(
        IReadOnlyList<(string Field, Guid Id)> inactiveWarehouses,
        IReadOnlyList<StockLineEntry> lines, IReadOnlyDictionary<Guid, ArticleFacts> articles)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (field, id) in inactiveWarehouses)
            errors[field] = [$"The record with id '{id}' is inactive."];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!articles.TryGetValue(lines[i].ArticleId, out var facts) || !facts.IsActive)
                errors[StockDocumentValidation.LineKey(i, ArticleField)] = [$"The record with id '{lines[i].ArticleId}' is inactive."];
        }
        return errors.Count == 0
            ? null
            : new AppError(ErrorCodes.ReferenceInactive,
                "A warehouse or an article of the document is inactive; reactivate it or change the draft, then post again. Nothing was posted.",
                errors);
    }

    /// <summary>
    /// No negative stock (R15; spec 007, R19): null when stock covers the outgoing lines, otherwise
    /// INSUFFICIENT_STOCK with the quantity key of every line of every short article.
    /// </summary>
    /// <param name="lines">The lines in base quantities.</param>
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
            errors[StockDocumentValidation.LineKey(index, QuantityField)] =
                [$"The document takes {Text(requested)} of this article in total, in its base unit; {Text(onHand.GetValueOrDefault(article))} is on hand in the warehouse."];
        }
        return new AppError(ErrorCodes.InsufficientStock,
            "Stock on hand does not cover the document; nothing was posted and the draft is unchanged. "
            + "Check stock on hand, then lower the quantities or receive stock first, and post again.",
            errors);
    }

    /// <summary>
    /// No negative stock, also by reversal (spec 006, R16): null when every (article, warehouse) pair stays at
    /// or above zero after the reversing <paramref name="movements"/>, otherwise INSUFFICIENT_STOCK with the
    /// quantity key of every line of the original that names a short article.
    /// </summary>
    /// <param name="originalLines">The posted lines of the original in base quantities, in line order.</param>
    public static AppError? ReversalSufficiency(
        IReadOnlyList<StockLineValues> originalLines, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand)
    {
        var shortArticles = StockMovements.ShortArticles(movements, onHand);
        if (shortArticles.Count == 0)
            return null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var index = 0; index < originalLines.Count; index++)
        {
            var article = originalLines[index].ArticleId;
            if (!shortArticles.Contains(article))
                continue;
            var worst = movements
                .Where(m => m.ArticleId == article)
                .GroupBy(m => m.WarehouseId)
                .Select(g => (Needed: -g.Sum(m => m.Quantity), OnHand: onHand.GetValueOrDefault((article, g.Key))))
                .First(p => p.Needed > p.OnHand);
            errors[StockDocumentValidation.LineKey(index, QuantityField)] =
                [$"The reversal takes {Text(worst.Needed)} of this article back out of the warehouse it was brought into; only {Text(worst.OnHand)} is still on hand there."];
        }
        return new AppError(ErrorCodes.InsufficientStock,
            "The goods this document brought in have already left: reversing it would make stock negative. "
            + "Nothing was reversed and the document is still posted. Reverse the later documents that took the goods out first, "
            + "or receive stock, then reverse again.",
            errors);
    }

    private static string Text(decimal quantity) => QuantityRules.Normalize(quantity).ToString(CultureInfo.InvariantCulture);

    /// <summary>Errors of one kind, keyed <c>lines[i].field</c>.</summary>
    private sealed class LineErrors
    {
        private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

        public bool Any => _errors.Count > 0;

        public void Add(int index, string field, string message) => _errors[StockDocumentValidation.LineKey(index, field)] = [message];

        public AppError ToError(string code, string detail) => new(code, detail, _errors);
    }
}
