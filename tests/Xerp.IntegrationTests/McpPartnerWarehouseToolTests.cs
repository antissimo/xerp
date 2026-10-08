using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 004, AC-100 to AC-121: the partner and warehouse tools apply the same rules as HTTP and return what
/// HTTP returns. The MCP client uses its own <c>agent</c> key, so attribution to the MCP key is distinguishable
/// from the tenant's first key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpPartnerWarehouseToolTests(XerpFixture app)
{
    private static readonly MasterApi P = MasterApi.Partners;
    private static readonly MasterApi W = MasterApi.Warehouses;

    private static readonly string[] OptionalText =
        ["taxId", "addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    private sealed record Session(TestTenant Tenant, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the tenant's first key (not the MCP key).</summary>
        public HttpClient Http => Tenant.Client;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var tenant = await app.NewTenantAsync();
        var agent = await Keys.CreateAsync(app, tenant.Client, "claude-sales", "agent");
        return new Session(tenant, agent, await app.McpAsync(agent.Key));
    }

    private static async Task AssertHttpNotFoundAsync(HttpClient client, MasterApi api, Guid id)
    {
        using var response = await client.GetAsync($"{api.Path}/{id}");
        await HttpAssert.NotFoundAsync(response);
    }

    // ---- partner tools ----

    [Fact]
    public async Task AC100_Partner_create_is_attributed_to_the_mcp_key_and_equals_http_get()
    {
        await using var s = await AgentAsync();

        var result = await s.Mcp.OkAsync("partner_create", new { code = "P-001", name = "Acme", isCustomer = true });

        Assert.NotEqual(Guid.Empty, result.Id());
        Assert.True(result.Bool("isCustomer"));
        Assert.False(result.Bool("isSupplier"));
        JsonBody.AssertNull(result, "taxId");
        Assert.Equal(s.Agent.Id, result.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await P.GetAsync(s.Http, result.Id()), result);
    }

    [Fact]
    public async Task AC101_Partner_create_with_every_argument_equals_http_get()
    {
        await using var s = await AgentAsync();
        var arguments = P.Full("P-002").With("isActive", false);

        var result = await s.Mcp.OkAsync("partner_create", arguments);

        JsonBody.AssertHasValues(arguments, result);
        McpAssert.JsonEqual(await P.GetAsync(s.Http, result.Id()), result);
    }

    [Fact]
    public async Task AC102_Partner_get_by_id_and_by_code_in_any_case_equals_http_get()
    {
        await using var s = await AgentAsync();
        var partner = await P.CreateAsync(s.Http, P.Full("Cust-7"));

        var byId = await s.Mcp.OkAsync("partner_get", new { id = partner.Id() });
        var byCode = await s.Mcp.OkAsync("partner_get", new { code = "Cust-7" });
        var otherCase = await s.Mcp.OkAsync("partner_get", new { code = "CUST-7" });

        var http = await P.GetAsync(s.Http, partner.Id());
        McpAssert.JsonEqual(http, byId);
        McpAssert.JsonEqual(http, byCode);
        McpAssert.JsonEqual(http, otherCase);
    }

    [Fact]
    public async Task AC103_Partner_list_equals_http_with_the_same_filters()
    {
        await using var s = await AgentAsync();
        // Three partners match all four filters; the others each fail exactly one of them.
        foreach (var code in new[] { "K-3", "K-1", "K-2" })
            await P.CreateAsync(s.Http, P.Minimal(code, $"Kappa {code}"));
        await P.CreateAsync(s.Http, P.Minimal("K-4", "Kappa both").With("isSupplier", true));
        await P.CreateAsync(s.Http, P.Minimal("K-5", "Kappa retired").With("isActive", false));
        await P.CreateAsync(s.Http, P.Minimal("L-1", "Lambda"));
        await P.CreateAsync(s.Http, new JsonObject { ["code"] = "K-6", ["name"] = "Kappa supplier", ["isSupplier"] = true });

        var all = await s.Mcp.OkAsync("partner_list", new { });
        var filtered = await s.Mcp.OkAsync("partner_list",
            new { search = "kappa", isCustomer = true, isSupplier = false, isActive = true, limit = 1, offset = 1 });

        McpAssert.JsonEqual(await P.ListAsync(s.Http), all);
        Assert.Equal(7, all.Total());
        McpAssert.JsonEqual(
            await P.ListAsync(s.Http, "?search=kappa&isCustomer=true&isSupplier=false&isActive=true&limit=1&offset=1"), filtered);
        Assert.Equal(["K-2"], filtered.Codes());
        Assert.Equal(3, filtered.Total());
    }

    [Fact]
    public async Task AC104_Partner_update_with_nulls_clears_optional_text_and_is_attributed_to_the_mcp_key()
    {
        await using var s = await AgentAsync();
        var partner = await P.CreateAsync(s.Http, P.Full("P1"));
        var arguments = new JsonObject
        {
            ["id"] = partner.Id().ToString(), ["code"] = "P1-NEW", ["name"] = "Renamed", ["isCustomer"] = false,
            ["isSupplier"] = true, ["isActive"] = false,
        };
        foreach (var field in OptionalText)
            arguments.With(field, null);
        Assert.Equal(13, arguments.Count);

        var result = await s.Mcp.OkAsync("partner_update", arguments);

        JsonBody.AssertNull(result, OptionalText);
        Assert.Equal("P1-NEW", result.Str("code"));
        Assert.Equal("Renamed", result.Str("name"));
        Assert.False(result.Bool("isCustomer"));
        Assert.True(result.Bool("isSupplier"));
        Assert.False(result.Bool("isActive"));
        Assert.Equal(s.Agent.Id, result.GetProperty("updatedBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, result.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await P.GetAsync(s.Http, partner.Id()), result);
    }

    [Fact]
    public async Task AC105_Partner_delete_returns_deleted_true_and_removes_the_partner()
    {
        await using var s = await AgentAsync();
        var partner = await P.CreateAsync(s.Http, "P1");

        var result = await s.Mcp.OkAsync("partner_delete", new { id = partner.Id() });

        McpAssert.JsonEqual(JsonSerializer.SerializeToElement(new { deleted = true }), result);
        await AssertHttpNotFoundAsync(s.Http, P, partner.Id());
        Assert.Equal(0, (await P.ListAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC106_Partner_create_applies_the_role_code_country_and_text_rules()
    {
        await using var s = await AgentAsync();

        await s.Mcp.ErrorAsync("partner_create", new { code = "P", name = "x" }, "VALIDATION_FAILED", "isCustomer", "isSupplier");
        await s.Mcp.ErrorAsync("partner_create", new { code = "P", name = "x", isCustomer = false, isSupplier = false },
            "VALIDATION_FAILED", "isCustomer", "isSupplier");
        await s.Mcp.ErrorAsync("partner_create", new { name = "x", isCustomer = true }, "VALIDATION_FAILED", "code");
        await s.Mcp.ErrorAsync("partner_create", new { code = "P", name = "x", isCustomer = true, countryCode = "hr" },
            "VALIDATION_FAILED", "countryCode");
        await s.Mcp.ErrorAsync("partner_create", new { code = "P", name = "x", isCustomer = true, taxId = "a\nb" },
            "VALIDATION_FAILED", "taxId");

        Assert.Equal(0, (await P.ListAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC107_Unknown_argument_or_wrong_json_type_is_a_validation_tool_error_not_a_protocol_error()
    {
        await using var s = await AgentAsync();

        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("partner_create",
            new { code = "P", name = "x", isCustomer = true, tenantId = Guid.NewGuid() }));
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("partner_create",
            new { code = "P", name = "x", isCustomer = true, email = "a@example.com" }));
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("partner_create",
            new { code = "P", name = "x", isCustomer = "true", isSupplier = true }));
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("partner_list", new { isCustomer = "true" }));
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("partner_list", new { foo = 1 }));

        Assert.Equal(0, (await P.ListAsync(s.Http)).Total());
    }

    [Theory]
    [InlineData("taxId")]
    [InlineData("countryCode")]
    public async Task AC108_Partner_update_without_a_nullable_argument_is_rejected_and_changes_nothing(string argument)
    {
        await using var s = await AgentAsync();
        var partner = await P.CreateAsync(s.Http, P.Full("P1"));
        var arguments = P.Full("P2").With("name", "Changed").WithId(partner.Id()).Without(argument);

        await s.Mcp.ErrorAsync("partner_update", arguments, "VALIDATION_FAILED", argument);

        await P.AssertUnchangedAsync(s.Http, partner);
    }

    [Fact]
    public async Task AC109_Partner_get_needs_exactly_one_of_id_and_code()
    {
        await using var s = await AgentAsync();
        var partner = await P.CreateAsync(s.Http, "P1");

        await s.Mcp.ErrorAsync("partner_get", new { }, "VALIDATION_FAILED", "id", "code");
        await s.Mcp.ErrorAsync("partner_get", new { id = partner.Id(), code = "P1" }, "VALIDATION_FAILED", "id", "code");
    }

    [Fact]
    public async Task AC109_Unknown_partners_are_not_found_and_a_taken_code_is_code_taken()
    {
        await using var s = await AgentAsync();
        var id = Guid.NewGuid();
        await P.CreateAsync(s.Http, "P-1", "First");

        await s.Mcp.ErrorAsync("partner_get", new { id }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("partner_update", P.Full("P-9").WithId(id), "NOT_FOUND");
        await s.Mcp.ErrorAsync("partner_delete", new { id }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("partner_get", new { id = "not-a-uuid" }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("partner_get", new { code = "nope" }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("partner_create", new { code = "p-1", name = "Second", isCustomer = true }, "CODE_TAKEN");

        var list = await P.ListAsync(s.Http);
        Assert.Equal(["P-1"], list.Codes());
        Assert.Equal("First", list.GetProperty("items")[0].Str("name"));
    }

    // ---- warehouse tools ----

    [Fact]
    public async Task AC110_Warehouse_create_is_attributed_to_the_mcp_key_and_equals_http_get()
    {
        await using var s = await AgentAsync();
        var arguments = W.Full("WH-2");

        var minimal = await s.Mcp.OkAsync("warehouse_create", new { code = "WH-1", name = "Main" });
        var withAddress = await s.Mcp.OkAsync("warehouse_create", arguments);

        JsonBody.AssertNull(minimal, MasterApi.AddressFields);
        Assert.Equal(s.Agent.Id, minimal.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await W.GetAsync(s.Http, minimal.Id()), minimal);
        JsonBody.AssertHasValues(arguments, withAddress);
        McpAssert.JsonEqual(await W.GetAsync(s.Http, withAddress.Id()), withAddress);
    }

    [Fact]
    public async Task AC111_Warehouse_get_and_list_equal_http()
    {
        await using var s = await AgentAsync();
        foreach (var code in new[] { "S-3", "S-1", "S-2" })
            await W.CreateAsync(s.Http, W.Minimal(code, $"Store {code}"));
        await W.CreateAsync(s.Http, W.Minimal("S-4", "Store closed").With("isActive", false));
        var other = await W.CreateAsync(s.Http, W.Full("Yard"));

        var byId = await s.Mcp.OkAsync("warehouse_get", new { id = other.Id() });
        var byCode = await s.Mcp.OkAsync("warehouse_get", new { code = "YARD" });
        var all = await s.Mcp.OkAsync("warehouse_list", new { });
        var filtered = await s.Mcp.OkAsync("warehouse_list", new { search = "store", isActive = true, limit = 1, offset = 1 });

        var http = await W.GetAsync(s.Http, other.Id());
        McpAssert.JsonEqual(http, byId);
        McpAssert.JsonEqual(http, byCode);
        McpAssert.JsonEqual(await W.ListAsync(s.Http), all);
        Assert.Equal(5, all.Total());
        McpAssert.JsonEqual(await W.ListAsync(s.Http, "?search=store&isActive=true&limit=1&offset=1"), filtered);
        Assert.Equal(["S-2"], filtered.Codes());
        Assert.Equal(3, filtered.Total());
    }

    [Fact]
    public async Task AC112_Warehouse_update_with_nulls_clears_the_address_and_is_attributed_to_the_mcp_key()
    {
        await using var s = await AgentAsync();
        var warehouse = await W.CreateAsync(s.Http, W.Full("W1"));
        var arguments = new JsonObject
        {
            ["id"] = warehouse.Id().ToString(), ["code"] = "W1-NEW", ["name"] = "Renamed", ["isActive"] = false,
        };
        foreach (var field in MasterApi.AddressFields)
            arguments.With(field, null);
        Assert.Equal(10, arguments.Count);

        var result = await s.Mcp.OkAsync("warehouse_update", arguments);

        JsonBody.AssertNull(result, MasterApi.AddressFields);
        Assert.Equal("W1-NEW", result.Str("code"));
        Assert.Equal("Renamed", result.Str("name"));
        Assert.False(result.Bool("isActive"));
        Assert.Equal(s.Agent.Id, result.GetProperty("updatedBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, result.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await W.GetAsync(s.Http, warehouse.Id()), result);
    }

    [Fact]
    public async Task AC112_Warehouse_delete_returns_deleted_true_and_removes_the_warehouse()
    {
        await using var s = await AgentAsync();
        var warehouse = await W.CreateAsync(s.Http, "W1");

        var result = await s.Mcp.OkAsync("warehouse_delete", new { id = warehouse.Id() });

        McpAssert.JsonEqual(JsonSerializer.SerializeToElement(new { deleted = true }), result);
        await AssertHttpNotFoundAsync(s.Http, W, warehouse.Id());
    }

    [Fact]
    public async Task AC113_Warehouse_tools_apply_the_code_country_and_argument_rules()
    {
        await using var s = await AgentAsync();
        var warehouse = await W.CreateAsync(s.Http, W.Full("W1"));

        await s.Mcp.ErrorAsync("warehouse_create", new { name = "Main" }, "VALIDATION_FAILED", "code");
        await s.Mcp.ErrorAsync("warehouse_create", new { code = "W2", name = "Main", countryCode = "HRV" },
            "VALIDATION_FAILED", "countryCode");
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("warehouse_create", new { code = "W2", name = "Main", isCustomer = true }));
        McpAssert.ValidationWithErrors(await s.Mcp.CallAsync("warehouse_create",
            new { code = "W2", name = "Main", tenantId = Guid.NewGuid() }));
        await s.Mcp.ErrorAsync("warehouse_update", W.Full("W9").WithId(warehouse.Id()).Without("city"),
            "VALIDATION_FAILED", "city");

        Assert.Equal(["W1"], (await W.ListAsync(s.Http)).Codes());
        await W.AssertUnchangedAsync(s.Http, warehouse);
    }

    [Fact]
    public async Task AC114_Warehouse_get_needs_exactly_one_of_id_and_code()
    {
        await using var s = await AgentAsync();
        var warehouse = await W.CreateAsync(s.Http, "W1");

        await s.Mcp.ErrorAsync("warehouse_get", new { }, "VALIDATION_FAILED", "id", "code");
        await s.Mcp.ErrorAsync("warehouse_get", new { id = warehouse.Id(), code = "W1" }, "VALIDATION_FAILED", "id", "code");
    }

    [Fact]
    public async Task AC114_Unknown_warehouses_are_not_found_and_a_taken_code_is_code_taken()
    {
        await using var s = await AgentAsync();
        var id = Guid.NewGuid();
        await W.CreateAsync(s.Http, "W-1", "First");

        await s.Mcp.ErrorAsync("warehouse_get", new { id }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("warehouse_update", W.Full("W-9").WithId(id), "NOT_FOUND");
        await s.Mcp.ErrorAsync("warehouse_delete", new { id }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("warehouse_create", new { code = "W-1", name = "Second" }, "CODE_TAKEN");

        var list = await W.ListAsync(s.Http);
        Assert.Equal(["W-1"], list.Codes());
        Assert.Equal("First", list.GetProperty("items")[0].Str("name"));
    }

    // ---- parity and attribution ----

    public static TheoryData<string, string, string, int, string> ParityCases() => new()
    {
        { "partner_create", "partners", """{ "code": "P", "name": "x" }""", 400, "VALIDATION_FAILED" },
        { "partner_create", "partners", """{ "code": "P", "name": "x", "isCustomer": true, "countryCode": "hr", "taxId": "TAXID51" }""", 400, "VALIDATION_FAILED" },
        { "partner_create", "partners", """{ "code": "taken", "name": "x", "isCustomer": true }""", 409, "CODE_TAKEN" },
        { "warehouse_create", "warehouses", "{}", 400, "VALIDATION_FAILED" },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public async Task AC120_A_rejected_input_has_the_same_code_and_error_keys_over_http_and_mcp(
        string tool, string resource, string json, int status, string code)
    {
        await using var s = await AgentAsync();
        await P.CreateAsync(s.Http, "TAKEN");
        json = json.Replace("TAXID51", new string('t', 51));

        var toolError = await s.Mcp.ErrorAsync(tool, JsonDocument.Parse(json).RootElement, code);
        using var response = await s.Http.PostAsync($"/api/v1/{resource}", HttpAssert.Raw(json));

        var problem = await HttpAssert.ProblemAsync(response, (HttpStatusCode)status, code);
        Assert.Equal(problem.Str("code"), toolError.Str("code"));
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(toolError));
        if (status == 400)
            Assert.NotEmpty(McpAssert.ErrorKeys(problem));
    }

    [Fact]
    public async Task AC120_The_two_invalid_fields_are_both_reported_by_the_tool()
    {
        await using var s = await AgentAsync();

        await s.Mcp.ErrorAsync("partner_create",
            new { code = "P", name = "x", isCustomer = true, countryCode = "hr", taxId = new string('t', 51) },
            "VALIDATION_FAILED", "countryCode", "taxId");
    }

    [Fact]
    public async Task AC121_Http_create_and_tool_update_are_attributed_to_their_own_keys()
    {
        await using var s = await AgentAsync();
        var partner = await P.CreateAsync(s.Http, "P1");
        var warehouse = await W.CreateAsync(s.Http, "W1");

        var updatedPartner = await s.Mcp.OkAsync("partner_update", P.Full("P1").WithId(partner.Id()));
        var updatedWarehouse = await s.Mcp.OkAsync("warehouse_update", W.Full("W1").WithId(warehouse.Id()));

        foreach (var (api, updated) in new[] { (P, updatedPartner), (W, updatedWarehouse) })
        {
            var stored = await api.GetAsync(s.Http, updated.Id());
            Assert.Equal(s.Tenant.ApiKeyId, stored.GetProperty("createdBy").GetGuid());
            Assert.Equal(s.Agent.Id, stored.GetProperty("updatedBy").GetGuid());
            McpAssert.JsonEqual(stored, updated);
        }
    }
}
