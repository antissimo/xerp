using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 007, AC-81 and AC-82: conversions and lines with a unit through tools apply the same rules as HTTP and
/// return what HTTP returns. The MCP client uses its own <c>agent</c> key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpArticleUnitToolTests(XerpFixture app)
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
    public async Task AC81_Conversions_and_a_line_in_a_unit_through_tools_only()
    {
        await using var s = await AgentAsync();
        var (a, pack) = (s.S.A, s.S.Pack);

        var created = await s.Mcp.OkAsync("article_unit_set", new { articleId = a, unitId = pack, factor = 6 });

        Assert.Equal(6m, created.Factor());
        Assert.Equal((a, pack), (created.GetProperty("article").Id(), created.GetProperty("unit").Id()));
        Assert.Equal("pcs", created.GetProperty("baseUnit").Str("code"));
        Assert.Equal(s.Agent.Id, created.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await Units.GetAsync(s.Http, a, pack), created);

        var replaced = await s.Mcp.OkAsync("article_unit_set", new { articleId = a, unitId = pack, factor = 8 });

        Assert.Equal(8m, replaced.Factor());
        Assert.Equal(created.Str("createdAt"), replaced.Str("createdAt"));
        McpAssert.JsonEqual(await Units.GetAsync(s.Http, a, pack), replaced);

        var got = await s.Mcp.OkAsync("article_unit_get", new { articleId = a, unitId = pack });
        var list = await s.Mcp.OkAsync("article_unit_list", new { articleId = a });
        var page = await s.Mcp.OkAsync("article_unit_list", new { articleId = a, limit = 1, offset = 1 });

        McpAssert.JsonEqual(await Units.GetAsync(s.Http, a, pack), got);
        Assert.Equal(new[] { "box", "pack" }, list.UnitCodes());
        McpAssert.JsonEqual(await Units.ListAsync(s.Http, a), list);
        McpAssert.JsonEqual(await Units.ListAsync(s.Http, a, "?limit=1&offset=1"), page);

        var draft = await s.Mcp.OkAsync("stock_document_create", Units.Draft("receipt", s.S.W1, (a, 2.5m, pack), (a, 1, null)));

        Units.AssertLine(draft.DocumentLines()[0], "pack", 2.5m, 8m, 20m);
        Units.AssertLine(draft.DocumentLines()[1], "pcs", 1m, 1m, 1m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), draft);

        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });

        Assert.Equal("posted", posted.Str("status"));
        Units.AssertLine(posted.DocumentLines()[0], "pack", 2.5m, 8m, 20m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), posted);
        var ledger = await s.Mcp.OkAsync("stock_ledger_entry_list", new { documentId = draft.Id() });
        Assert.Equal(new[] { 20m, 1m }, ledger.Items().Select(e => e.Quantity()).ToArray());
        Assert.All(ledger.Items(), e => Assert.Equal("pcs", e.GetProperty("unit").Str("code")));
        McpAssert.JsonEqual(await Stock.LedgerAsync(s.Http, $"?documentId={draft.Id()}"), ledger);
        McpAssert.JsonEqual(await Stock.OnHandAsync(s.Http), await s.Mcp.OkAsync("stock_on_hand_list"));
        Assert.Equal(21m, await Stock.QuantityAsync(s.Http, a, s.S.W1));

        var deleted = await s.Mcp.OkAsync("article_unit_delete", new { articleId = a, unitId = pack });

        Assert.True(deleted.Bool("deleted"));
        Assert.Equal(new[] { "deleted" }, deleted.PropertyNames());
        using var gone = await Units.SendGetAsync(s.Http, a, pack);
        await HttpAssert.NotFoundAsync(gone);
        await s.Mcp.ErrorAsync("article_unit_get", new { articleId = a, unitId = pack }, "NOT_FOUND");
        // The posted line keeps its factor, also as the tool shows it.
        var after = await s.Mcp.OkAsync("stock_document_get", new { id = draft.Id() });
        Units.AssertLine(after.DocumentLines()[0], "pack", 2.5m, 8m, 20m);
    }

    [Fact]
    public async Task AC81_Update_through_the_tool_changes_the_unit_of_a_line_and_the_article_list_filters_by_alternative_unit()
    {
        await using var s = await AgentAsync();
        var draft = await Units.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 5, null));

        var inBoxes = await s.Mcp.OkAsync("stock_document_update", Units.Replacement(s.S.W1, (s.S.A, 5, s.S.Box)).WithId(draft.Id()));
        Units.AssertLine(inBoxes.DocumentLines()[0], "box", 5m, 12m, 60m);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), inBoxes);

        var body = Units.Replacement(s.S.W1, (s.S.A, 5, null)).WithId(draft.Id());
        body["lines"]![0]!.AsObject()["unitId"] = null;
        var inBase = await s.Mcp.OkAsync("stock_document_update", body);
        Units.AssertLine(inBase.DocumentLines()[0], "pcs", 5m, 1m, 5m);

        var articles = await s.Mcp.OkAsync("article_list", new { alternativeUnitId = s.S.Box });
        Assert.Equal(new[] { "A" }, articles.Codes());
        McpAssert.JsonEqual(await Art.ListAsync(s.Http, $"?alternativeUnitId={s.S.Box}"), articles);
    }

    [Fact]
    public async Task AC81_A_factor_changed_through_the_tool_changes_drafts_and_not_posted_lines()
    {
        await using var s = await AgentAsync();
        var posted = await Units.PostedAsync(s.Http, "receipt", s.S.W1, (s.S.A, 5, s.S.Box));
        var draft = await Units.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 5, s.S.Box));

        await s.Mcp.OkAsync("article_unit_set", new { articleId = s.S.A, unitId = s.S.Box, factor = 10 });

        Units.AssertLine((await s.Mcp.OkAsync("stock_document_get", new { id = draft.Id() })).DocumentLines()[0], "box", 5m, 10m, 50m);
        Units.AssertLine((await s.Mcp.OkAsync("stock_document_get", new { id = posted.Id() })).DocumentLines()[0], "box", 5m, 12m, 60m);
        var reversing = await s.Mcp.OkAsync("stock_document_reverse", new { id = posted.Id(), documentDate = Stock.Date });
        Units.AssertLine(reversing.DocumentLines()[0], "box", 5m, 12m, 60m);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_Set_errors_are_the_same_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, pack, pcs) = (s.S.A, s.S.Pack, s.S.Pcs);
        var (randomUnit, randomArticle) = (Guid.NewGuid(), Guid.NewGuid());
        var inactive = await Uom.CreateAsync(s.Http, "bag", "Bag", isActive: false);

        using (var http = await Units.PutAsync(s.Http, a, pack, 0))
            await AssertParityAsync(http, await s.Mcp.CallAsync("article_unit_set", new { articleId = a, unitId = pack, factor = 0 }),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED", "factor");
        using (var http = await Units.PutAsync(s.Http, a, pcs, 6))
            await AssertParityAsync(http, await s.Mcp.CallAsync("article_unit_set", new { articleId = a, unitId = pcs, factor = 6 }),
                HttpStatusCode.Conflict, "UNIT_IS_BASE_UNIT", "unitId");
        using (var http = await Units.PutAsync(s.Http, a, randomUnit, 6))
            await AssertParityAsync(http, await s.Mcp.CallAsync("article_unit_set", new { articleId = a, unitId = randomUnit, factor = 6 }),
                HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND", "unitId");
        using (var http = await Units.PutAsync(s.Http, a, inactive.Id(), 6))
            await AssertParityAsync(http, await s.Mcp.CallAsync("article_unit_set", new { articleId = a, unitId = inactive.Id(), factor = 6 }),
                HttpStatusCode.Conflict, "REFERENCE_INACTIVE", "unitId");
        using (var http = await Units.PutAsync(s.Http, randomArticle, pack, 6))
            await AssertParityAsync(http, await s.Mcp.CallAsync("article_unit_set", new { articleId = randomArticle, unitId = pack, factor = 6 }),
                HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(new[] { "box" }, (await Units.ListAsync(s.Http, a)).UnitCodes());
    }

    [Fact]
    public async Task AC82_Addressing_arguments_are_validated_under_their_own_names()
    {
        // Section 5: 003/R15 applies to articleId and unitId each under its own name.
        await using var s = await AgentAsync();
        var (a, box) = (s.S.A, s.S.Box);

        await s.Mcp.ErrorAsync("article_unit_set", new { articleId = a, factor = 6 }, "VALIDATION_FAILED", "unitId");
        await s.Mcp.ErrorAsync("article_unit_set", new { unitId = box, factor = 6 }, "VALIDATION_FAILED", "articleId");
        await s.Mcp.ErrorAsync("article_unit_set", new { articleId = a, unitId = box }, "VALIDATION_FAILED", "factor");
        await s.Mcp.ErrorAsync("article_unit_get", new { articleId = a }, "VALIDATION_FAILED", "unitId");
        await s.Mcp.ErrorAsync("article_unit_get", new { articleId = "", unitId = box }, "VALIDATION_FAILED", "articleId");
        await s.Mcp.ErrorAsync("article_unit_delete", new { unitId = box }, "VALIDATION_FAILED", "articleId");
        await s.Mcp.ErrorAsync("article_unit_list", new { }, "VALIDATION_FAILED", "articleId");
        await s.Mcp.ErrorAsync("article_unit_list", new { articleId = a, limit = 0 }, "VALIDATION_FAILED", "limit");
        await s.Mcp.ErrorAsync("article_unit_list", new { articleId = Guid.NewGuid() }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("article_unit_get", new { articleId = a, unitId = s.S.Pack }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("article_unit_delete", new { articleId = a, unitId = s.S.Pack }, "NOT_FOUND");
        // A string that is not a UUID is NOT_FOUND without errors keys, as the malformed path segment over HTTP
        // (003/R15, AC-24). Neither id is a reference argument, also not unitId of article_unit_set (007-q B-D1).
        foreach (var (tool, arguments) in new (string, object)[]
                 {
                     ("article_unit_set", new { articleId = a, unitId = "abc", factor = 6 }),
                     ("article_unit_set", new { articleId = "abc", unitId = box, factor = 6 }),
                     ("article_unit_get", new { articleId = a, unitId = "abc" }),
                     ("article_unit_get", new { articleId = "abc", unitId = box }),
                     ("article_unit_delete", new { articleId = a, unitId = "abc" }),
                     ("article_unit_delete", new { articleId = "abc", unitId = box }),
                     ("article_unit_list", new { articleId = "abc" }),
                 })
            Assert.Empty(McpAssert.ErrorKeys(await s.Mcp.ErrorAsync(tool, arguments, "NOT_FOUND")));

        Assert.Equal(12m, (await Units.GetAsync(s.Http, a, box)).Factor());
    }

    [Fact]
    public async Task AC82_In_use_errors_are_the_same_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, box) = (s.S.A, s.S.Box);
        await Units.CreateAsync(s.Http, "receipt", s.S.W1, (a, 1, box));
        var article = await Art.GetAsync(s.Http, a);

        using (var http = await Units.DeleteAsync(s.Http, a, box))
            await AssertParityAsync(http, await s.Mcp.CallAsync("article_unit_delete", new { articleId = a, unitId = box }),
                HttpStatusCode.Conflict, "IN_USE");
        using (var http = await Units.DeleteUnitAsync(s.Http, box))
            await AssertParityAsync(http, await s.Mcp.CallAsync("uom_delete", new { id = box }), HttpStatusCode.Conflict, "IN_USE");
        // The base unit of an article with conversions (B gets one; it has no stock documents).
        await Units.SetAsync(s.Http, s.S.B, box, 50);
        var b = await Art.GetAsync(s.Http, s.S.B);
        using (var http = await Stock.PutArticleAsync(s.Http, s.S.B, Stock.ArticleBody(b).With("baseUnitId", s.S.Pack.ToString())))
            await AssertParityAsync(http,
                await s.Mcp.CallAsync("article_update", Stock.ArticleBody(b).With("baseUnitId", s.S.Pack.ToString()).WithId(s.S.B)),
                HttpStatusCode.Conflict, "IN_USE", "baseUnitId");

        Assert.Equal(12m, (await Units.GetAsync(s.Http, a, box)).Factor());
        McpAssert.JsonEqual(article, await Art.GetAsync(s.Http, a));
        McpAssert.JsonEqual(b, await Art.GetAsync(s.Http, s.S.B));
    }

    [Fact]
    public async Task AC82_Line_unit_errors_are_the_same_as_over_http()
    {
        await using var s = await AgentAsync();
        var (a, b, w1) = (s.S.A, s.S.B, s.S.W1);
        await Units.SetAsync(s.Http, a, s.S.Pack, 0.4m);

        async Task ParityAsync(Func<System.Text.Json.Nodes.JsonObject> Body, string code, string key)
        {
            using var http = await Stock.PostAsync(s.Http, Body());
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_create", Body()), HttpStatusCode.Conflict, code, key);
        }

        await ParityAsync(() => Units.Draft("receipt", w1, (b, 1, s.S.Box)), "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await ParityAsync(() => Units.Draft("receipt", w1, (a, 1, null), (a, 1, Guid.Parse("0199c0de-0000-7000-8000-00000000abcd"))),
            "REFERENCE_NOT_FOUND", "lines[1].unitId");
        await ParityAsync(() => Units.Draft("receipt", w1, (a, 0.000001m, s.S.Pack)), "QUANTITY_NOT_CONVERTIBLE", "lines[0].quantity");

        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());

        // Posting: not convertible any more, and insufficient stock counted in base units.
        await Units.SetAsync(s.Http, a, s.S.Pack, 0.5m);
        var first = await Units.CreateAsync(s.Http, "receipt", w1, (a, 0.000002m, s.S.Pack));
        var second = await Units.CreateAsync(s.Http, "receipt", w1, (a, 0.000002m, s.S.Pack));
        await Units.SetAsync(s.Http, a, s.S.Pack, 0.2m);
        using (var http = await Stock.SendPostAsync(s.Http, first.Id()))
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_post", new { id = second.Id() }),
                HttpStatusCode.Conflict, "QUANTITY_NOT_CONVERTIBLE", "lines[0].quantity");
        await Stock.ReceiveAsync(s.Http, w1, a, 30);
        var issue = await Units.CreateAsync(s.Http, "issue", w1, (a, 3, s.S.Box));
        using (var http = await Stock.SendPostAsync(s.Http, issue.Id()))
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_post", new { id = issue.Id() }),
                HttpStatusCode.Conflict, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, a, w1));
    }
}
