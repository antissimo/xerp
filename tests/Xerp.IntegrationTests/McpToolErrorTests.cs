using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 003, AC-70 to AC-80: application errors are tool errors with the codes and <c>errors</c> keys of HTTP.
/// AC-98: <see cref="McpAssert.Error"/> asserts the exact code, so no asserted result is INTERNAL_ERROR.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpToolErrorTests(XerpFixture app)
{
    private async Task<(TestTenant Tenant, McpConnection Mcp)> ConnectAsync()
    {
        var tenant = await app.NewTenantAsync();
        return (tenant, await app.McpAsync(tenant.Key));
    }

    [Fact]
    public async Task AC70_Uom_create_with_missing_or_invalid_fields_is_a_validation_tool_error()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;

        await mcp.ErrorAsync("uom_create", new { name = "x" }, "VALIDATION_FAILED", "code");
        await mcp.ErrorAsync("uom_create", new { code = "a b", name = "" }, "VALIDATION_FAILED", "code", "name");

        Assert.Equal(0, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC71_Unknown_argument_or_wrong_json_type_is_a_validation_tool_error_not_a_protocol_error()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;

        McpAssert.ValidationWithErrors(await mcp.CallAsync("uom_create", new { code = "kg", name = "Kilogram", tenantId = Guid.NewGuid() }));
        McpAssert.ValidationWithErrors(await mcp.CallAsync("uom_create", new { code = "kg", name = "Kilogram", foo = 1 }));
        McpAssert.ValidationWithErrors(await mcp.CallAsync("uom_create", new { code = "kg", name = 123 }));
        McpAssert.ValidationWithErrors(await mcp.CallAsync("uom_list", new { limit = "10" }));
        await mcp.ErrorAsync("whoami", new { x = 1 }, "VALIDATION_FAILED");

        Assert.Equal(0, (await Uom.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC72_Uom_list_with_out_of_range_paging_is_a_validation_tool_error()
    {
        var (_, mcp) = await ConnectAsync();
        await using var _1 = mcp;

        await mcp.ErrorAsync("uom_list", new { limit = 0 }, "VALIDATION_FAILED", "limit");
        await mcp.ErrorAsync("uom_list", new { limit = 501 }, "VALIDATION_FAILED", "limit");
        await mcp.ErrorAsync("uom_list", new { offset = -1 }, "VALIDATION_FAILED", "offset");
    }

    [Theory]
    [InlineData("uom_get")]
    [InlineData("article_get")]
    public async Task AC73_Get_needs_exactly_one_of_id_and_code(string tool)
    {
        var (_, mcp) = await ConnectAsync();
        await using var _1 = mcp;

        await mcp.ErrorAsync(tool, new { }, "VALIDATION_FAILED", "id", "code");
        await mcp.ErrorAsync(tool, new { id = Guid.NewGuid(), code = "kg" }, "VALIDATION_FAILED", "id", "code");
    }

    [Fact]
    public async Task AC74_Unknown_records_are_not_found()
    {
        var (_, mcp) = await ConnectAsync();
        await using var _1 = mcp;
        var id = Guid.NewGuid();

        await mcp.ErrorAsync("uom_get", new { id }, "NOT_FOUND");
        await mcp.ErrorAsync("uom_update", new { id, code = "kg", name = "Kilogram", isActive = true }, "NOT_FOUND");
        await mcp.ErrorAsync("uom_delete", new { id }, "NOT_FOUND");
        await mcp.ErrorAsync("api_key_get", new { id }, "NOT_FOUND");
        await mcp.ErrorAsync("api_key_revoke", new { id }, "NOT_FOUND");
        await mcp.ErrorAsync("uom_get", new { id = "not-a-uuid" }, "NOT_FOUND");
        await mcp.ErrorAsync("uom_get", new { code = "nope" }, "NOT_FOUND");
    }

    [Fact]
    public async Task AC74_Unknown_articles_are_not_found()
    {
        var (_, mcp) = await ConnectAsync();
        await using var _1 = mcp;
        var id = Guid.NewGuid();

        await mcp.ErrorAsync("article_get", new { id }, "NOT_FOUND");
        await mcp.ErrorAsync("article_delete", new { id }, "NOT_FOUND");
    }

    [Fact]
    public async Task AC75_Uom_create_of_an_existing_code_in_another_case_is_code_taken()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        await mcp.OkAsync("uom_create", new { code = "kg", name = "Kilogram" });

        await mcp.ErrorAsync("uom_create", new { code = "KG", name = "Kilo" }, "CODE_TAKEN");

        Assert.Equal(["kg"], (await Uom.ListAsync(tenant.Client)).Codes());
    }

    [Fact]
    public async Task AC76_Uom_delete_of_a_unit_in_use_is_in_use_and_keeps_the_unit()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        await Art.CreateAsync(tenant.Client, "ART-001", "Bolt", unit.Id());

        await mcp.ErrorAsync("uom_delete", new { id = unit.Id() }, "IN_USE");

        McpAssert.JsonEqual(unit, await Uom.GetAsync(tenant.Client, unit.Id()));
    }

    [Fact]
    public async Task AC77_Article_create_with_a_bad_base_unit_reference()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        var inactive = await Uom.CreateAsync(tenant.Client, "old", "Old unit", isActive: false);

        await mcp.ErrorAsync("article_create",
            new { code = "A1", name = "Bolt", type = "stock", baseUnitId = Guid.NewGuid() }, "REFERENCE_NOT_FOUND", "baseUnitId");
        await mcp.ErrorAsync("article_create",
            new { code = "A2", name = "Bolt", type = "stock", baseUnitId = inactive.Id() }, "REFERENCE_INACTIVE", "baseUnitId");
        await mcp.ErrorAsync("article_create",
            new { code = "A3", name = "Bolt", type = "stock", baseUnitId = "abc" }, "VALIDATION_FAILED", "baseUnitId");

        Assert.Equal(0, (await Art.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC78_Article_update_without_description_is_rejected_and_changes_nothing()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        using var created = await tenant.Client.PostAsJsonAsync(Art.Path,
            new { code = "ART-001", name = "Bolt", type = "stock", baseUnitId = unit.Id(), description = "zinc" });
        var article = await HttpAssert.JsonAsync(created, HttpStatusCode.Created);

        await mcp.ErrorAsync("article_update",
            new { id = article.Id(), code = "ART-002", name = "Changed", type = "service", baseUnitId = unit.Id(), isActive = false },
            "VALIDATION_FAILED", "description");

        McpAssert.JsonEqual(article, await Art.GetAsync(tenant.Client, article.Id()));
    }

    [Fact]
    public async Task AC79_Api_key_revoke_of_the_mcp_key_itself_is_refused()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;

        await mcp.ErrorAsync("api_key_revoke", new { id = tenant.ApiKeyId }, "CANNOT_REVOKE_SELF");

        var whoami = await mcp.OkAsync("whoami");
        Assert.Equal(tenant.ApiKeyId, whoami.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
    }

    [Theory]
    [InlineData("""{ "name": "x" }""")]
    [InlineData("""{ "code": "a b", "name": "" }""")]
    public async Task AC80_Validation_error_has_the_same_code_and_error_keys_over_http(string json)
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;

        var tool = await mcp.ErrorAsync("uom_create", JsonDocument.Parse(json).RootElement, "VALIDATION_FAILED");
        using var response = await tenant.Client.PostAsync(Uom.Path, HttpAssert.Raw(json));

        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        AssertSameError(problem, tool);
    }

    [Fact]
    public async Task AC80_Code_taken_has_the_same_code_and_error_keys_over_http()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        var tool = await mcp.ErrorAsync("uom_create", new { code = "KG", name = "Kilo" }, "CODE_TAKEN");
        using var response = await tenant.Client.PostAsJsonAsync(Uom.Path, new { code = "KG", name = "Kilo" });

        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "CODE_TAKEN");
        AssertSameError(problem, tool);
    }

    [Fact]
    public async Task AC80_In_use_has_the_same_code_and_error_keys_over_http()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        await Art.CreateAsync(tenant.Client, "ART-001", "Bolt", unit.Id());

        var tool = await mcp.ErrorAsync("uom_delete", new { id = unit.Id() }, "IN_USE");
        using var response = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit.Id()}");

        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "IN_USE");
        AssertSameError(problem, tool);
    }

    [Fact]
    public async Task AC80_Reference_not_found_has_the_same_code_and_error_keys_over_http()
    {
        var (tenant, mcp) = await ConnectAsync();
        await using var _ = mcp;
        var body = new { code = "A1", name = "Bolt", type = "stock", baseUnitId = Guid.NewGuid() };

        var tool = await mcp.ErrorAsync("article_create", body, "REFERENCE_NOT_FOUND", "baseUnitId");
        using var response = await tenant.Client.PostAsJsonAsync(Art.Path, body);

        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND");
        AssertSameError(problem, tool);
    }

    private static void AssertSameError(JsonElement problem, JsonElement toolError)
    {
        Assert.Equal(problem.Str("code"), toolError.Str("code"));
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(toolError));
    }
}
