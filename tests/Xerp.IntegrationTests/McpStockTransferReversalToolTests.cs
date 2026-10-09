using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 006, AC-81 and AC-82: transfers and reversal through tools apply the same rules as HTTP and return what
/// HTTP returns. The MCP client uses its own <c>agent</c> key, so a reversal made through a tool is attributed
/// to that key and not to the tenant's first key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpStockTransferReversalToolTests(XerpFixture app)
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
    public async Task AC81_Transfer_post_and_reverse_through_tools_only()
    {
        await using var s = await AgentAsync();
        await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 100);
        var stockAtStart = await Stock.StockMapAsync(s.Http);

        var draft = await s.Mcp.OkAsync("stock_document_create", Stock.Transfer(s.S.W1, s.S.W2, (s.S.A, 30)));

        Assert.Equal("transfer", draft.Str("type"));
        Assert.Equal("draft", draft.Str("status"));
        Assert.Equal(s.S.W2, draft.GetProperty("toWarehouse").Id());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), draft);

        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal("ST-000001", posted.Number());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), posted);
        Assert.Equal(70m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W2));
        var ledger = await s.Mcp.OkAsync("stock_ledger_entry_list", new { documentId = draft.Id() });
        Assert.Equal(new[] { -30m, 30m }, ledger.Items().Select(e => e.Quantity()).ToArray());
        McpAssert.JsonEqual(await Stock.LedgerAsync(s.Http, $"?documentId={draft.Id()}"), ledger);

        var reversing = await s.Mcp.OkAsync("stock_document_reverse",
            new { id = draft.Id(), documentDate = Stock.NextDay, note = "wrong warehouse" });

        Assert.Equal("ST-000001", reversing.GetProperty("reversalOf").Str("number"));
        Assert.Equal(draft.Id(), reversing.GetProperty("reversalOf").Id());
        Assert.Equal("ST-000002", reversing.Number());
        Assert.Equal("posted", reversing.Str("status"));
        Assert.Equal("wrong warehouse", reversing.Str("note"));
        Assert.Equal(s.Agent.Id, reversing.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Agent.Id, reversing.GetProperty("postedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, reversing.Id()), reversing);

        var original = await s.Mcp.OkAsync("stock_document_get", new { id = draft.Id() });

        Assert.Equal("reversed", original.Str("status"));
        Assert.Equal(reversing.Id(), original.GetProperty("reversedBy").Id());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), original);
        // Stock is back to the start, also as the tools see it.
        Assert.Equal(stockAtStart, await Stock.StockMapAsync(s.Http));
        McpAssert.JsonEqual(await Stock.OnHandAsync(s.Http), await s.Mcp.OkAsync("stock_on_hand_list"));
        var reversal = await s.Mcp.OkAsync("stock_ledger_entry_list", new { documentId = reversing.Id() });
        Assert.All(reversal.Items(), e => Assert.True(e.GetProperty("document").Bool("isReversal")));
        McpAssert.JsonEqual(await Stock.LedgerAsync(s.Http, $"?documentId={reversing.Id()}"), reversal);
    }

    [Fact]
    public async Task AC81_A_reversal_over_http_and_one_through_the_tool_give_the_same_kind_of_document()
    {
        await using var s = await AgentAsync();
        var first = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 5);
        var second = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 5);

        var overHttp = await Stock.ReverseAsync(s.Http, first.Id(), Stock.NextDay);
        var throughTool = await s.Mcp.OkAsync("stock_document_reverse", new { id = second.Id(), documentDate = Stock.NextDay });

        Assert.Equal(overHttp.PropertyNames(), throughTool.PropertyNames());
        Assert.Equal(("SR-000003", "SR-000004"), (overHttp.Number(), throughTool.Number()));
        JsonBody.AssertNull(throughTool, "note", "reversedBy", "toWarehouse");
        McpAssert.JsonEqual(overHttp.GetProperty("lines"), throughTool.GetProperty("lines"));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
    }

    [Fact]
    public async Task AC81_Update_and_list_through_tools_know_transfers_and_reversed_documents()
    {
        await using var s = await AgentAsync();
        await Stock.ReceiveAsync(s.Http, s.S.W2, s.S.A, 10);
        var draft = await Stock.CreateTransferAsync(s.Http, s.S.W1, s.S.W2, (s.S.A, 1));

        var swapped = await s.Mcp.OkAsync("stock_document_update",
            Stock.TransferReplacement(s.S.W2, s.S.W1, (s.S.A, 4)).WithId(draft.Id()));

        Assert.Equal(s.S.W2, swapped.GetProperty("warehouse").Id());
        Assert.Equal(s.S.W1, swapped.GetProperty("toWarehouse").Id());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), swapped);

        await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });
        await s.Mcp.OkAsync("stock_document_reverse", new { id = draft.Id(), documentDate = Stock.Date });
        var transfers = await s.Mcp.OkAsync("stock_document_list", new { type = "transfer" });
        var reversed = await s.Mcp.OkAsync("stock_document_list", new { status = "reversed" });
        var intoW1 = await s.Mcp.OkAsync("stock_document_list", new { warehouseId = s.S.W1 });

        Assert.Equal(2, transfers.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, "?type=transfer"), transfers);
        Assert.Equal(draft.Id(), Assert.Single(reversed.Items()).Id());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, "?status=reversed"), reversed);
        // W1 is only the destination of the two transfers.
        Assert.Equal(2, intoW1.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, $"?warehouseId={s.S.W1}"), intoW1);
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_Transfer_validation_and_reference_errors_have_the_code_and_keys_of_http()
    {
        await using var s = await AgentAsync();
        var same = Stock.Transfer(s.S.W1, s.S.W1, (s.S.A, 1));
        var missing = Stock.Transfer(s.S.W1, s.S.W2, (s.S.A, 1)).Without("toWarehouseId");
        var unknown = Stock.Transfer(s.S.W1, Guid.NewGuid(), (s.S.A, 1));
        var onReceipt = Stock.Draft("receipt", s.S.W1, (s.S.A, 1)).With("toWarehouseId", s.S.W2.ToString());

        using var sameHttp = await Stock.PostAsync(s.Http, same.DeepClone().AsObject());
        using var missingHttp = await Stock.PostAsync(s.Http, missing.DeepClone().AsObject());
        using var unknownHttp = await Stock.PostAsync(s.Http, unknown.DeepClone().AsObject());
        using var onReceiptHttp = await Stock.PostAsync(s.Http, onReceipt.DeepClone().AsObject());

        await AssertParityAsync(sameHttp, await s.Mcp.CallAsync("stock_document_create", same),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "toWarehouseId");
        await AssertParityAsync(missingHttp, await s.Mcp.CallAsync("stock_document_create", missing),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "toWarehouseId");
        await AssertParityAsync(unknownHttp, await s.Mcp.CallAsync("stock_document_create", unknown),
            HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND", "toWarehouseId");
        await AssertParityAsync(onReceiptHttp, await s.Mcp.CallAsync("stock_document_create", onReceipt),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "toWarehouseId");
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC82_Transfer_posting_errors_have_the_code_and_keys_of_http()
    {
        await using var s = await AgentAsync();
        await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 5);
        var tooMuch = await Stock.CreateTransferAsync(s.Http, s.S.W1, s.S.W2, (s.S.A, 3), (s.S.A, 3));

        using var shortHttp = await Stock.SendPostAsync(s.Http, tooMuch.Id());
        await AssertParityAsync(shortHttp, await s.Mcp.CallAsync("stock_document_post", new { id = tooMuch.Id() }),
            HttpStatusCode.Conflict, "INSUFFICIENT_STOCK", "lines[0].quantity", "lines[1].quantity");

        await Stock.SetWarehouseActiveAsync(s.Http, s.S.W2, false);
        using var inactiveHttp = await Stock.SendPostAsync(s.Http, tooMuch.Id());
        await AssertParityAsync(inactiveHttp, await s.Mcp.CallAsync("stock_document_post", new { id = tooMuch.Id() }),
            HttpStatusCode.Conflict, "REFERENCE_INACTIVE", "toWarehouseId");
        await Stock.AssertDraftAsync(s.Http, tooMuch.Id());
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
    }

    [Fact]
    public async Task AC82_Reverse_errors_have_the_code_and_keys_of_http()
    {
        await using var s = await AgentAsync();
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 1));
        var issued = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 10);
        await Stock.IssueAsync(s.Http, s.S.W1, s.S.A, 4);
        var kept = await Stock.ReceiveAsync(s.Http, s.S.W2, s.S.B, 10);
        var random = Guid.NewGuid();
        var stockBefore = await Stock.StockMapAsync(s.Http);
        var documentsBefore = (await Stock.DocumentsAsync(s.Http)).Total();

        using var draftHttp = await Stock.SendReverseAsync(s.Http, draft.Id());
        using var issuedHttp = await Stock.SendReverseAsync(s.Http, issued.Id());
        using var earlyHttp = await Stock.SendReverseAsync(s.Http, kept.Id(), "2026-10-08");
        using var randomHttp = await Stock.SendReverseAsync(s.Http, random);
        using var noDateHttp = await Stock.SendReverseAsync(s.Http, kept.Id(), new System.Text.Json.Nodes.JsonObject());

        await AssertParityAsync(draftHttp,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = draft.Id(), documentDate = Stock.Date }),
            HttpStatusCode.Conflict, "INVALID_STATE");
        await AssertParityAsync(issuedHttp,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = issued.Id(), documentDate = Stock.Date }),
            HttpStatusCode.Conflict, "INSUFFICIENT_STOCK", "lines[0].quantity");
        await AssertParityAsync(earlyHttp,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = kept.Id(), documentDate = "2026-10-08" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "documentDate");
        await AssertParityAsync(randomHttp,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = random, documentDate = Stock.Date }),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertParityAsync(noDateHttp,
            await s.Mcp.CallAsync("stock_document_reverse", new { id = kept.Id() }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "documentDate");
        // Nothing was reversed by any of the refused calls.
        Assert.Equal(stockBefore, await Stock.StockMapAsync(s.Http));
        Assert.Equal(documentsBefore, (await Stock.DocumentsAsync(s.Http)).Total());
        await Stock.AssertUnchangedAsync(s.Http, issued);
        await Stock.AssertUnchangedAsync(s.Http, kept);
    }

    [Fact]
    public async Task AC82_A_reversed_and_a_reversing_document_refuse_every_tool_that_would_change_them()
    {
        await using var s = await AgentAsync();
        var original = await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 10);
        var reversing = await s.Mcp.OkAsync("stock_document_reverse", new { id = original.Id(), documentDate = Stock.Date });
        await Stock.ReceiveAsync(s.Http, s.S.W1, s.S.A, 50);

        foreach (var id in new[] { original.Id(), reversing.Id() })
        {
            await s.Mcp.ErrorAsync("stock_document_reverse", new { id, documentDate = Stock.NextDay }, "INVALID_STATE");
            await s.Mcp.ErrorAsync("stock_document_post", new { id }, "INVALID_STATE");
            await s.Mcp.ErrorAsync("stock_document_delete", new { id }, "INVALID_STATE");
            await s.Mcp.ErrorAsync("stock_document_update", Stock.Replacement(s.S.W1, (s.S.A, 1)).WithId(id), "INVALID_STATE");
        }
        Assert.Equal(50m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
        Assert.Equal(3, (await Stock.DocumentsAsync(s.Http)).Total());
    }
}
