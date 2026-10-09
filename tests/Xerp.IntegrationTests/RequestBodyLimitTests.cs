using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Review 001, item 7 (docs/reviews/open-items.md, "before MVP"): a request body under <c>/api/v1</c> and
/// <c>/mcp</c> is at most 1 MB; a larger one is refused with <c>413</c> in the error model, whether its
/// length is declared (<c>Content-Length</c>) or not (chunked). Most cases run against a host on a real
/// Kestrel listener: what is at stake is that a client still sending its body gets the answer.
/// </summary>
[Collection(XerpCollection.Name)]
public class RequestBodyLimitTests(XerpFixture app)
{
    private const int Limit = 1024 * 1024;
    private const string TooLarge = "PAYLOAD_TOO_LARGE";

    [Fact]
    public async Task Api_body_above_the_limit_is_413_and_a_body_of_exactly_the_limit_is_accepted()
    {
        var tenant = await app.NewTenantAsync();
        using var client = Listening(tenant.Key);
        var code = XerpFixture.UniqueCode("u")[..10];

        using var declared = await client.PostAsync(Uom.Path, Declared(UnitBody(code, Limit + 1)));
        using var chunked = await client.PostAsync(Uom.Path, Chunked(UnitBody(code, Limit + 1)));
        using var huge = await client.PostAsync(Uom.Path, Chunked(UnitBody(code, 4 * Limit)));
        using var replace = await client.PutAsync($"{Uom.Path}/{Guid.NewGuid()}", Declared(UnitBody(code, Limit + 1)));

        foreach (var response in new[] { declared, chunked, huge, replace })
        {
            var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, TooLarge);
            Assert.Equal(TooLarge, problem.GetProperty("title").GetString());
            Assert.Equal("about:blank", problem.GetProperty("type").GetString());
        }

        // Nothing of the refused requests was applied, and the limit itself is still a legitimate size.
        using var missing = await client.GetAsync($"{Uom.Path}/by-code/{code}");
        await HttpAssert.NotFoundAsync(missing);
        using var atLimit = await client.PostAsync(Uom.Path, Declared(UnitBody(code, Limit)));
        await HttpAssert.JsonAsync(atLimit, HttpStatusCode.Created);
        using var atLimitChunked = await client.PostAsync(Uom.Path, Chunked(UnitBody(code + "c", Limit)));
        await HttpAssert.JsonAsync(atLimitChunked, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Mcp_body_above_the_limit_is_413_and_a_body_of_exactly_the_limit_is_processed()
    {
        var tenant = await app.NewTenantAsync();
        using var client = Listening(tenant.Key);

        using var declared = await client.SendAsync(McpPost(Declared(Padded(Mcp.CallWhoAmI, Limit + 1))));
        using var chunked = await client.SendAsync(McpPost(Chunked(Padded(Mcp.CallWhoAmI, Limit + 1))));

        // As for 401 and 403 on /mcp (ADR-0009): an HTTP problem document, not a JSON-RPC or tool error.
        await HttpAssert.ProblemAsync(declared, HttpStatusCode.RequestEntityTooLarge, TooLarge);
        await HttpAssert.ProblemAsync(chunked, HttpStatusCode.RequestEntityTooLarge, TooLarge);

        using var atLimit = await client.SendAsync(McpPost(Declared(Padded(Mcp.CallWhoAmI, Limit))));
        var text = await atLimit.Content.ReadAsStringAsync();
        Assert.True(atLimit.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)atLimit.StatusCode}: {text}");
        Assert.Contains(tenant.Code, text);
    }

    [Fact]
    public async Task The_credential_is_judged_before_the_size_of_the_body()
    {
        var tenant = await app.NewTenantAsync();
        using var anonymous = Listening(null);
        using var admin = Listening(XerpFixture.AdminKey);
        using var client = Listening(tenant.Key);
        var body = UnitBody("x", Limit + 1);

        using var noKey = await anonymous.PostAsync(Uom.Path, Declared(body));
        using var noKeyMcp = await anonymous.SendAsync(McpPost(Chunked(body)));
        using var adminOnTenantRoute = await admin.PostAsync(Uom.Path, Declared(body));
        using var tenantOnAdminRoute = await client.PostAsync("/api/v1/admin/tenants", Declared(body));
        using var adminRoute = await admin.PostAsync("/api/v1/admin/tenants", Declared(body));

        await HttpAssert.UnauthenticatedAsync(noKey);
        await HttpAssert.UnauthenticatedAsync(noKeyMcp);
        await HttpAssert.ForbiddenAsync(adminOnTenantRoute);
        await HttpAssert.ForbiddenAsync(tenantOnAdminRoute);
        await HttpAssert.ProblemAsync(adminRoute, HttpStatusCode.RequestEntityTooLarge, TooLarge);
    }

    [Fact]
    public async Task The_in_process_host_refuses_a_declared_length_above_the_limit_too()
    {
        var tenant = await app.NewTenantAsync();

        using var tooLarge = await tenant.Client.PostAsync(Uom.Path, Declared(UnitBody("x", Limit + 1)));
        using var mcpTooLarge = await tenant.Client.SendAsync(McpPost(Declared(Padded(Mcp.CallWhoAmI, Limit + 1))));
        using var health = await tenant.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health") { Content = Declared(UnitBody("x", Limit + 1)) });

        await HttpAssert.ProblemAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge, TooLarge);
        await HttpAssert.ProblemAsync(mcpTooLarge, HttpStatusCode.RequestEntityTooLarge, TooLarge);
        // The limit belongs to /api/v1 and /mcp only.
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    /// <summary>A client of a second host that listens on a real Kestrel port.</summary>
    private HttpClient Listening(string? key)
    {
        var host = app.CreateHost(XerpFixture.AdminKey);
        host.UseKestrel(0);
        host.StartServer();
        var client = host.CreateClient();
        return key is null ? client : XerpFixture.Bearer(client, key);
    }

    /// <summary>A valid unit-of-measure body of exactly <paramref name="bytes"/> bytes (JSON allows the padding).</summary>
    private static byte[] UnitBody(string code, int bytes) =>
        Padded($$"""{"code":"{{code}}","name":"Unit {{code}}"}""", bytes);

    private static byte[] Padded(string json, int bytes)
    {
        var body = Encoding.UTF8.GetBytes(json.PadRight(bytes, ' '));
        Assert.Equal(bytes, body.Length);
        return body;
    }

    private static HttpRequestMessage McpPost(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Mcp.Path) { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        return request;
    }

    private static ByteArrayContent Declared(byte[] body) => Json(new ByteArrayContent(body));

    private static HttpContent Chunked(byte[] body) => Json(new UnknownLengthContent(body));

    private static T Json<T>(T content) where T : HttpContent
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>Content without a length, so the client sends it with <c>Transfer-Encoding: chunked</c>.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
