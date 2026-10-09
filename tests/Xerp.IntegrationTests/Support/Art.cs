using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Xerp.IntegrationTests.Support;

/// <summary>Shortcuts for the article endpoints (spec 002).</summary>
public static class Art
{
    public const string Path = "/api/v1/articles";

    /// <summary>A create body; optional fields are left out of the JSON when null.</summary>
    public static Dictionary<string, object?> Body(
        string code, string name, Guid baseUnitId, string type = "stock", string? description = null, bool? isActive = null)
    {
        var body = new Dictionary<string, object?> { ["code"] = code, ["name"] = name, ["type"] = type, ["baseUnitId"] = baseUnitId };
        if (description is not null) body["description"] = description;
        if (isActive is not null) body["isActive"] = isActive;
        return body;
    }

    /// <summary>A complete replace body (all six fields present).</summary>
    public static object ReplaceBody(
        string code, string name, Guid baseUnitId, string type = "stock", string? description = null, bool isActive = true) =>
        new { code, name, description, type, baseUnitId, isActive };

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string code, string name, Guid baseUnitId, string type = "stock", string? description = null, bool? isActive = null) =>
        client.PostAsJsonAsync(Path, Body(code, name, baseUnitId, type, description, isActive));

    public static async Task<JsonElement> CreateAsync(
        HttpClient client, string code, string name, Guid baseUnitId, string type = "stock", string? description = null, bool? isActive = null)
    {
        using var response = await PostAsync(client, code, name, baseUnitId, type, description, isActive);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, object body) =>
        client.PutAsJsonAsync($"{Path}/{id}", body);

    public static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"{Path}/{id}");
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(Path + query);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary>A new active unit of measure; returns its id.</summary>
    public static async Task<Guid> UnitAsync(HttpClient client, string code = "pcs", string name = "Piece", bool? isActive = null) =>
        (await Uom.CreateAsync(client, code, name, isActive)).Id();

    public static Guid BaseUnitId(this JsonElement article) => article.GetProperty("baseUnit").GetProperty("id").GetGuid();
}
