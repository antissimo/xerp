using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 008, AC-81 and AC-82: a count through tools applies the same rules as HTTP and returns what HTTP
/// returns. The MCP client uses its own <c>agent</c> key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpStockCountToolTests(XerpFixture app)
{
    private sealed record Session(UnitSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the tenant's first key (not the MCP key).</summary>
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await Units.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-warehouse", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    /// <summary>The same refused input over both surfaces: same code, same <c>errors</c> keys (AC-82).</summary>
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
    public async Task AC81_Count_post_and_reverse_through_tools_only()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await Stock.ReceiveAsync(s.Http, w1, a, 100);

        var draft = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("count", w1, (a, 97)));

        Assert.Equal("count", draft.Str("type"));
        Assert.Equal("draft", draft.Str("status"));
        Assert.Equal(s.Agent.Id, draft.GetProperty("createdBy").GetGuid());
        Counts.AssertLine(Assert.Single(draft.DocumentLines()), quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), draft);

        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });

        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(s.Agent.Id, posted.GetProperty("postedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), posted);
        var onHand = await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = a, warehouseId = w1 });
        Assert.Equal(97m, Assert.Single(onHand.Items()).Quantity());
        McpAssert.JsonEqual(await Stock.OnHandAsync(s.Http, $"?articleId={a}&warehouseId={w1}"), onHand);
        var ledger = await s.Mcp.OkAsync("stock_ledger_entry_list", new { documentId = draft.Id() });
        Assert.Equal(-3m, Assert.Single(ledger.Items()).Quantity());
        Assert.Equal("count", ledger.Items()[0].GetProperty("document").Str("type"));
        McpAssert.JsonEqual(await Stock.LedgerAsync(s.Http, $"?documentId={draft.Id()}"), ledger);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), await s.Mcp.OkAsync("stock_document_get", new { number = "SC-000001" }));

        var reversing = await s.Mcp.OkAsync("stock_document_reverse", new { id = draft.Id(), documentDate = Stock.NextDay });

        Assert.Equal(("count", "SC-000002"), (reversing.Str("type"), reversing.Number()));
        Assert.Equal(draft.Id(), reversing.GetProperty("reversalOf").Id());
        Counts.AssertLine(reversing.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, reversing.Id()), reversing);
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, a, w1));
        var counts = await s.Mcp.OkAsync("stock_document_list", new { type = "count" });
        Assert.Equal(2, counts.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, "?type=count"), counts);
    }

    [Fact]
    public async Task AC81_An_agent_recovers_from_an_outdated_count_by_saving_it_again()
    {
        // Section 5: read the document, save it again with stock_document_update, post again.
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await Stock.ReceiveAsync(s.Http, w1, a, 100);
        var draft = await s.Mcp.OkAsync("stock_document_create", Counts.Draft(w1, (a, 8, s.S.Box), (s.S.B, 0, null)));
        Counts.AssertLine(draft.DocumentLines()[0], quantity: 8m, baseQuantity: 96m, book: 100m, difference: -4m);
        await Stock.IssueAsync(s.Http, w1, a, 10);

        await s.Mcp.ErrorAsync("stock_document_post", new { id = draft.Id() }, "COUNT_OUTDATED", "lines[0].quantity");
        var read = await s.Mcp.OkAsync("stock_document_get", new { id = draft.Id() });
        Assert.Equal("draft", read.Str("status"));
        Counts.AssertLine(read.DocumentLines()[0], quantity: 8m, baseQuantity: 96m, book: 100m, difference: -4m);

        var saved = await s.Mcp.OkAsync("stock_document_update", Counts.SameValues(read).WithId(draft.Id()));

        Counts.AssertLine(saved.DocumentLines()[0], quantity: 8m, baseQuantity: 96m, book: 90m, difference: 6m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), saved);
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });
        Assert.Equal("SC-000001", posted.Number());
        Assert.Equal(96m, await Stock.QuantityAsync(s.Http, a, w1));
    }

    [Fact]
    public async Task AC81_A_count_without_differences_is_reversed_through_the_tool()
    {
        // R17 (008-q T-Q5): a posted count that wrote no entries is reversed like any other.
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await Stock.ReceiveAsync(s.Http, w1, a, 100);
        var draft = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("count", w1, (a, 100)));
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });
        Assert.Empty(await Stock.EntriesAsync(s.Http, posted.Id()));

        var reversing = await s.Mcp.OkAsync("stock_document_reverse", new { id = posted.Id(), documentDate = Stock.NextDay });

        Assert.Equal(("count", "posted", "SC-000002"), (reversing.Str("type"), reversing.Str("status"), reversing.Number()));
        Assert.Equal(posted.Id(), reversing.GetProperty("reversalOf").Id());
        Assert.Equal(s.Agent.Id, reversing.GetProperty("postedBy").GetGuid());
        Counts.AssertLine(Assert.Single(reversing.DocumentLines()), quantity: 100m, baseQuantity: 100m, book: 100m, difference: 0m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, reversing.Id()), reversing);
        Assert.Empty(await Stock.EntriesAsync(s.Http, reversing.Id()));
        Assert.Equal("reversed", (await s.Mcp.OkAsync("stock_document_get", new { id = posted.Id() })).Str("status"));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, a, w1));

        using var http = await Stock.SendReverseAsync(s.Http, posted.Id(), Stock.NextDay);
        await AssertParityAsync(http,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = posted.Id(), documentDate = Stock.NextDay }),
            HttpStatusCode.Conflict, "INVALID_STATE");
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_An_outdated_count_is_the_same_error_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, b, w1) = (s.S.A, s.S.B, s.S.W1);
        await Stock.ReceiveAsync(s.Http, w1, a, 100);
        var draft = await Counts.CreateAsync(s.Http, w1, (a, 97, null), (b, 5, null));
        await Stock.IssueAsync(s.Http, w1, a, 10);

        using var http = await Stock.SendPostAsync(s.Http, draft.Id());
        var error = await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_post", new { id = draft.Id() }),
            HttpStatusCode.Conflict, "COUNT_OUTDATED", "lines[0].quantity");

        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(error));
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(90m, await Stock.QuantityAsync(s.Http, a, w1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, b, w1));
    }

    [Fact]
    public async Task AC82_Count_validation_errors_are_the_same_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);

        async Task ParityAsync(Func<System.Text.Json.Nodes.JsonObject> body, HttpStatusCode status, string code, params string[] keys)
        {
            using var http = await Stock.PostAsync(s.Http, body());
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_create", body()), status, code, keys);
        }

        await ParityAsync(() => Counts.Draft(w1, (a, 1, null), (a, 2, null)), HttpStatusCode.BadRequest,
            "VALIDATION_FAILED", "lines[0].articleId", "lines[1].articleId");
        await ParityAsync(() => Counts.Draft(w1, (a, -1, null)), HttpStatusCode.BadRequest, "VALIDATION_FAILED", "lines[0].quantity");
        await ParityAsync(() => Counts.Draft(w1, (a, 1, null)).With("toWarehouseId", s.S.W2.ToString()), HttpStatusCode.BadRequest,
            "VALIDATION_FAILED", "toWarehouseId");
        await ParityAsync(() => Counts.Draft(w1, (s.S.Service, 1, null)), HttpStatusCode.Conflict, "ARTICLE_NOT_STOCKED", "lines[0].articleId");
        // On the other types zero stays invalid, also through the tool.
        await ParityAsync(() => Stock.Draft("receipt", w1, (a, 0)), HttpStatusCode.BadRequest, "VALIDATION_FAILED", "lines[0].quantity");

        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
        // Zero is a valid counted quantity through the tool.
        var zero = await s.Mcp.OkAsync("stock_document_create", Counts.Draft(w1, (a, 0, null)));
        Counts.AssertLine(zero.DocumentLines()[0], quantity: 0m, baseQuantity: 0m, book: 0m, difference: 0m);
    }

    [Fact]
    public async Task AC82_Reversing_a_count_whose_surplus_was_issued_is_the_same_error_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        var count = await Counts.PostedAsync(s.Http, w1, (a, 50, null));
        await Stock.IssueAsync(s.Http, w1, a, 30);

        using var http = await Stock.SendReverseAsync(s.Http, count.Id());
        await AssertParityAsync(http,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = count.Id(), documentDate = Stock.Date }),
            HttpStatusCode.Conflict, "INSUFFICIENT_STOCK", "lines[0].quantity");

        Assert.Equal("posted", (await Stock.GetAsync(s.Http, count.Id())).Str("status"));
        Assert.Equal(20m, await Stock.QuantityAsync(s.Http, a, w1));
        // A posted count through the tools is as final as over HTTP.
        await s.Mcp.ErrorAsync("stock_document_post", new { id = count.Id() }, "INVALID_STATE");
        await s.Mcp.ErrorAsync("stock_document_delete", new { id = count.Id() }, "INVALID_STATE");
        await s.Mcp.ErrorAsync("stock_document_update", Stock.Replacement(w1, (a, 1)).WithId(count.Id()), "INVALID_STATE");
    }
}
