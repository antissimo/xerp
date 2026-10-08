using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Xerp.IntegrationTests.Support;

public static class HttpAssert
{
    /// <summary>
    /// "Problem with code X" (spec 001, section 10): Content-Type application/problem+json, body with
    /// code X and a status equal to the HTTP status; plus title and detail (AC-70). Never asserts on text.
    /// </summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status} {code}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var body = document.RootElement.Clone();
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.String, body.GetProperty("title").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.Equal(JsonValueKind.String, body.GetProperty("detail").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        AssertLeaksNothing(text);
        return body;
    }

    /// <summary>400 VALIDATION_FAILED whose <c>errors</c> object has (at least) the given keys, each non-empty.</summary>
    public static async Task<JsonElement> ValidationAsync(HttpResponseMessage response, params string[] errorKeys)
    {
        var body = await ProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(body.TryGetProperty("errors", out var errors), "A validation problem must have 'errors'.");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.NotEmpty(errors.EnumerateObject());
        foreach (var key in errorKeys)
        {
            Assert.True(errors.TryGetProperty(key, out var messages), $"errors has no key '{key}': {errors}");
            Assert.Equal(JsonValueKind.Array, messages.ValueKind);
            Assert.NotEqual(0, messages.GetArrayLength());
        }
        return body;
    }

    /// <summary>401 UNAUTHENTICATED with a Bearer challenge (spec 001, S2).</summary>
    public static async Task UnauthenticatedAsync(HttpResponseMessage response)
    {
        await ProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out var values), "WWW-Authenticate header is missing.");
        Assert.StartsWith("Bearer", string.Join(", ", values!), StringComparison.Ordinal);
    }

    public static Task ForbiddenAsync(HttpResponseMessage response) =>
        ProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");

    public static Task NotFoundAsync(HttpResponseMessage response) =>
        ProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");

    public static Task CodeTakenAsync(HttpResponseMessage response) =>
        ProblemAsync(response, HttpStatusCode.Conflict, "CODE_TAKEN");

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>S6: no stack traces, SQL or connection details in an error body.</summary>
    private static void AssertLeaksNothing(string body)
    {
        foreach (var marker in new[] { "   at ", "Exception", "Npgsql", "SELECT ", "INSERT ", "Host=", "Password", "duplicate key" })
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
    }

    public static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
}

/// <summary>Shortcuts for the units-of-measure endpoints.</summary>
public static class Uom
{
    public const string Path = "/api/v1/units-of-measure";

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string code, string name) =>
        client.PostAsJsonAsync(Path, new { code, name });

    public static async Task<JsonElement> CreateAsync(HttpClient client, string code, string name, bool? isActive = null)
    {
        object body = isActive is null ? new { code, name } : new { code, name, isActive };
        using var response = await client.PostAsJsonAsync(Path, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

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

    public static Guid Id(this JsonElement unit) => unit.GetProperty("id").GetGuid();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static string[] Codes(this JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToArray();

    public static int Total(this JsonElement list) => list.GetProperty("total").GetInt32();
}
