using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-81 to AC-84: the stock tools apply the same rules as HTTP and return what HTTP returns. The MCP
/// client uses its own <c>agent</c> key, so attribution to the MCP key is distinguishable from the tenant's
/// first key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpStockToolTests(XerpFixture app)
{
    private sealed record Session(StockSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the tenant's first key (not the MCP key).</summary>
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await Stock.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-warehouse", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    /// <summary>The same refused input over both surfaces: same code, same <c>errors</c> keys (AC-83).</summary>
    private static async Task<JsonElement> AssertParityAsync(
        HttpResponseMessage http, CallToolResult tool, HttpStatusCode status, string code, params string[] errorKeys)
    {
        var problem = await HttpAssert.ProblemAsync(http, status, code);
        var error = McpAssert.Error(tool, code, errorKeys);
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(error));
        return error;
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_Receive_and_query_through_tools_only()
    {
        await using var s = await AgentAsync();

        var draft = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("receipt", s.S.W1, (s.S.A, 100)));

        Assert.Equal("draft", draft.Str("status"));
        JsonBody.AssertNull(draft, "number", "postedAt", "postedBy");
        Assert.Equal(s.Agent.Id, draft.GetProperty("createdBy").GetGuid());
        Assert.Equal(100m, Assert.Single(draft.DocumentLines()).Quantity());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), draft);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));

        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal("SR-000001", posted.Number());
        Assert.Equal(s.Agent.Id, posted.GetProperty("postedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), posted);

        var onHand = await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = s.S.A });
        var ledger = await s.Mcp.OkAsync("stock_ledger_entry_list", new { documentId = draft.Id() });

        var item = Assert.Single(onHand.Items());
        Assert.Equal(100m, item.Quantity());
        Assert.Equal(s.S.W1, item.GetProperty("warehouse").Id());
        McpAssert.JsonEqual(await Stock.OnHandAsync(s.Http, $"?articleId={s.S.A}"), onHand);
        var entry = Assert.Single(ledger.Items());
        Assert.Equal(100m, entry.Quantity());
        Assert.Equal(s.Agent.Id, entry.GetProperty("postedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.LedgerAsync(s.Http, $"?documentId={draft.Id()}"), ledger);
    }

    [Fact]
    public async Task AC81_Issue_through_tools_reduces_stock_and_never_below_zero()
    {
        await using var s = await AgentAsync();
        await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 10);

        var issue = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("issue", s.S.W1, (s.S.A, 4), (s.S.A, 6)));
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = issue.Id() });
        var second = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("issue", s.S.W1, (s.S.A, 0.000001m)));
        await s.Mcp.ErrorAsync("stock_document_post", new { id = second.Id() }, "INSUFFICIENT_STOCK", "lines[0].quantity");

        Assert.Equal("SI-000001", posted.Number());
        var onHand = await s.Mcp.OkAsync("stock_on_hand_list");
        Assert.Empty(onHand.Items());
        Assert.Equal(0, onHand.Total());
        var ledger = await s.Mcp.OkAsync("stock_ledger_entry_list", new { articleId = s.S.A, warehouseId = s.S.W1 });
        Assert.Equal(new[] { 10m, -4m, -6m }, ledger.Items().Select(e => e.Quantity()).ToArray());
        McpAssert.JsonEqual(await Stock.LedgerAsync(s.Http, $"?articleId={s.S.A}&warehouseId={s.S.W1}"), ledger);
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_Get_by_id_and_by_number_equals_http()
    {
        await using var s = await AgentAsync();
        var posted = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 5);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.S.W1, (s.S.A, 1), (s.S.B, 2));

        var byId = await s.Mcp.OkAsync("stock_document_get", new { id = posted.Id() });
        var byNumber = await s.Mcp.OkAsync("stock_document_get", new { number = "SR-000001" });
        var byLowerNumber = await s.Mcp.OkAsync("stock_document_get", new { number = "sr-000001" });
        var draftById = await s.Mcp.OkAsync("stock_document_get", new { id = draft.Id() });

        foreach (var result in new[] { byId, byNumber, byLowerNumber })
            McpAssert.JsonEqual(posted, result);
        McpAssert.JsonEqual(draft, draftById);
        await s.Mcp.ErrorAsync("stock_document_get", new { number = "SR-999999" }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("stock_document_get", new { id = Guid.NewGuid() }, "NOT_FOUND");
    }

    [Fact]
    public async Task AC82_List_with_type_and_status_equals_http()
    {
        await using var s = await AgentAsync();
        await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 5);
        await Stock.ReceiveAsync(s.Http, s.S.W2, s.S.A, 5);
        await Stock.IssueAsync(s.Http, s.S.W1, s.S.A, 1);
        await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 1));

        var filtered = await s.Mcp.OkAsync("stock_document_list", new { type = "receipt", status = "posted" });
        var combined = await s.Mcp.OkAsync("stock_document_list",
            new { type = "receipt", status = "posted", warehouseId = s.S.W2, search = "sr-", limit = 1, offset = 0 });
        var all = await s.Mcp.OkAsync("stock_document_list");

        Assert.Equal(2, filtered.Total());
        Assert.All(filtered.Items(), d => Assert.Equal(("receipt", "posted"), (d.Str("type"), d.Str("status"))));
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, "?type=receipt&status=posted"), filtered);
        Assert.Equal(1, combined.Total());
        McpAssert.JsonEqual(
            await Stock.DocumentsAsync(s.Http, $"?type=receipt&status=posted&warehouseId={s.S.W2}&search=sr-&limit=1&offset=0"), combined);
        Assert.Equal(4, all.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http), all);
    }

    [Fact]
    public async Task AC82_Update_and_delete_of_a_draft_equal_http()
    {
        await using var s = await AgentAsync();
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 1));
        var arguments = new JsonObject
        {
            ["id"] = draft.Id().ToString(), ["documentDate"] = "2026-12-01", ["warehouseId"] = s.S.W2.ToString(),
            ["reference"] = "DN-1", ["note"] = null, ["lines"] = Stock.Lines((s.S.B, 7), (s.S.A, 2.5m)),
        };

        var updated = await s.Mcp.OkAsync("stock_document_update", arguments);

        Assert.Equal(draft.Id(), updated.Id());
        Assert.Equal("receipt", updated.Str("type"));
        Assert.Equal("draft", updated.Str("status"));
        Assert.Equal("2026-12-01", updated.Str("documentDate"));
        Assert.Equal(s.S.W2, updated.GetProperty("warehouse").Id());
        Assert.Equal("DN-1", updated.Str("reference"));
        JsonBody.AssertNull(updated, "note");
        Assert.Equal(new[] { 7m, 2.5m }, updated.DocumentLines().Select(l => l.Quantity()).ToArray());
        Assert.Equal(s.Agent.Id, updated.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), updated);

        var deleted = await s.Mcp.OkAsync("stock_document_delete", new { id = draft.Id() });

        McpAssert.JsonEqual(JsonSerializer.SerializeToElement(new { deleted = true }), deleted);
        using var get = await s.Http.GetAsync($"{Stock.Documents}/{draft.Id()}");
        await HttpAssert.NotFoundAsync(get);
        await s.Mcp.ErrorAsync("stock_document_delete", new { id = draft.Id() }, "NOT_FOUND");
    }

    // ---- AC-83 ----

    [Fact]
    public async Task AC83_Posting_an_issue_beyond_stock_is_the_same_error_as_over_http()
    {
        await using var s = await AgentAsync();
        await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 10);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.S.W1, (s.S.A, 11));
        var ledger = await Stock.LedgerAsync(s.Http);

        var tool = await s.Mcp.CallAsync("stock_document_post", new { id = draft.Id() });
        using var http = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertParityAsync(http, tool, HttpStatusCode.Conflict, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "A refused posting wrote to the ledger");
    }

    [Fact]
    public async Task AC83_Post_update_and_delete_of_a_posted_document_are_invalid_state()
    {
        await using var s = await AgentAsync();
        var posted = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 10);
        var update = Stock.Replacement(s.S.W2, (s.S.B, 1));

        var toolPost = await s.Mcp.CallAsync("stock_document_post", new { id = posted.Id() });
        var toolUpdate = await s.Mcp.CallAsync("stock_document_update", update.DeepClone().AsObject().WithId(posted.Id()));
        var toolDelete = await s.Mcp.CallAsync("stock_document_delete", new { id = posted.Id() });
        using var httpPost = await Stock.SendPostAsync(s.Http, posted.Id());
        using var httpUpdate = await Stock.PutAsync(s.Http, posted.Id(), update);
        using var httpDelete = await Stock.DeleteAsync(s.Http, posted.Id());

        await AssertParityAsync(httpPost, toolPost, HttpStatusCode.Conflict, "INVALID_STATE");
        await AssertParityAsync(httpUpdate, toolUpdate, HttpStatusCode.Conflict, "INVALID_STATE");
        await AssertParityAsync(httpDelete, toolDelete, HttpStatusCode.Conflict, "INVALID_STATE");
        await Stock.AssertUnchangedAsync(s.Http, posted);
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
        Assert.Equal(1, (await Stock.LedgerAsync(s.Http)).Total());
    }

    public static TheoryData<string, string, int, string, string> RejectedCreates() => new()
    {
        { "service article", """[ { "articleId": "{S}", "quantity": 1 } ]""", 409, "ARTICLE_NOT_STOCKED", "lines[0].articleId" },
        { "quantity 0", """[ { "articleId": "{A}", "quantity": 0 } ]""", 400, "VALIDATION_FAILED", "lines[0].quantity" },
        { "unknown article on line 2", """[ { "articleId": "{A}", "quantity": 1 }, { "articleId": "0199c0de-0000-7000-8000-000000000001", "quantity": 1 } ]""",
            409, "REFERENCE_NOT_FOUND", "lines[1].articleId" },
        { "no lines", "[]", 400, "VALIDATION_FAILED", "lines" },
    };

    [Theory]
    [MemberData(nameof(RejectedCreates))]
    public async Task AC83_A_rejected_create_has_the_same_code_and_error_keys_over_http_and_mcp(
        string what, string lines, int status, string code, string errorKey)
    {
        await using var s = await AgentAsync();
        var body = Stock.Draft("issue", s.S.W1)
            .With("lines", JsonNode.Parse(lines.Replace("{S}", s.S.S.ToString()).Replace("{A}", s.S.A.ToString())));

        var tool = await s.Mcp.CallAsync("stock_document_create", body);
        using var http = await Stock.PostAsync(s.Http, body);

        Assert.NotNull(what);
        await AssertParityAsync(http, tool, (HttpStatusCode)status, code, errorKey);
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC83_A_quoted_quantity_is_a_validation_tool_error()
    {
        await using var s = await AgentAsync();
        var body = Stock.Draft("receipt", s.S.W1).With("lines", new JsonArray(Stock.Line(s.S.A, "5")));

        await s.Mcp.ErrorAsync("stock_document_create", body, "VALIDATION_FAILED");

        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC83_Get_needs_exactly_one_of_id_and_number()
    {
        await using var s = await AgentAsync();
        var posted = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 1);

        await s.Mcp.ErrorAsync("stock_document_get", new { }, "VALIDATION_FAILED", "id", "number");
        await s.Mcp.ErrorAsync("stock_document_get", new { id = posted.Id(), number = "SR-000001" }, "VALIDATION_FAILED", "id", "number");
    }

    [Fact]
    public async Task AC83_Masters_used_by_a_stock_document_are_in_use_through_their_tools_too()
    {
        await using var s = await AgentAsync();
        await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 1));
        var articleUpdate = Stock.ArticleBody(s.S.ArticleA).With("type", "service");

        var toolDelete = await s.Mcp.CallAsync("article_delete", new { id = s.S.A });
        var toolUpdate = await s.Mcp.CallAsync("article_update", articleUpdate.DeepClone().AsObject().WithId(s.S.A));
        var toolWarehouse = await s.Mcp.CallAsync("warehouse_delete", new { id = s.S.W1 });
        using var httpDelete = await s.Http.DeleteAsync($"{Art.Path}/{s.S.A}");
        using var httpUpdate = await Stock.PutArticleAsync(s.Http, s.S.A, articleUpdate);
        using var httpWarehouse = await MasterApi.Warehouses.DeleteAsync(s.Http, s.S.W1);

        await AssertParityAsync(httpDelete, toolDelete, HttpStatusCode.Conflict, "IN_USE");
        await AssertParityAsync(httpUpdate, toolUpdate, HttpStatusCode.Conflict, "IN_USE", "type");
        await AssertParityAsync(httpWarehouse, toolWarehouse, HttpStatusCode.Conflict, "IN_USE");
        McpAssert.JsonEqual(s.S.ArticleA, await Art.GetAsync(s.Http, s.S.A), "A refused tool call changed the article");
    }

    [Fact]
    public async Task AC83_Posting_against_an_inactive_master_is_the_same_error_as_over_http()
    {
        await using var s = await AgentAsync();
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 1), (s.S.B, 1));
        await Stock.SetArticleActiveAsync(s.Http, s.S.B, false);

        var tool = await s.Mcp.CallAsync("stock_document_post", new { id = draft.Id() });
        using var http = await Stock.SendPostAsync(s.Http, draft.Id());

        await AssertParityAsync(http, tool, HttpStatusCode.Conflict, "REFERENCE_INACTIVE", "lines[1].articleId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
    }

    // ---- AC-84 ----

    [Fact]
    public async Task AC84_Created_over_http_and_posted_through_the_tool_are_attributed_to_their_own_keys()
    {
        await using var s = await AgentAsync();
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 3));

        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });

        Assert.Equal(s.S.Tenant.ApiKeyId, posted.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Agent.Id, posted.GetProperty("postedBy").GetGuid());
        var stored = await Stock.GetAsync(s.Http, draft.Id());
        McpAssert.JsonEqual(stored, posted);
        var entry = Assert.Single((await Stock.LedgerAsync(s.Http, $"?documentId={draft.Id()}")).Items());
        Assert.Equal(s.Agent.Id, entry.GetProperty("postedBy").GetGuid());
    }
}
