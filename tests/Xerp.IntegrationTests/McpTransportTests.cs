using System.Net;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 003, AC-30 to AC-36: the /mcp endpoint, its authentication and its Origin check.</summary>
[Collection(XerpCollection.Name)]
public class McpTransportTests(XerpFixture app)
{
    private const string AllowedOrigin = "https://app.example";

    [Fact]
    public async Task AC30_Client_with_a_tenant_key_initialises_a_tools_only_server()
    {
        var tenant = await app.NewTenantAsync();

        await using var mcp = await app.McpAsync(tenant.Key);

        Assert.True(string.CompareOrdinal(mcp.Client.NegotiatedProtocolVersion, "2025-06-18") >= 0,
            $"Negotiated protocol revision '{mcp.Client.NegotiatedProtocolVersion}' is older than 2025-06-18.");
        Assert.Equal("xerp", mcp.Client.ServerInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(mcp.Client.ServerInfo.Version));
        Assert.NotNull(mcp.Client.ServerCapabilities.Tools);
        Assert.Null(mcp.Client.ServerCapabilities.Resources);
        Assert.Null(mcp.Client.ServerCapabilities.Prompts);
        Assert.False(string.IsNullOrWhiteSpace(mcp.Client.ServerInstructions));
    }

    public static TheoryData<string, string> McpMethodsAndBadCredentials()
    {
        var data = new TheoryData<string, string>();
        foreach (var body in new[] { Mcp.Initialize, Mcp.ToolsList })
            foreach (var credential in new[] { "none", "wrong", "basic", "revoked" })
                data.Add(body, credential);
        return data;
    }

    [Theory]
    [MemberData(nameof(McpMethodsAndBadCredentials))]
    public async Task AC31_Mcp_without_a_valid_tenant_key_is_unauthenticated(string body, string credential)
    {
        var tenant = await app.NewTenantAsync();
        var authorization = credential switch
        {
            "none" => null,
            "wrong" => "Bearer wrong",
            "basic" => $"Basic {tenant.Key}",
            _ => $"Bearer {await RevokedKeyAsync(tenant)}",
        };
        using var client = app.Anonymous();

        using var response = await client.SendAsync(Mcp.Post(body, authorization));

        await HttpAssert.UnauthenticatedAsync(response);
    }

    [Theory]
    [InlineData(Mcp.Initialize)]
    [InlineData(Mcp.ToolsList)]
    public async Task AC32_Mcp_with_the_admin_key_is_forbidden(string body)
    {
        using var client = app.Anonymous();

        using var response = await client.SendAsync(Mcp.Post(body, $"Bearer {XerpFixture.AdminKey}"));

        await HttpAssert.ForbiddenAsync(response);
    }

    [Fact]
    public async Task AC33_Origin_not_on_the_allow_list_is_forbidden()
    {
        var tenant = await app.NewTenantAsync();
        using var client = app.Anonymous();

        using var response = await client.SendAsync(Mcp.Post(Mcp.Initialize, $"Bearer {tenant.Key}", "https://evil.example"));

        await HttpAssert.ForbiddenAsync(response);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AC33_Origin_on_the_allow_list_is_processed_without_cors_headers()
    {
        var tenant = await app.NewTenantAsync();
        var host = app.CreateHost(XerpFixture.AdminKey, ("Xerp:Mcp:AllowedOrigins:0", AllowedOrigin));
        using var client = host.CreateClient();

        using var allowed = await client.SendAsync(Mcp.Post(Mcp.Initialize, $"Bearer {tenant.Key}", AllowedOrigin));
        using var other = await client.SendAsync(Mcp.Post(Mcp.Initialize, $"Bearer {tenant.Key}", "https://evil.example"));
        using var none = await client.SendAsync(Mcp.Post(Mcp.Initialize, $"Bearer {tenant.Key}"));

        Assert.True(allowed.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for an allowed Origin, got {(int)allowed.StatusCode}: {await allowed.Content.ReadAsStringAsync()}");
        Assert.Contains("\"serverInfo\"", await allowed.Content.ReadAsStringAsync());
        await HttpAssert.ForbiddenAsync(other);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        foreach (var response in new[] { allowed, other, none })
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AC34_Get_and_delete_are_not_allowed()
    {
        var tenant = await app.NewTenantAsync();

        using var get = await tenant.Client.GetAsync(Mcp.Path);
        using var delete = await tenant.Client.DeleteAsync(Mcp.Path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task AC34_Initialize_issues_no_session()
    {
        var tenant = await app.NewTenantAsync();
        using var client = app.Anonymous();

        using var response = await client.SendAsync(Mcp.Post(Mcp.Initialize, $"Bearer {tenant.Key}"));
        await using var mcp = await app.McpAsync(tenant.Key);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for initialize, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.False(response.Headers.Contains("MCP-Session-Id"));
        Assert.True(string.IsNullOrEmpty(mcp.Client.SessionId), "The MCP client was given a session id.");
    }

    [Fact]
    public async Task AC35_Connected_client_whose_key_is_revoked_gets_http_401_on_the_next_call()
    {
        var tenant = await app.NewTenantAsync();
        var b = await Keys.CreateAsync(app, tenant.Client, "b", "agent");
        await using var mcp = await app.McpAsync(b.Key);
        await mcp.OkAsync("whoami");

        using (var revoke = await Keys.RevokeAsync(tenant.Client, b.Id))
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        CallToolResult? result = null;
        var failure = await Record.ExceptionAsync(async () => result = await mcp.CallAsync("whoami"));

        Assert.True(failure is not null,
            $"The call after revocation must fail as an HTTP error, got a tool result: {(result is null ? "" : McpAssert.Describe(result))}");
        for (var e = failure; e is not null; e = e.InnerException)
            if (e is HttpRequestException { StatusCode: not null } http)
                Assert.Equal(HttpStatusCode.Unauthorized, http.StatusCode);
        using var client = app.Anonymous();
        using var raw = await client.SendAsync(Mcp.Post(Mcp.CallWhoAmI, $"Bearer {b.Key}"));
        await HttpAssert.UnauthenticatedAsync(raw);
    }

    [Fact]
    public async Task AC36_Body_that_is_not_json_never_gives_500()
    {
        var tenant = await app.NewTenantAsync();
        using var client = app.Anonymous();

        using var response = await client.SendAsync(Mcp.Post("not json", $"Bearer {tenant.Key}"));

        Assert.True((int)response.StatusCode < 500, $"Expected a status below 500, got {(int)response.StatusCode}.");
        // The endpoint exists: 404 here would only mean that /mcp is not mapped.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> RevokedKeyAsync(TestTenant tenant)
    {
        var key = await Keys.CreateAsync(app, tenant.Client, "revoked", "agent");
        using var revoke = await Keys.RevokeAsync(tenant.Client, key.Id);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        return key.Key;
    }
}
