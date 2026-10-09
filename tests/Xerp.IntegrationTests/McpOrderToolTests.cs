using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-86 to AC-88: the order tools and the stock tools with an order link apply the same rules as
/// HTTP and return what HTTP returns. The MCP client uses its own <c>agent</c> key, so attribution to the MCP
/// key is distinguishable from the tenant's first key. Written once for every kind of order (ADR-0016).
/// </summary>
public abstract class McpOrderToolTests(XerpFixture app, OrderApi o)
{
    protected sealed record Session(OrderSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the tenant's first key (not the MCP key).</summary>
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    protected async Task<Session> AgentAsync()
    {
        var setup = await Orders.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-orders", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    /// <summary>The same refused input over both surfaces: same code, same <c>errors</c> keys (AC-88).</summary>
    protected static async Task<JsonElement> AssertParityAsync(
        HttpResponseMessage http, CallToolResult tool, string code, params string[] errorKeys)
    {
        var status = code == "VALIDATION_FAILED" ? HttpStatusCode.BadRequest : code == "NOT_FOUND" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict;
        var problem = await HttpAssert.ProblemAsync(http, status, code);
        var error = McpAssert.Error(tool, code, errorKeys);
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(error));
        return error;
    }

    // ---- AC-86 ----

    [Fact]
    public async Task AC86_Order_fulfil_close_and_reopen_through_tools_only()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await o.PrepareStockAsync(s.S);

        var draft = await s.Mcp.OkAsync(o.Tool("create"), o.Body(s.S, (a, 10, null, 2.5m), (a, 1, s.S.Box, 30m)));

        Assert.Equal("draft", draft.Str("status"));
        JsonBody.AssertNull(draft, "number", "confirmedAt", "confirmedBy");
        Assert.Equal(s.Agent.Id, draft.GetProperty("createdBy").GetGuid());
        Assert.Equal(55m, draft.Dec("totalAmount"));
        McpAssert.JsonEqual(await o.GetAsync(s.Http, draft.Id()), draft);

        var confirmed = await s.Mcp.OkAsync(o.Tool("confirm"), new { id = draft.Id() });

        Assert.Equal(("confirmed", o.Number(1)), (confirmed.Str("status"), confirmed.Number()));
        Assert.Equal(s.Agent.Id, confirmed.GetProperty("confirmedBy").GetGuid());
        Assert.Equal([10m, 12m], confirmed.OrderLines().Select(l => l.Outstanding()));
        McpAssert.JsonEqual(await o.GetAsync(s.Http, draft.Id()), confirmed);

        var document = await s.Mcp.OkAsync("stock_document_create", o.Document(w1, draft.Id(), (a, 4, 1, null), (a, 1, 2, s.S.Box)));

        Assert.Equal((o.DocumentType, "draft"), (document.Str("type"), document.Str("status")));
        o.AssertLinked(document, confirmed);
        Assert.Equal([1, 2], document.DocumentLines().Select(l => l.GetProperty("orderLineNo").GetInt32()));
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, document.Id()), document);

        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = document.Id() });

        Assert.Equal(o.DocumentNumber(1), posted.Number());
        Assert.Equal(s.Agent.Id, posted.GetProperty("postedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, document.Id()), posted);

        var read = await s.Mcp.OkAsync(o.Tool("get"), new { number = o.Number(1) });

        Assert.Equal("partial", o.ProgressOf(read));
        Assert.Equal([(4m, 6m), (12m, 0m)], read.OrderLines().Select(l => (o.DoneOf(l), l.Outstanding())));
        McpAssert.JsonEqual(await o.GetAsync(s.Http, draft.Id()), read);
        McpAssert.JsonEqual(read, await s.Mcp.OkAsync(o.Tool("get"), new { id = draft.Id() }));

        var onHand = await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = a, warehouseId = w1 });

        var item = Assert.Single(onHand.Items());
        Assert.Equal(6m, item.Dec(o.OnHand));
        Assert.Equal(await Stock.QuantityAsync(s.Http, a, w1), item.Quantity());
        McpAssert.JsonEqual(await Stock.OnHandAsync(s.Http, $"?articleId={a}&warehouseId={w1}"), onHand);
        var linked = await s.Mcp.OkAsync("stock_document_list", new Dictionary<string, object> { [o.LinkId] = draft.Id() });
        Assert.Equal(1, linked.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, $"?{o.LinkId}={draft.Id()}"), linked);

        var closed = await s.Mcp.OkAsync(o.Tool("close"), new { id = draft.Id() });

        Assert.Equal("closed", closed.Str("status"));
        Assert.Equal(s.Agent.Id, closed.GetProperty("closedBy").GetGuid());
        Assert.Equal([0m, 0m], closed.OrderLines().Select(l => l.Outstanding()));
        McpAssert.JsonEqual(await o.GetAsync(s.Http, draft.Id()), closed);
        Assert.Equal(0m, await o.OnHandAsync(s.Http, a, w1));

        var reopened = await s.Mcp.OkAsync(o.Tool("reopen"), new { id = draft.Id() });

        Assert.Equal("confirmed", reopened.Str("status"));
        JsonBody.AssertNull(reopened, "closedAt", "closedBy");
        McpAssert.JsonEqual(read, reopened, "Close and reopen through tools changed the order");
        McpAssert.JsonEqual(await o.GetAsync(s.Http, draft.Id()), reopened);
    }

    // ---- AC-87 ----

    [Fact]
    public async Task AC87_List_update_and_delete_equal_http()
    {
        await using var s = await AgentAsync();
        await o.PrepareStockAsync(s.S);
        var partial = await o.OrderedAsync(s.S, (s.S.A, 10, null, 1m));
        await o.FulfilAsync(s.Http, partial, 1, 4);
        await o.OrderedAsync(s.S, (s.S.B, 1, null, 1m));
        var draft = await o.CreateAsync(s.Http, o.Body(s.S, (s.S.A, 1, null, 1m)).With("reference", "Offer-9"));

        var filtered = await s.Mcp.OkAsync(o.Tool("list"), new Dictionary<string, object> { ["status"] = "confirmed", [o.Progress] = "partial" });
        var all = await s.Mcp.OkAsync(o.Tool("list"));
        var combined = await s.Mcp.OkAsync(o.Tool("list"), new Dictionary<string, object>
        {
            [o.PartnerId] = o.PartnerOf(s.S).Id(), ["warehouseId"] = s.S.W1, ["search"] = "offer", ["limit"] = 1, ["offset"] = 0,
        });

        Assert.Equal(partial.Id(), Assert.Single(filtered.Items()).Id());
        McpAssert.JsonEqual(await o.ListAsync(s.Http, $"?status=confirmed&{o.Progress}=partial"), filtered);
        Assert.Equal(3, all.Total());
        McpAssert.JsonEqual(await o.ListAsync(s.Http), all);
        Assert.Equal(draft.Id(), Assert.Single(combined.Items()).Id());
        McpAssert.JsonEqual(
            await o.ListAsync(s.Http, $"?{o.PartnerId}={o.PartnerOf(s.S).Id()}&warehouseId={s.S.W1}&search=offer&limit=1&offset=0"), combined);

        var body = o.Replacement(o.PartnerOf(s.S).Id(), s.S.W2, (s.S.B, 7, null, 1.1m)).With(o.DueDate, "2026-11-15").With("note", "Changed by the agent");
        var updated = await s.Mcp.OkAsync(o.Tool("update"), body.WithId(draft.Id()));

        Assert.Equal(draft.Id(), updated.Id());
        Assert.Equal((7.7m, "2026-11-15"), (updated.Dec("totalAmount"), updated.Str(o.DueDate)));
        JsonBody.AssertNull(updated, "reference");
        Assert.Equal(s.Agent.Id, updated.GetProperty("updatedBy").GetGuid());
        Assert.Equal(s.S.Tenant.ApiKeyId, updated.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await o.GetAsync(s.Http, draft.Id()), updated);

        var deleted = await s.Mcp.OkAsync(o.Tool("delete"), new { id = draft.Id() });

        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        Assert.Equal(new[] { "deleted" }, deleted.PropertyNames());
        using var gone = await o.SendGetAsync(s.Http, draft.Id());
        await HttpAssert.NotFoundAsync(gone);
        await s.Mcp.ErrorAsync(o.Tool("get"), new { id = draft.Id() }, "NOT_FOUND");
    }

    // ---- AC-88 ----

    [Fact]
    public async Task AC88_Order_tool_errors_are_the_same_as_over_http()
    {
        await using var s = await AgentAsync();
        var confirmed = await o.OrderedAsync(s.S, (s.S.A, 10, null, 2.5m));
        var draft = await o.DraftAsync(s.S, (s.S.A, 1, null, 1m));

        async Task CreateAsync(Func<JsonObject> body, string code, params string[] keys)
        {
            using var http = await o.PostAsync(s.Http, body());
            await AssertParityAsync(http, await s.Mcp.CallAsync(o.Tool("create"), body()), code, keys);
        }

        await CreateAsync(() => o.Body(o.WrongPartnerOf(s.S).Id(), s.S.W1, (s.S.A, 1, null, 1m)), "PARTNER_ROLE_MISSING", o.PartnerId);
        await CreateAsync(() => o.Body(s.S, (s.S.A, 1, null, -1m)), "VALIDATION_FAILED", "lines[0].unitPrice");
        await CreateAsync(() => o.Body(Guid.CreateVersion7(), s.S.W1, (s.S.A, 1, null, 1m)), "REFERENCE_NOT_FOUND", o.PartnerId);
        await CreateAsync(() => o.Body(s.S, (s.S.Service, 1, null, 1m)), "ARTICLE_NOT_STOCKED", "lines[0].articleId");
        await CreateAsync(() => o.Body(s.S, (s.S.B, 1, s.S.Box, 1m)), "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await CreateAsync(() => o.Body(s.S, (s.S.A, 1, null, 1m)).With(o.DueDate, "2026-10-08"), "VALIDATION_FAILED", o.DueDate);
        await CreateAsync(() => o.Body(s.S, (s.S.A, 1, null, 1m)).With("status", "confirmed"), "VALIDATION_FAILED");
        Assert.Equal(2, (await o.ListAsync(s.Http)).Total());

        var replacement = o.Replacement(s.S, (s.S.B, 1, null, 1m));
        using var put = await o.PutAsync(s.Http, confirmed.Id(), replacement);
        await AssertParityAsync(put, await s.Mcp.CallAsync(o.Tool("update"), o.Replacement(s.S, (s.S.B, 1, null, 1m)).WithId(confirmed.Id())), "INVALID_STATE");
        using var delete = await o.DeleteAsync(s.Http, confirmed.Id());
        await AssertParityAsync(delete, await s.Mcp.CallAsync(o.Tool("delete"), new { id = confirmed.Id() }), "INVALID_STATE");
        using var confirm = await o.SendAsync(s.Http, confirmed.Id(), "confirm");
        await AssertParityAsync(confirm, await s.Mcp.CallAsync(o.Tool("confirm"), new { id = confirmed.Id() }), "INVALID_STATE");
        using var reopen = await o.SendAsync(s.Http, confirmed.Id(), "reopen");
        await AssertParityAsync(reopen, await s.Mcp.CallAsync(o.Tool("reopen"), new { id = confirmed.Id() }), "INVALID_STATE");
        using var close = await o.SendAsync(s.Http, draft.Id(), "close");
        await AssertParityAsync(close, await s.Mcp.CallAsync(o.Tool("close"), new { id = draft.Id() }), "INVALID_STATE");
        // A missing field of the update is a validation error under its own name.
        using var incomplete = await o.PutAsync(s.Http, draft.Id(), o.Replacement(s.S, (s.S.A, 1, null, 1m)).Without("note"));
        await AssertParityAsync(incomplete,
            await s.Mcp.CallAsync(o.Tool("update"), o.Replacement(s.S, (s.S.A, 1, null, 1m)).Without("note").WithId(draft.Id())), "VALIDATION_FAILED", "note");

        await o.AssertUnchangedAsync(s.Http, confirmed);
        await o.AssertUnchangedAsync(s.Http, draft);
    }

    [Fact]
    public async Task AC88_Get_needs_exactly_one_of_id_and_number()
    {
        await using var s = await AgentAsync();
        var confirmed = await o.OrderedAsync(s.S, (s.S.A, 10, null, 2.5m));

        await s.Mcp.ErrorAsync(o.Tool("get"), new { }, "VALIDATION_FAILED", "id", "number");
        await s.Mcp.ErrorAsync(o.Tool("get"), new { id = confirmed.Id(), number = confirmed.Number() }, "VALIDATION_FAILED", "id", "number");
        await s.Mcp.ErrorAsync(o.Tool("get"), new { id = "abc" }, "NOT_FOUND");
        await s.Mcp.ErrorAsync(o.Tool("get"), new { number = o.Number(999) }, "NOT_FOUND");
        await s.Mcp.ErrorAsync(o.Tool("get"), new { id = confirmed.Id(), foo = 1 }, "VALIDATION_FAILED");
        await s.Mcp.ErrorAsync(o.Tool("list"), new { status = "open" }, "VALIDATION_FAILED", "status");
        await s.Mcp.ErrorAsync(o.Tool("confirm"), new { }, "VALIDATION_FAILED", "id");
        McpAssert.JsonEqual(confirmed, await s.Mcp.OkAsync(o.Tool("get"), new { number = confirmed.Number()!.ToLowerInvariant() }));
    }

    [Fact]
    public async Task AC88_Fulfilment_errors_through_the_stock_tools_are_the_same_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await o.PrepareStockAsync(s.S);
        var order = await o.OrderedAsync(s.S, (a, 10, null, 1m));
        var closed = await o.CloseAsync(s.Http, (await o.OrderedAsync(s.S, (a, 10, null, 1m))).Id());

        // Above the order.
        var above = await s.Mcp.OkAsync("stock_document_create", o.Document(w1, order.Id(), (a, 11, 1, null)));
        using var post = await Stock.SendPostAsync(s.Http, above.Id());
        var exceeded = await AssertParityAsync(post, await s.Mcp.CallAsync("stock_document_post", new { id = above.Id() }),
            "QUANTITY_EXCEEDS_ORDER", "lines[0].quantity");
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(exceeded));
        await Stock.AssertDraftAsync(s.Http, above.Id());

        async Task CreateAsync(Func<JsonObject> body, string code, params string[] keys)
        {
            using var http = await Stock.PostAsync(s.Http, body());
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_create", body()), code, keys);
        }

        await CreateAsync(() => o.Document(w1, closed.Id(), (a, 1, 1, null)), "ORDER_NOT_OPEN", o.LinkId);
        await CreateAsync(() => o.Document(s.S.W2, order.Id(), (a, 1, 1, null)), "ORDER_MISMATCH", "warehouseId");
        await CreateAsync(() => o.Document(w1, order.Id(), (s.S.B, 1, 1, null)), "ORDER_MISMATCH", "lines[0].articleId");
        await CreateAsync(() => o.Document(w1, order.Id(), (a, 1, 99, null)), "REFERENCE_NOT_FOUND", "lines[0].orderLineNo");
        await CreateAsync(() => o.Document(w1, Guid.CreateVersion7(), (a, 1, 1, null)), "REFERENCE_NOT_FOUND", o.LinkId);
        await CreateAsync(() => o.Document(w1, order.Id(), (a, 1, 1, null)).With("type", o.OtherTypes[0]), "VALIDATION_FAILED", o.LinkId);
        await CreateAsync(() => Stock.Draft(o.DocumentType, w1).With(o.LinkId, order.Id().ToString())
            .With("lines", new JsonArray(Stock.Line(a, 1))), "VALIDATION_FAILED", "lines[0].orderLineNo");
        // The link is not an argument of the update tool.
        using var put = await Stock.PutAsync(s.Http, above.Id(), OrderApi.DocumentReplacement(w1, (a, 1, 1, null)).With(o.LinkId, order.Id().ToString()));
        await AssertParityAsync(put, await s.Mcp.CallAsync("stock_document_update",
            OrderApi.DocumentReplacement(w1, (a, 1, 1, null)).With(o.LinkId, order.Id().ToString()).WithId(above.Id())), "VALIDATION_FAILED");
        Assert.Equal(1, (await o.DocumentsAsync(s.Http, order.Id())).Length);

        // The remedy of section 5: read the order, correct the document to what is outstanding, post.
        var outstanding = (await s.Mcp.OkAsync(o.Tool("get"), new { id = order.Id() })).OrderLines()[0].Outstanding();
        await s.Mcp.OkAsync("stock_document_update", OrderApi.DocumentReplacement(w1, (a, outstanding, 1, null)).WithId(above.Id()));
        await s.Mcp.OkAsync("stock_document_post", new { id = above.Id() });
        await o.AssertProgressAsync(s.Http, order.Id(), "confirmed", "full", (10m, 0m));
    }

    [Fact]
    public async Task AC88_Masters_on_an_order_are_in_use_through_the_tools()
    {
        await using var s = await AgentAsync();
        await o.OrderedAsync(s.S, s.S.W2, (s.S.B, 1, null, 1m));
        var draft = await o.DraftAsync(s.S, (s.S.A, 1, s.S.Box, 1m));
        var partner = o.PartnerOf(s.S).Id();

        using var http = await MasterApi.Partners.DeleteAsync(s.Http, partner);
        await AssertParityAsync(http, await s.Mcp.CallAsync("partner_delete", new { id = partner }), "IN_USE");
        await s.Mcp.ErrorAsync("warehouse_delete", new { id = s.S.W2 }, "IN_USE");
        await s.Mcp.ErrorAsync("article_delete", new { id = s.S.B }, "IN_USE");
        await s.Mcp.ErrorAsync("article_unit_delete", new { articleId = s.S.A, unitId = s.S.Box }, "IN_USE");
        await s.Mcp.ErrorAsync("article_update", Stock.ArticleBody(s.S.U.S.ArticleB).With("type", "service").WithId(s.S.B), "IN_USE", "type");

        await s.Mcp.OkAsync(o.Tool("delete"), new { id = draft.Id() });
        Assert.True((await s.Mcp.OkAsync("article_unit_delete", new { articleId = s.S.A, unitId = s.S.Box })).GetProperty("deleted").GetBoolean());
        await MasterApi.Partners.AssertUnchangedAsync(s.Http, o.PartnerOf(s.S));
    }
}
