using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// "MCP client" of spec 003, section 10: the official C# SDK's client connected to <c>/mcp</c> of the in-process
/// test server with a given API key.
/// </summary>
public sealed class McpConnection(McpClient client, HttpClient http) : IAsyncDisposable
{
    public McpClient Client => client;

    /// <summary>Calls a tool; <paramref name="arguments"/> is an anonymous object whose properties become the arguments.</summary>
    public async Task<CallToolResult> CallAsync(string tool, object? arguments = null)
    {
        var args = new Dictionary<string, JsonElement>();
        if (arguments is not null)
            foreach (var property in JsonSerializer.SerializeToElement(arguments).EnumerateObject())
                args[property.Name] = property.Value.Clone();
        return await client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = args });
    }

    /// <summary>Calls a tool and asserts "tool success"; returns <c>structuredContent</c>.</summary>
    public async Task<JsonElement> OkAsync(string tool, object? arguments = null) =>
        McpAssert.Success(await CallAsync(tool, arguments));

    /// <summary>Calls a tool and asserts "tool error <paramref name="code"/>"; returns the error object.</summary>
    public async Task<JsonElement> ErrorAsync(string tool, object? arguments, string code, params string[] errorKeys) =>
        McpAssert.Error(await CallAsync(tool, arguments), code, errorKeys);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception)
        {
            // A connection whose key was revoked (AC-35) may fail while closing; nothing to assert here.
        }
        http.Dispose();
    }
}

public static class Mcp
{
    public const string Path = "/mcp";

    public const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"xerp-tests","version":"1.0"}}}""";

    public const string ToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";

    public const string CallWhoAmI =
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"whoami","arguments":{}}}""";

    public static async Task<McpConnection> McpAsync(this XerpFixture app, string key, XerpFactory? host = null)
    {
        var http = XerpFixture.Bearer((host ?? app.Factory).CreateClient(), key);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, Path),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http, null, false);
        var client = await McpClient.CreateAsync(transport);
        return new McpConnection(client, http);
    }

    /// <summary>A raw <c>POST /mcp</c> as an MCP client would send it, without any credential.</summary>
    public static HttpRequestMessage Post(string body, string? authorization = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }
}

public static class McpAssert
{
    /// <summary>
    /// "Tool success" (spec 003, section 10): isError not true, a structuredContent object, exactly one text
    /// block whose text parses to JSON equal to structuredContent.
    /// </summary>
    public static JsonElement Success(CallToolResult result)
    {
        Assert.True(result.IsError != true, $"Expected tool success, got a tool error: {Describe(result)}");
        Assert.True(result.StructuredContent is { ValueKind: JsonValueKind.Object },
            $"Tool success must have a structuredContent object: {Describe(result)}");
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var text = JsonDocument.Parse(block.Text);
        JsonEqual(result.StructuredContent!.Value, text.RootElement, "structuredContent and the text block differ");
        return result.StructuredContent.Value.Clone();
    }

    /// <summary>
    /// "Tool error X" (spec 003, section 10): isError true, exactly one text block holding a JSON object with
    /// code X and a non-empty detail, no structuredContent; <c>errors</c> has (at least) the given keys.
    /// AC-98 / S6: the code is never INTERNAL_ERROR unless asked for, and the text leaks nothing.
    /// </summary>
    public static JsonElement Error(CallToolResult result, string code, params string[] errorKeys)
    {
        Assert.True(result.IsError == true, $"Expected tool error {code}, got: {Describe(result)}");
        Assert.True(result.StructuredContent is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined },
            $"A tool error must not have structuredContent: {Describe(result)}");
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var document = JsonDocument.Parse(block.Text);
        var error = document.RootElement.Clone();
        Assert.Equal(JsonValueKind.Object, error.ValueKind);
        Assert.True(error.TryGetProperty("code", out var actual) && actual.GetString() == code,
            $"Expected tool error {code}, got: {block.Text}");
        Assert.True(error.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(detail.GetString()), $"A tool error must have a non-empty detail: {block.Text}");
        HttpAssert.AssertLeaksNothing(block.Text);
        if (errorKeys.Length > 0)
        {
            Assert.True(error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object,
                $"The tool error has no 'errors' object: {block.Text}");
            foreach (var key in errorKeys)
            {
                Assert.True(errors.TryGetProperty(key, out var messages), $"errors has no key '{key}': {block.Text}");
                Assert.Equal(JsonValueKind.Array, messages.ValueKind);
                Assert.NotEqual(0, messages.GetArrayLength());
            }
        }
        return error;
    }

    /// <summary>VALIDATION_FAILED whose <c>errors</c> object is present and non-empty (R14).</summary>
    public static JsonElement ValidationWithErrors(CallToolResult result)
    {
        var error = Error(result, "VALIDATION_FAILED");
        Assert.True(error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            && errors.EnumerateObject().Any(), $"VALIDATION_FAILED must have non-empty 'errors': {error}");
        return error;
    }

    /// <summary>"Equal to HTTP" (spec 003, section 10): same properties, same values.</summary>
    public static void JsonEqual(JsonElement expected, JsonElement actual, string what = "JSON values differ")
    {
        Assert.True(JsonElement.DeepEquals(expected, actual), $"{what}:\n{expected}\n!=\n{actual}");
    }

    /// <summary>The keys of <c>errors</c> of a problem document or a tool error, sorted; empty when absent.</summary>
    public static string[] ErrorKeys(JsonElement error) =>
        error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            ? errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()
            : [];

    public static string Describe(CallToolResult result) =>
        $"isError={result.IsError}, structuredContent={result.StructuredContent?.ToString() ?? "(none)"}, content=[" +
        string.Join(" | ", result.Content.Select(c => c is TextContentBlock t ? t.Text : c.Type)) + "]";
}

public sealed record TestKey(Guid Id, string Key, JsonElement Body, HttpClient Client);

/// <summary>Shortcuts for the API key endpoints (spec 003, section 4).</summary>
public static class Keys
{
    public const string Path = "/api/v1/api-keys";

    public static async Task<TestKey> CreateAsync(XerpFixture app, HttpClient client, string name = "bot", string actorType = "agent")
    {
        using var response = await client.PostAsJsonAsync(Path, new { name, actorType });
        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        var key = body.Str("key");
        return new TestKey(body.Id(), key, body, app.WithBearer(key));
    }

    public static Task<HttpResponseMessage> RevokeAsync(HttpClient client, Guid id) =>
        client.PostAsync($"{Path}/{id}/revoke", null);

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

    public static string[] Names(this JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();

    public static Guid[] Ids(this JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
}

/// <summary>Shortcuts for the article endpoints, written against the contract of spec 002, section 4.</summary>
public static class Art
{
    public const string Path = "/api/v1/articles";

    public static async Task<JsonElement> CreateAsync(HttpClient client, string code, string name, Guid baseUnitId, string type = "stock")
    {
        using var response = await client.PostAsJsonAsync(Path, new { code, name, type, baseUnitId });
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
}
