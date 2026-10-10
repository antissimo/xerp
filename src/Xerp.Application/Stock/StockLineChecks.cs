using System.Globalization;
using Xerp.Application.Common;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;
using Xerp.Domain.Rules;

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
    public const string OrderLineField = "orderLineNo";

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
    /// <param name="type">The type of the document: on a count a counted quantity of zero converts (spec 008, R5). The lines of an order are judged like those of a receipt.</param>
    /// <param name="orderLines">
    /// For a document linked to an order (spec 009, R21): the line numbers the order has. A line whose
    /// <c>orderLineNo</c> is not among them names something that does not exist, like an unknown article. Null
    /// for an unlinked document and for the lines of an order itself.
    /// </param>
    public static Result<IReadOnlyList<StockLineEntry>> References(
        IReadOnlyList<StockLineRequest> lines, StockLineFacts facts, IReadOnlySet<Guid> articlesOnDocument, IReadOnlySet<Guid> unitsOnDocument,
        StockDocumentType type, IReadOnlyCollection<int>? orderLines = null)
    {
        var articles = facts.Articles;

        var unknown = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!articles.ContainsKey(lines[i].ArticleId))
                unknown.Add(i, ArticleField, $"No record with id '{lines[i].ArticleId}' exists.");
            if (lines[i].UnitId is { } unit && !facts.Units.ContainsKey(unit))
                unknown.Add(i, UnitField, $"No record with id '{unit}' exists.");
            if (orderLines is not null && lines[i].OrderLineNo is { } orderLineNo && !orderLines.Contains(orderLineNo))
                unknown.Add(i, OrderLineField, $"The order has no line {orderLineNo}; its lines are numbered 1 to {orderLines.Count}.");
        }
        if (unknown.Any)
            return unknown.ToError(ErrorCodes.ReferenceNotFound,
                orderLines is null
                    ? "A line names an article or a unit of measure that does not exist."
                    : "A line names an article, a unit of measure or an order line that does not exist.");

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
                "Only articles of type \"stock\" can be on a stock document or an order; a service has no stock.");

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
            .Select(l => new StockLineEntry(l.ArticleId, l.UnitId ?? articles[l.ArticleId].BaseUnitId, l.Quantity, l.OrderLineNo))
            .ToList();
        var converted = Convert(entries, facts, type);
        if (!converted.IsSuccess)
            return converted.Error;
        return entries;
    }

    /// <summary>
    /// Converts every line with the factors of this moment (spec 007, R14, R15, R17): the factor and the base
    /// quantity per line, in line order, or QUANTITY_NOT_CONVERTIBLE with the quantity key of every line whose
    /// base quantity would be zero or above the maximum. On a count a counted quantity of zero converts to zero
    /// (spec 008, R5); a counted quantity greater than zero obeys the same rule as any other line.
    /// </summary>
    /// <exception cref="InvalidOperationException">A line is in a unit that is not a unit of its article; saving never allows that.</exception>
    public static Result<IReadOnlyList<ConvertedLine>> Convert(
        IReadOnlyList<StockLineEntry> lines, StockLineFacts facts, StockDocumentType type)
    {
        var converted = new List<ConvertedLine>(lines.Count);
        var notConvertible = new LineErrors();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var factor = facts.Factor(line.ArticleId, line.UnitId)
                ?? throw new InvalidOperationException($"Line {i + 1} is in a unit that is not a unit of its article.");
            if (!UnitConversion.TryToBaseOn(type, line.Quantity, factor, out var baseQuantity))
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
    /// (R13; spec 006, R5): the warehouse(s) and every article. Header before lines: the inactive warehouses are
    /// reported together and alone; inactive articles only when the header is clean, all of them together.
    /// Units are not re-checked (spec 007, R18).
    /// </summary>
    /// <param name="inactiveWarehouses">The error key and id of each inactive warehouse of the document.</param>
    public static AppError? ActiveForPosting(
        IReadOnlyList<(string Field, Guid Id)> inactiveWarehouses,
        IReadOnlyList<StockLineEntry> lines, IReadOnlyDictionary<Guid, ArticleFacts> articles) =>
        ActiveMasters(inactiveWarehouses, lines, articles,
            "A warehouse, the partner or an article of the document is inactive; reactivate it or change the draft, then post again. Nothing was posted.");

    /// <summary>
    /// The rule behind <see cref="ActiveForPosting"/>, for any step that needs every master active (spec 005,
    /// R13; spec 009, R12): the inactive header masters together and alone, then every inactive article.
    /// </summary>
    /// <param name="inactiveHeader">The error key and id of each inactive master the header names, in the order of the keys.</param>
    public static AppError? ActiveMasters(
        IReadOnlyList<(string Field, Guid Id)> inactiveHeader,
        IReadOnlyList<StockLineEntry> lines, IReadOnlyDictionary<Guid, ArticleFacts> articles, string detail)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (field, id) in inactiveHeader)
            errors[field] = [$"The record with id '{id}' is inactive."];
        var headerIsClean = errors.Count == 0;
        for (var i = 0; headerIsClean && i < lines.Count; i++)
        {
            if (!articles.TryGetValue(lines[i].ArticleId, out var facts) || !facts.IsActive)
                errors[StockDocumentValidation.LineKey(i, ArticleField)] = [$"The record with id '{lines[i].ArticleId}' is inactive."];
        }
        return errors.Count == 0
            ? null
            : new AppError(ErrorCodes.ReferenceInactive, detail, errors);
    }

    /// <summary>
    /// The rule <c>stock.negativeStockAllowed</c> at posting (R15; spec 006, R7; spec 007, R19; spec 012, R17,
    /// R18): null when no (article, warehouse) pair is lowered to below zero by the document's
    /// <paramref name="movements"/> - and always when the tenant allows negative stock - otherwise
    /// INSUFFICIENT_STOCK with the quantity key of every line of every short article, naming the rule.
    /// </summary>
    /// <param name="lines">The lines in base quantities.</param>
    /// <param name="movements">What posting the lines moves, in base quantities.</param>
    /// <param name="onHand">Stock on hand of the pairs the movements touch; a missing pair has none.</param>
    /// <param name="negativeStockAllowed">The tenant's value of the rule.</param>
    public static AppError? Sufficiency(
        IReadOnlyList<StockLineValues> lines, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand, bool negativeStockAllowed) =>
        Short(lines, movements, onHand, negativeStockAllowed,
            (taken, there) => $"The document takes {Text(taken)} of this article in total, in its base unit; {Text(there)} is on hand in the warehouse.",
            "Stock on hand does not cover the document; nothing was posted and the draft is unchanged. "
            + "Check stock on hand, then lower the quantities or receive stock first, and post again.");

    /// <summary>
    /// The same rule at a reversal (spec 006, R16; spec 012, R17): null when no pair is lowered to below zero
    /// by the reversing <paramref name="movements"/>, otherwise INSUFFICIENT_STOCK with the quantity key of
    /// every line of the original that names a short article, naming the rule.
    /// </summary>
    /// <param name="originalLines">The posted lines of the original in base quantities, in line order.</param>
    public static AppError? ReversalSufficiency(
        IReadOnlyList<StockLineValues> originalLines, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand, bool negativeStockAllowed) =>
        Short(originalLines, movements, onHand, negativeStockAllowed,
            (taken, there) => $"The reversal takes {Text(taken)} of this article back out of the warehouse it was brought into; only {Text(there)} is still on hand there.",
            "The goods this document brought in have already left: reversing it would make stock negative. "
            + "Nothing was reversed and the document is still posted. Reverse the later documents that took the goods out first, "
            + "or receive stock, then reverse again.");

    /// <summary>The one refusal for stock that would go below zero: posting and reversal differ in their words only.</summary>
    private static AppError? Short(
        IReadOnlyList<StockLineValues> lines, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand, bool negativeStockAllowed,
        Func<decimal, decimal, string> message, string detail)
    {
        var shortPairs = StockMovements.ShortPairs(movements, onHand, negativeStockAllowed);
        if (shortPairs.Count == 0)
            return null;
        var errors = LineErrorsOf(lines, shortPairs, pair => message(Taken(movements, pair), onHand.GetValueOrDefault(pair)));
        return new AppError(ErrorCodes.InsufficientStock, detail, errors).RefusedBy(RuleRegistry.NegativeStockAllowed, negativeStockAllowed);
    }

    /// <summary>
    /// The rule <c>sales.reservedStockProtected</c> at a posting and at a reversal (spec 012, R27, R28): null
    /// when the <paramref name="movements"/> take nothing that confirmed sales orders reserve - and always when
    /// the tenant does not protect reserved stock - otherwise STOCK_RESERVED with the quantity key of every
    /// line with the article of a pair that is taken from, naming the rule.
    /// </summary>
    /// <param name="lines">The lines of the document - of the original, for a reversal - in base quantities, in line order.</param>
    /// <param name="reservedBefore">The reserved quantity of the pairs the movements lower, as it is now.</param>
    /// <param name="reservedAfter">As it would be after the operation: a delivery lowers what its own order still awaits.</param>
    /// <param name="reservedStockProtected">The tenant's value of the rule.</param>
    /// <param name="reversal">True when a posted document is being reversed; it changes the words only.</param>
    public static AppError? Reservation(
        IReadOnlyList<StockLineValues> lines, IReadOnlyCollection<StockMovement> movements,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> onHand,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> reservedBefore,
        IReadOnlyDictionary<(Guid ArticleId, Guid WarehouseId), decimal> reservedAfter,
        bool reservedStockProtected, bool reversal)
    {
        var reservedPairs = StockMovements.ReservedPairs(movements, onHand, reservedBefore, reservedAfter, reservedStockProtected);
        if (reservedPairs.Count == 0)
            return null;
        var errors = LineErrorsOf(lines, reservedPairs, pair =>
            $"The {(reversal ? "reversal" : "document")} takes {Text(Taken(movements, pair))} of this article out of the warehouse, in its base unit; "
            + $"{Text(onHand.GetValueOrDefault(pair))} is on hand there and {Text(reservedAfter.GetValueOrDefault(pair))} stays reserved for confirmed sales orders.");
        return new AppError(ErrorCodes.StockReserved,
            "The goods are reserved for confirmed sales orders: afterwards stock on hand would no longer cover what those orders still await. "
            + (reversal ? "Nothing was reversed and the document is still posted. " : "Nothing was posted and the draft is unchanged. ")
            + "Check `availableQuantity` in stock on hand; take less, receive stock first, or deliver against the sales orders instead.",
            errors).RefusedBy(RuleRegistry.ReservedStockProtected, reservedStockProtected);
    }

    /// <summary>What the movements take out of a pair in total: the opposite of their sum.</summary>
    private static decimal Taken(IEnumerable<StockMovement> movements, (Guid ArticleId, Guid WarehouseId) pair) =>
        -movements.Where(m => (m.ArticleId, m.WarehouseId) == pair).Sum(m => m.Quantity);

    /// <summary>The quantity key of every line whose article is the article of a refused pair, in line order, with the message of the first such pair.</summary>
    private static Dictionary<string, string[]> LineErrorsOf(
        IReadOnlyList<StockLineValues> lines, IReadOnlyList<(Guid ArticleId, Guid WarehouseId)> pairs,
        Func<(Guid ArticleId, Guid WarehouseId), string> message)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            var article = lines[index].ArticleId;
            if (pairs.Any(p => p.ArticleId == article))
                errors[StockDocumentValidation.LineKey(index, QuantityField)] = [message(pairs.First(p => p.ArticleId == article))];
        }
        return errors;
    }

    /// <summary>
    /// A count is posted against the book quantity it shows (spec 008, R10): null when stock on hand of every
    /// line's article in the count's warehouse still equals the line's book quantity, otherwise COUNT_OUTDATED
    /// with the quantity key of every line for which it does not. Only the quantity is compared.
    /// </summary>
    /// <param name="bookQuantities">Per line, in line order, the article and the book quantity recorded when the draft was saved.</param>
    /// <param name="onHand">Stock on hand per article in the count's warehouse, as it is now; a missing article has none.</param>
    public static AppError? Current(IReadOnlyList<StockLineValues> bookQuantities, IReadOnlyDictionary<Guid, decimal> onHand)
    {
        var outdated = CountRules.OutdatedLines(bookQuantities, onHand);
        if (outdated.Count == 0)
            return null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var index in outdated)
            errors[StockDocumentValidation.LineKey(index, QuantityField)] =
                [$"The count was saved against a book quantity of {Text(bookQuantities[index].Quantity)}; stock on hand of this article in the warehouse is now {Text(onHand.GetValueOrDefault(bookQuantities[index].ArticleId))}, in its base unit."];
        return new AppError(ErrorCodes.CountOutdated,
            "Stock changed since the count was saved, so the difference it shows is no longer the difference to stock. Nothing was posted "
            + "and the count is still a draft. Read the document, check the counted quantities, save it again (that takes the current "
            + "book quantity) and post again.",
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
