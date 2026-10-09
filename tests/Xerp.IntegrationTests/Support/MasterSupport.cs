using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// "A master with code, name and address" (spec 004, section 11): the HTTP routes and tool names of one
/// resource, and request bodies as mutable JSON objects so that a test can omit or replace a single field.
/// </summary>
public sealed class MasterApi(string resource, string toolPrefix, Func<string, string, JsonObject> minimal, Func<string, JsonObject> full)
{
    public static readonly string[] AddressFields =
        ["addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    /// <summary>Spec 004, 4.1. "Partner P": code, name, <c>isCustomer: true</c>.</summary>
    public static readonly MasterApi Partners = new("partners", "partner",
        (code, name) => new JsonObject { ["code"] = code, ["name"] = name, ["isCustomer"] = true },
        code => new JsonObject
        {
            ["code"] = code, ["name"] = "Acme d.o.o.", ["isCustomer"] = true, ["isSupplier"] = true,
            ["taxId"] = "HR12345678901", ["addressLine1"] = "Ilica 1", ["addressLine2"] = "2nd floor",
            ["postalCode"] = "10000", ["city"] = "Zagreb", ["region"] = "Grad Zagreb", ["countryCode"] = "HR",
            ["isActive"] = true,
        });

    /// <summary>Spec 004, 4.2.</summary>
    public static readonly MasterApi Warehouses = new("warehouses", "warehouse",
        (code, name) => new JsonObject { ["code"] = code, ["name"] = name },
        code => new JsonObject
        {
            ["code"] = code, ["name"] = "Main warehouse", ["addressLine1"] = "Slavonska avenija 6",
            ["addressLine2"] = "Hall B", ["postalCode"] = "10000", ["city"] = "Zagreb",
            ["region"] = "Grad Zagreb", ["countryCode"] = "HR", ["isActive"] = true,
        });

    public string Path { get; } = $"/api/v1/{resource}";

    /// <summary>Tool name for an operation: <c>list</c>, <c>get</c>, <c>create</c>, <c>update</c>, <c>delete</c>.</summary>
    public string Tool(string operation) => $"{toolPrefix}_{operation}";

    /// <summary>The smallest valid create body.</summary>
    public JsonObject Minimal(string code, string name = "Name") => minimal(code, name);

    /// <summary>A valid body with every field of the replace operation set to a non-null value.</summary>
    public JsonObject Full(string code) => full(code);

    /// <summary>The fields of the replace operation (twelve for a partner, nine for a warehouse).</summary>
    public string[] ReplaceFields => Full("x").Select(p => p.Key).ToArray();

    public Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Path, body);

    public Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, JsonObject body) =>
        client.PutAsJsonAsync($"{Path}/{id}", body);

    public Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Path}/{id}");

    public Task<HttpResponseMessage> ByCodeAsync(HttpClient client, string code) =>
        client.GetAsync($"{Path}/by-code/{Uri.EscapeDataString(code)}");

    public async Task<JsonElement> CreateAsync(HttpClient client, JsonObject body)
    {
        using var response = await PostAsync(client, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public Task<JsonElement> CreateAsync(HttpClient client, string code, string name = "Name") =>
        CreateAsync(client, Minimal(code, name));

    public async Task<JsonElement> ReplaceAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"{Path}/{id}");
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(Path + query);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary>Asserts that a rejected write left the record exactly as it was.</summary>
    public async Task AssertUnchangedAsync(HttpClient client, JsonElement before) =>
        McpAssert.JsonEqual(before, await GetAsync(client, before.Id()), $"The record of {resource} changed");

    public override string ToString() => resource;
}

public static class JsonBody
{
    public static JsonObject With(this JsonObject body, string property, JsonNode? value)
    {
        body[property] = value;
        return body;
    }

    public static JsonObject Without(this JsonObject body, string property)
    {
        body.Remove(property);
        return body;
    }

    /// <summary>The body as tool arguments, with the addressed record's <c>id</c> added.</summary>
    public static JsonObject WithId(this JsonObject body, Guid id) => body.With("id", id.ToString());

    /// <summary>Asserts that every property of <paramref name="expected"/> has the same value in <paramref name="actual"/>.</summary>
    public static void AssertHasValues(JsonObject expected, JsonElement actual)
    {
        foreach (var (name, value) in expected)
        {
            Assert.True(actual.TryGetProperty(name, out var found), $"Property '{name}' is missing: {actual}");
            McpAssert.JsonEqual(JsonSerializer.SerializeToElement(value), found, $"Property '{name}' differs");
        }
    }

    /// <summary>Asserts that each property is present with the JSON value <c>null</c> (spec 004, R3).</summary>
    public static void AssertNull(JsonElement record, params string[] properties)
    {
        foreach (var name in properties)
        {
            Assert.True(record.TryGetProperty(name, out var value), $"Property '{name}' is missing: {record}");
            Assert.True(value.ValueKind == JsonValueKind.Null, $"Property '{name}' should be null, is {value}");
        }
    }

    public static bool Bool(this JsonElement element, string property) => element.GetProperty(property).GetBoolean();

    public static string[] PropertyNames(this JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>Text with a control character, named so that test case names stay printable.</summary>
public static class ControlText
{
    public static string Of(string character) => character switch
    {
        "nul" => "a\u0000b",
        "lf" => "a\nb",
        "tab" => "a\tb",
        _ => throw new ArgumentOutOfRangeException(nameof(character), character, null),
    };
}
