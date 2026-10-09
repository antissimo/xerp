namespace Xerp.Domain.Inventory;

/// <summary>A line as a caller gives it: an article and a quantity in the article's base unit.</summary>
public readonly record struct StockLineValues(Guid ArticleId, decimal Quantity);

/// <summary>
/// Rule for a quantity on a document line (ADR-0012, decision 7; spec 005, R6): an exact decimal greater than
/// zero, with at most six decimal places, at most 999,999,999.999999. A value outside the rule is rejected,
/// never rounded.
/// </summary>
public static class QuantityRules
{
    public const int DecimalPlaces = 6;
    public const decimal Max = 999_999_999.999999m;

    public static bool IsValid(decimal quantity) =>
        quantity > 0 && quantity <= Max && decimal.Round(quantity, DecimalPlaces) == quantity;

    /// <summary>The same value without trailing zeros (<c>100.000000</c> becomes <c>100</c>), so it is written one way everywhere.</summary>
    public static decimal Normalize(decimal quantity) => quantity / 1.0000000000000000000000000000m;
}

/// <summary>
/// The number a stock document gets when it is posted (ADR-0012, decision 6; spec 005, R17): the prefix of its
/// type and the tenant's counter value, padded with zeros to at least six digits.
/// </summary>
public static class DocumentNumber
{
    public const int MinDigits = 6;

    public static string Prefix(StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => "SR",
        StockDocumentType.Issue => "SI",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string Format(StockDocumentType type, long counter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(counter, 1);
        return $"{Prefix(type)}-{counter.ToString("D" + MinDigits, System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// No negative stock (ADR-0012, decision 4; spec 005, R15): for every article of an outgoing document, the sum
/// of its line quantities must not exceed the stock on hand of that article in the document's warehouse.
/// </summary>
public static class StockSufficiency
{
    /// <summary>
    /// The zero-based positions of all lines of every short article, ascending; empty when stock covers the
    /// document. An article missing from <paramref name="onHand"/> has zero stock.
    /// </summary>
    public static IReadOnlyList<int> ShortLines(IReadOnlyList<StockLineValues> lines, IReadOnlyDictionary<Guid, decimal> onHand)
    {
        var shortArticles = lines
            .GroupBy(l => l.ArticleId)
            .Where(g => g.Sum(l => l.Quantity) > onHand.GetValueOrDefault(g.Key))
            .Select(g => g.Key)
            .ToHashSet();
        return Enumerable.Range(0, lines.Count).Where(i => shortArticles.Contains(lines[i].ArticleId)).ToList();
    }
}
