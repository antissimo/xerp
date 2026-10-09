using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// Shortcuts for stock counts (spec 008): a stock document of type <c>count</c> whose lines state the counted
/// quantity and show the book quantity they were saved against and the difference.
/// </summary>
public static class Counts
{
    /// <summary>A create body of a count; a line may name a unit (spec 007).</summary>
    public static JsonObject Draft(Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        Units.Draft("count", warehouse, lines);

    public static Task<JsonElement> CreateAsync(HttpClient client, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        Stock.CreateAsync(client, Draft(warehouse, lines));

    /// <summary>"Count n of an article in a warehouse": a draft count with one line.</summary>
    public static Task<JsonElement> CountAsync(HttpClient client, Guid warehouse, Guid article, decimal quantity) =>
        CreateAsync(client, warehouse, (article, quantity, null));

    /// <summary>Creates and posts a count; asserts <c>200</c> on post.</summary>
    public static async Task<JsonElement> PostedAsync(HttpClient client, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        await Stock.PostDocumentAsync(client, (await CreateAsync(client, warehouse, lines)).Id());

    /// <summary>The replace body that keeps every value of the given draft ("PUT with the same values").</summary>
    public static JsonObject SameValues(JsonElement document) => new()
    {
        ["documentDate"] = document.Str("documentDate"),
        ["warehouseId"] = document.GetProperty("warehouse").Id().ToString(),
        ["reference"] = document.GetProperty("reference").GetString(),
        ["note"] = document.GetProperty("note").GetString(),
        ["lines"] = new JsonArray(document.DocumentLines()
            .Select(l => (JsonNode)Units.Line(l.GetProperty("article").Id(), l.Quantity(), l.GetProperty("unit").Id()))
            .ToArray()),
    };

    /// <summary>Saves a draft count again with the same values (R6: this records the book quantities anew).</summary>
    public static async Task<JsonElement> ResaveAsync(HttpClient client, Guid id) =>
        await Stock.ReplaceAsync(client, id, SameValues(await Stock.GetAsync(client, id)));

    public static decimal Book(this JsonElement line) => line.GetProperty("bookQuantity").GetDecimal();

    public static decimal Difference(this JsonElement line) => line.GetProperty("differenceQuantity").GetDecimal();

    /// <summary>Asserts counted quantity, its base quantity, the book quantity and the difference of a count line.</summary>
    public static void AssertLine(JsonElement line, decimal quantity, decimal baseQuantity, decimal book, decimal difference)
    {
        Assert.True(line.TryGetProperty("bookQuantity", out var b) && b.ValueKind == JsonValueKind.Number, $"The count line has no bookQuantity: {line}");
        Assert.True(line.TryGetProperty("differenceQuantity", out var d) && d.ValueKind == JsonValueKind.Number,
            $"The count line has no differenceQuantity: {line}");
        var actual = (line.Quantity(), line.BaseQuantity(), b.GetDecimal(), d.GetDecimal());
        Assert.True(actual == (quantity, baseQuantity, book, difference),
            $"Count line (quantity, baseQuantity, bookQuantity, differenceQuantity) is {actual}, expected {(quantity, baseQuantity, book, difference)}: {line}");
    }

    /// <summary>A one-unit count line: counted <paramref name="quantity"/> in the base unit against <paramref name="book"/>.</summary>
    public static void AssertLine(JsonElement line, decimal quantity, decimal book) =>
        AssertLine(line, quantity, quantity, book, quantity - book);

    /// <summary>The ledger entries of a document as (lineNo, quantity), in ledger order.</summary>
    public static async Task<(int LineNo, decimal Quantity)[]> MovementsAsync(HttpClient client, Guid documentId) =>
        (await Stock.EntriesAsync(client, documentId)).Select(e => (e.GetProperty("lineNo").GetInt32(), e.Quantity())).ToArray();
}
