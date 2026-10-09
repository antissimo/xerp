using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The setup of spec 007, section 10: the standard setup of spec 005 plus units <c>box</c> and <c>pack</c> and
/// the conversion A / box = 12, set through the API. B has no conversions.
/// </summary>
public sealed record UnitSetup(StockSetup S, JsonElement BoxUnit, JsonElement PackUnit)
{
    public TestTenant Tenant => S.Tenant;
    public HttpClient Http => S.Http;
    public Guid A => S.A;
    public Guid B => S.B;
    public Guid Service => S.S;
    public Guid W1 => S.W1;
    public Guid W2 => S.W2;
    public Guid Pcs => S.UnitId;
    public Guid Box => BoxUnit.Id();
    public Guid Pack => PackUnit.Id();
}

/// <summary>Shortcuts for article units (spec 007, section 4.1) and for stock document lines with a unit (4.3).</summary>
public static class Units
{
    public static string Path(Guid article) => $"{Art.Path}/{article}/units";

    public static string Path(Guid article, Guid unit) => $"{Art.Path}/{article}/units/{unit}";

    public static async Task<UnitSetup> SetupAsync(XerpFixture app, TestTenant? tenant = null)
    {
        var s = await Stock.SetupAsync(app, tenant);
        var box = await Uom.CreateAsync(s.Http, "box", "Box");
        var pack = await Uom.CreateAsync(s.Http, "pack", "Pack");
        var setup = new UnitSetup(s, box, pack);
        using var response = await PutAsync(s.Http, s.A, setup.Box, 12);
        await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        return setup;
    }

    // ---- conversions ----

    public static JsonObject Body(JsonNode? factor) => new() { ["factor"] = factor };

    /// <summary><c>PUT /articles/{articleId}/units/{unitId}</c> with any body, the raw response.</summary>
    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid article, Guid unit, JsonObject body) =>
        client.PutAsJsonAsync(Path(article, unit), body);

    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid article, Guid unit, decimal factor) =>
        PutAsync(client, article, unit, Body(factor));

    /// <summary>Sets a conversion and asserts <c>201</c> or <c>200</c> (R3); returns the ArticleUnit.</summary>
    public static async Task<JsonElement> SetAsync(HttpClient client, Guid article, Guid unit, decimal factor)
    {
        using var response = await PutAsync(client, article, unit, factor);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Expected 201 or 200 from Set, got {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static Task<HttpResponseMessage> SendGetAsync(HttpClient client, Guid article, Guid unit) =>
        client.GetAsync(Path(article, unit));

    public static async Task<JsonElement> GetAsync(HttpClient client, Guid article, Guid unit)
    {
        using var response = await SendGetAsync(client, article, unit);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> ListAsync(HttpClient client, Guid article, string query = "")
    {
        using var response = await client.GetAsync(Path(article) + query);
        var list = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Array, list.GetProperty("items").ValueKind);
        return list;
    }

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid article, Guid unit) =>
        client.DeleteAsync(Path(article, unit));

    /// <summary>Deletes a conversion and asserts <c>204</c>.</summary>
    public static async Task RemoveAsync(HttpClient client, Guid article, Guid unit)
    {
        using var response = await DeleteAsync(client, article, unit);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public static decimal Factor(this JsonElement conversionOrLine) => conversionOrLine.GetProperty("factor").GetDecimal();

    public static decimal BaseQuantity(this JsonElement line) => line.GetProperty("baseQuantity").GetDecimal();

    /// <summary>The unit codes of a list of ArticleUnit, in list order.</summary>
    public static string[] UnitCodes(this JsonElement list) =>
        list.Items().Select(i => i.GetProperty("unit").Str("code")).ToArray();

    /// <summary>The factor of a conversion, or <c>null</c> when it does not exist (<c>404</c>).</summary>
    public static async Task<decimal?> FactorOrNullAsync(HttpClient client, Guid article, Guid unit)
    {
        using var response = await SendGetAsync(client, article, unit);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return (await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Factor();
    }

    // ---- units of measure ----

    public static Task<HttpResponseMessage> PutUnitAsync(HttpClient client, Guid id, string code, string name, bool isActive) =>
        client.PutAsJsonAsync($"{Uom.Path}/{id}", new { code, name, isActive });

    /// <summary>Replaces a unit of measure keeping its code and name (spec 001) and asserts <c>200</c>.</summary>
    public static async Task SetUnitActiveAsync(HttpClient client, JsonElement unit, bool isActive)
    {
        using var response = await PutUnitAsync(client, unit.Id(), unit.Str("code"), unit.Str("name"), isActive);
        await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<HttpResponseMessage> DeleteUnitAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Uom.Path}/{id}");

    // ---- stock document lines with a unit ----

    /// <summary>A line; <paramref name="unit"/> <c>null</c> leaves <c>unitId</c> out of the JSON.</summary>
    public static JsonObject Line(Guid article, decimal quantity, Guid? unit = null)
    {
        var line = Stock.Line(article, quantity);
        return unit is null ? line : line.With("unitId", unit.Value.ToString());
    }

    public static JsonArray Lines(params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        new(lines.Select(l => (JsonNode)Line(l.Article, l.Quantity, l.Unit)).ToArray());

    /// <summary>A create body of a receipt or an issue whose lines may name a unit.</summary>
    public static JsonObject Draft(string type, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        Stock.Draft(type, warehouse).With("lines", Lines(lines));

    public static JsonObject Transfer(Guid from, Guid to, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        Draft("transfer", from, lines).With("toWarehouseId", to.ToString());

    public static JsonObject Replacement(Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        Stock.Replacement(warehouse).With("lines", Lines(lines));

    public static Task<JsonElement> CreateAsync(HttpClient client, string type, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        Stock.CreateAsync(client, Draft(type, warehouse, lines));

    /// <summary>Creates and posts a receipt or an issue whose lines may name a unit; asserts <c>200</c> on post.</summary>
    public static async Task<JsonElement> PostedAsync(HttpClient client, string type, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        await Stock.PostDocumentAsync(client, (await CreateAsync(client, type, warehouse, lines)).Id());

    public static async Task<JsonElement> PostedTransferAsync(HttpClient client, Guid from, Guid to, params (Guid Article, decimal Quantity, Guid? Unit)[] lines) =>
        await Stock.PostDocumentAsync(client, (await Stock.CreateAsync(client, Transfer(from, to, lines))).Id());

    /// <summary>
    /// Asserts the five unit properties of a line representation (section 4.3): what was entered
    /// (<c>unit</c>, <c>quantity</c>) and what it is in base units (<c>factor</c>, <c>baseUnit</c>, <c>baseQuantity</c>).
    /// </summary>
    public static void AssertLine(JsonElement line, string unitCode, decimal quantity, decimal factor, decimal baseQuantity, string baseUnitCode = "pcs")
    {
        Assert.True(line.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.Object, $"The line has no unit: {line}");
        Assert.True(line.TryGetProperty("baseUnit", out var baseUnit) && baseUnit.ValueKind == JsonValueKind.Object, $"The line has no baseUnit: {line}");
        Assert.True(line.TryGetProperty("factor", out var f) && f.ValueKind == JsonValueKind.Number, $"The line has no factor: {line}");
        Assert.True(line.TryGetProperty("baseQuantity", out var b) && b.ValueKind == JsonValueKind.Number, $"The line has no baseQuantity: {line}");
        var actual = (unit.Str("code"), line.Quantity(), f.GetDecimal(), baseUnit.Str("code"), b.GetDecimal());
        Assert.True(actual == (unitCode, quantity, factor, baseUnitCode, baseQuantity),
            $"Line (unit, quantity, factor, baseUnit, baseQuantity) is {actual}, expected {(unitCode, quantity, factor, baseUnitCode, baseQuantity)}: {line}");
    }

    /// <summary>
    /// R19: every ledger entry of a posted document is plus or minus the <c>baseQuantity</c> of its line, in the
    /// article's base unit. Returns the entries.
    /// </summary>
    public static async Task<JsonElement[]> AssertEntriesAreBaseQuantitiesAsync(HttpClient client, JsonElement posted)
    {
        var lines = posted.DocumentLines().ToDictionary(l => l.GetProperty("lineNo").GetInt32());
        var entries = await Stock.EntriesAsync(client, posted.Id());
        var perLine = posted.Str("type") == "transfer" ? 2 : 1;
        Assert.Equal(lines.Count * perLine, entries.Length);
        foreach (var entry in entries)
        {
            var line = lines[entry.GetProperty("lineNo").GetInt32()];
            Assert.True(Math.Abs(entry.Quantity()) == line.BaseQuantity(),
                $"Ledger entry {entry.Quantity()} is not plus or minus the line's baseQuantity {line.BaseQuantity()}: {entry}");
            Assert.Equal(line.GetProperty("article").Id(), entry.GetProperty("article").Id());
            Assert.Equal(line.GetProperty("baseUnit").Id(), entry.GetProperty("unit").Id());
        }
        return entries;
    }
}
