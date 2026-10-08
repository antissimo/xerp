using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// "The standard setup" of spec 005, section 10: a tenant with unit <c>pcs</c>, stock articles <c>A</c> and
/// <c>B</c>, service article <c>S</c> and warehouses <c>W1</c> and <c>W2</c>, all created through the API.
/// </summary>
public sealed record StockSetup(
    TestTenant Tenant, JsonElement Unit, JsonElement ArticleA, JsonElement ArticleB, JsonElement ArticleS,
    JsonElement Warehouse1, JsonElement Warehouse2)
{
    public HttpClient Http => Tenant.Client;
    public Guid UnitId => Unit.Id();
    public Guid A => ArticleA.Id();
    public Guid B => ArticleB.Id();
    public Guid S => ArticleS.Id();
    public Guid W1 => Warehouse1.Id();
    public Guid W2 => Warehouse2.Id();
}

/// <summary>Shortcuts for stock documents, stock on hand and the stock ledger (spec 005, section 4).</summary>
public static class Stock
{
    public const string Documents = "/api/v1/stock-documents";
    public const string OnHand = "/api/v1/stock-on-hand";
    public const string Ledger = "/api/v1/stock-ledger-entries";
    public const string Date = "2026-10-09";

    public static async Task<StockSetup> SetupAsync(XerpFixture app, TestTenant? tenant = null)
    {
        tenant ??= await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = await Uom.CreateAsync(http, "pcs", "Piece");
        return new StockSetup(
            tenant, unit,
            await Art.CreateAsync(http, "A", "Article A", unit.Id()),
            await Art.CreateAsync(http, "B", "Article B", unit.Id()),
            await Art.CreateAsync(http, "S", "Service S", unit.Id(), "service"),
            await MasterApi.Warehouses.CreateAsync(http, "W1", "Warehouse one"),
            await MasterApi.Warehouses.CreateAsync(http, "W2", "Warehouse two"));
    }

    // ---- request bodies ----

    public static JsonObject Line(Guid article, JsonNode? quantity) =>
        new() { ["articleId"] = article.ToString(), ["quantity"] = quantity };

    public static JsonArray Lines(params (Guid Article, decimal Quantity)[] lines) =>
        new(lines.Select(l => (JsonNode)Line(l.Article, l.Quantity)).ToArray());

    /// <summary>A create body with the required properties only.</summary>
    public static JsonObject Draft(string type, Guid warehouse, params (Guid Article, decimal Quantity)[] lines) => new()
    {
        ["type"] = type, ["documentDate"] = Date, ["warehouseId"] = warehouse.ToString(), ["lines"] = Lines(lines),
    };

    /// <summary>A replace body: all five properties present, <c>reference</c> and <c>note</c> null.</summary>
    public static JsonObject Replacement(Guid warehouse, params (Guid Article, decimal Quantity)[] lines) => new()
    {
        ["documentDate"] = Date, ["warehouseId"] = warehouse.ToString(), ["reference"] = null, ["note"] = null,
        ["lines"] = Lines(lines),
    };

    // ---- documents ----

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) =>
        client.PostAsJsonAsync(Documents, body);

    public static async Task<JsonElement> CreateAsync(HttpClient client, JsonObject body)
    {
        using var response = await PostAsync(client, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static Task<JsonElement> CreateAsync(HttpClient client, string type, Guid warehouse, params (Guid Article, decimal Quantity)[] lines) =>
        CreateAsync(client, Draft(type, warehouse, lines));

    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, JsonObject body) =>
        client.PutAsJsonAsync($"{Documents}/{id}", body);

    public static async Task<JsonElement> ReplaceAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Documents}/{id}");

    /// <summary><c>POST /stock-documents/{id}/post</c>, the raw response.</summary>
    public static Task<HttpResponseMessage> SendPostAsync(HttpClient client, Guid id) =>
        client.PostAsync($"{Documents}/{id}/post", null);

    /// <summary>Posts a draft and asserts <c>200</c>; returns the posted document.</summary>
    public static async Task<JsonElement> PostDocumentAsync(HttpClient client, Guid id)
    {
        using var response = await SendPostAsync(client, id);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"{Documents}/{id}");
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<HttpResponseMessage> ByNumberAsync(HttpClient client, string number) =>
        client.GetAsync($"{Documents}/by-number/{Uri.EscapeDataString(number)}");

    /// <summary>"Receive n of an article into a warehouse": a receipt with one line, posted.</summary>
    public static async Task<JsonElement> ReceiveAsync(HttpClient client, Guid warehouse, Guid article, decimal quantity) =>
        await PostDocumentAsync(client, (await CreateAsync(client, "receipt", warehouse, (article, quantity))).Id());

    /// <summary>An issue with one line, posted (fails the test when the posting is refused).</summary>
    public static async Task<JsonElement> IssueAsync(HttpClient client, Guid warehouse, Guid article, decimal quantity) =>
        await PostDocumentAsync(client, (await CreateAsync(client, "issue", warehouse, (article, quantity))).Id());

    // ---- queries ----

    public static async Task<JsonElement> ListAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var list = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Array, list.GetProperty("items").ValueKind);
        return list;
    }

    public static Task<JsonElement> DocumentsAsync(HttpClient client, string query = "") => ListAsync(client, Documents + query);

    public static Task<JsonElement> OnHandAsync(HttpClient client, string query = "") => ListAsync(client, OnHand + query);

    public static Task<JsonElement> LedgerAsync(HttpClient client, string query = "") => ListAsync(client, Ledger + query);

    /// <summary>"Stock(article, warehouse)": the quantity of the pair in stock on hand, or 0 when it is not listed.</summary>
    public static async Task<decimal> QuantityAsync(HttpClient client, Guid article, Guid warehouse)
    {
        var list = await OnHandAsync(client, $"?articleId={article}&warehouseId={warehouse}");
        var items = list.Items();
        Assert.True(items.Length <= 1, $"One pair must be one item of stock on hand: {list}");
        Assert.Equal(items.Length, list.Total());
        if (items.Length == 0)
            return 0m;
        Assert.Equal(article, items[0].GetProperty("article").Id());
        Assert.Equal(warehouse, items[0].GetProperty("warehouse").Id());
        return items[0].Quantity();
    }

    /// <summary>The sum of the ledger quantities of one pair (R19).</summary>
    public static async Task<decimal> LedgerSumAsync(HttpClient client, Guid article, Guid warehouse)
    {
        var list = await LedgerAsync(client, $"?articleId={article}&warehouseId={warehouse}&limit=500");
        Assert.Equal(list.Total(), list.Items().Length);
        return list.Items().Sum(e => e.Quantity());
    }

    // ---- assertions ----

    /// <summary>A <c>409</c> problem with the given code whose <c>errors</c> has (at least) the given keys.</summary>
    public static async Task<JsonElement> ConflictAsync(HttpResponseMessage response, string code, params string[] errorKeys)
    {
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, code);
        var keys = McpAssert.ErrorKeys(problem);
        foreach (var key in errorKeys)
            Assert.True(keys.Contains(key), $"{code}: errors has no key '{key}', keys are [{string.Join(", ", keys)}]: {problem}");
        return problem;
    }

    /// <summary>Asserts that the document is still a draft without number and posting data (R9, R12).</summary>
    public static async Task<JsonElement> AssertDraftAsync(HttpClient client, Guid id)
    {
        var document = await GetAsync(client, id);
        Assert.Equal("draft", document.Str("status"));
        JsonBody.AssertNull(document, "number", "postedAt", "postedBy");
        return document;
    }

    /// <summary>Asserts that a refused operation left the document exactly as it was.</summary>
    public static async Task AssertUnchangedAsync(HttpClient client, JsonElement before) =>
        McpAssert.JsonEqual(before, await GetAsync(client, before.Id()), "The stock document changed");

    public static JsonElement[] Items(this JsonElement list) => list.GetProperty("items").EnumerateArray().ToArray();

    public static decimal Quantity(this JsonElement lineOrEntry) => lineOrEntry.GetProperty("quantity").GetDecimal();

    public static JsonElement[] DocumentLines(this JsonElement document) => document.GetProperty("lines").EnumerateArray().ToArray();

    public static string? Number(this JsonElement document) => document.GetProperty("number").GetString();

    // ---- masters used by stock documents ----

    /// <summary>The replace body (spec 002, section 4) that keeps every value of the given article.</summary>
    public static JsonObject ArticleBody(JsonElement article) => new()
    {
        ["code"] = article.Str("code"), ["name"] = article.Str("name"),
        ["description"] = article.GetProperty("description").GetString(), ["type"] = article.Str("type"),
        ["baseUnitId"] = article.GetProperty("baseUnit").Id().ToString(), ["isActive"] = article.Bool("isActive"),
    };

    public static Task<HttpResponseMessage> PutArticleAsync(HttpClient client, Guid id, JsonObject body) =>
        client.PutAsJsonAsync($"{Art.Path}/{id}", body);

    public static async Task<JsonElement> ReplaceArticleAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutArticleAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task SetArticleActiveAsync(HttpClient client, Guid id, bool isActive) =>
        await ReplaceArticleAsync(client, id, ArticleBody(await Art.GetAsync(client, id)).With("isActive", isActive));

    /// <summary>The replace body (spec 004, 4.2) that keeps every value of the given warehouse.</summary>
    public static JsonObject WarehouseBody(JsonElement warehouse)
    {
        var body = new JsonObject();
        foreach (var field in MasterApi.Warehouses.ReplaceFields)
            body[field] = JsonNode.Parse(warehouse.GetProperty(field).GetRawText());
        return body;
    }

    public static async Task SetWarehouseActiveAsync(HttpClient client, Guid id, bool isActive)
    {
        var w = MasterApi.Warehouses;
        await w.ReplaceAsync(client, id, WarehouseBody(await w.GetAsync(client, id)).With("isActive", isActive));
    }
}
