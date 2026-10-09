using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

internal static class StockTestSupport
{
    /// <summary>
    /// Posts a document whose lines are all in the base unit of their article: factor 1 on every line
    /// (spec 007, R14) - what posting was before lines could name a unit.
    /// </summary>
    public static IReadOnlyList<StockLedgerEntry> PostInBaseUnits(this StockDocument document, string number, DateTime now, Guid actorKeyId) =>
        document.Post(number, Enumerable.Repeat(UnitConversion.BaseUnitFactor, document.Lines.Count).ToList(), now, actorKeyId);
}
