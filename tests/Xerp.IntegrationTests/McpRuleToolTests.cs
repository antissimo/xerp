using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-110 to AC-114 (section 5): the five rule tools, the <c>rules</c> member of a tool error, parity
/// with HTTP, and an order without a partner through the tools.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpRuleToolTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;

    private sealed record Session(OrderSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await Orders.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-rules", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    /// <summary>The object without the named properties ("timestamps and ids aside", AC-113).</summary>
    private static JsonNode Without(JsonElement element, params string[] properties)
    {
        var node = JsonNode.Parse(element.GetRawText())!.AsObject();
        foreach (var property in properties)
            node.Remove(property);
        return node;
    }

    private static void AssertSame(JsonNode expected, JsonNode actual, string what) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{what}:\n{expected.ToJsonString()}\n!=\n{actual.ToJsonString()}");

    // ---- AC-110 ----

    [Fact]
    public async Task AC110_List_get_set_post_reset_and_history_through_tools_only()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);

        var list = await s.Mcp.OkAsync("rule_list", new { });

        Assert.Equal(6, list.Total());
        Assert.Equal(Rules.Keys, list.RuleKeys());
        McpAssert.JsonEqual(await Rules.ListAsync(s.Http), list, "rule_list differs from GET /rules");
        McpAssert.JsonEqual(await Rules.ListAsync(s.Http, "?group=sales&search=partner&source=default&limit=1&offset=0"),
            await s.Mcp.OkAsync("rule_list", new { group = "sales", search = "partner", source = "default", limit = 1, offset = 0 }));

        var one = await s.Mcp.OkAsync("rule_get", new { key = "sales.reservedStockProtected" });

        Rules.AssertAtDefault(one, Rules.Protected);
        McpAssert.JsonEqual(await Rules.GetAsync(s.Http, Rules.Protected), one);

        var set = await s.Mcp.OkAsync("rule_set", new { key = "stock.negativeStockAllowed", value = true });

        // Attribution as 003/R15: the key of the MCP request.
        Rules.AssertTenantValue(set, Rules.NegativeStock, true, s.Agent.Id);
        McpAssert.JsonEqual(await Rules.GetAsync(s.Http, Rules.NegativeStock), set);

        var issue = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("issue", w1, (a, 5)));
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = issue.Id() });

        Assert.Equal(("posted", "SI-000001"), (posted.Str("status"), posted.Number()));
        var onHand = Assert.Single((await s.Mcp.OkAsync("stock_on_hand_list", new { articleId = a, warehouseId = w1 })).Items());
        Assert.Equal((-5m, -5m), (onHand.Quantity(), onHand.Dec("availableQuantity")));

        var reset = await s.Mcp.OkAsync("rule_reset", new { key = "stock.negativeStockAllowed" });

        Rules.AssertAtDefault(reset, Rules.NegativeStock);
        Assert.Equal(s.Agent.Id, reset.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(await Rules.GetAsync(s.Http, Rules.NegativeStock), reset);

        var changes = await s.Mcp.OkAsync("rule_change_list", new { });

        Assert.Equal((2, 2), (changes.Total(), changes.Items().Length));
        Rules.AssertChange(changes.Items()[0], Rules.NegativeStock, "reset", true, false, s.Agent.Id);
        Rules.AssertChange(changes.Items()[1], Rules.NegativeStock, "set", false, true, s.Agent.Id);
        McpAssert.JsonEqual(await Rules.ChangesAsync(s.Http), changes, "rule_change_list differs from GET /rule-changes");
        McpAssert.JsonEqual(await Rules.ChangesAsync(s.Http, $"?key={Rules.NegativeStock}&limit=1&offset=1"),
            await s.Mcp.OkAsync("rule_change_list", new { key = Rules.NegativeStock, limit = 1, offset = 1 }));
        Assert.Equal(0, (await s.Mcp.OkAsync("rule_change_list", new { key = "nope" })).Total());
        await Rules.AssertBalancedAsync(s.Http);
    }

    // ---- AC-111 ----

    [Fact]
    public async Task AC111_Tool_errors_of_the_rule_tools()
    {
        await using var s = await AgentAsync();
        const string key = Rules.NegativeStock;
        // The tools exist: the errors below are about the arguments.
        Rules.AssertAtDefault(await s.Mcp.OkAsync("rule_get", new { key }), key);

        foreach (var value in new object?[] { "yes", "true", 1, 0, null, new[] { true }, new { } })
            Rules.AssertNamesNoRule(await s.Mcp.ErrorAsync("rule_set", new Dictionary<string, object?> { ["key"] = key, ["value"] = value },
                "VALIDATION_FAILED", "value"));
        await s.Mcp.ErrorAsync("rule_set", new { key }, "VALIDATION_FAILED", "value");
        await s.Mcp.ErrorAsync("rule_set", new { key, value = true, x = 1 }, "VALIDATION_FAILED", "x");

        // key is the addressing argument: missing, null or not a string -> VALIDATION_FAILED; no rule -> NOT_FOUND.
        await s.Mcp.ErrorAsync("rule_get", new { }, "VALIDATION_FAILED", "key");
        await s.Mcp.ErrorAsync("rule_get", new Dictionary<string, object?> { ["key"] = null }, "VALIDATION_FAILED", "key");
        await s.Mcp.ErrorAsync("rule_get", new { key = 5 }, "VALIDATION_FAILED", "key");
        await s.Mcp.ErrorAsync("rule_reset", new { }, "VALIDATION_FAILED", "key");
        await s.Mcp.ErrorAsync("rule_set", new { value = true }, "VALIDATION_FAILED", "key");
        await s.Mcp.ErrorAsync("rule_get", new { key, x = 1 }, "VALIDATION_FAILED", "x");
        Rules.AssertNamesNoRule(await s.Mcp.ErrorAsync("rule_get", new { key = "nope" }, "NOT_FOUND"));
        await s.Mcp.ErrorAsync("rule_get", new { key = "Stock.NegativeStockAllowed" }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("rule_set", new { key = "stock.negativeStock", value = true }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("rule_reset", new { key = "nope" }, "NOT_FOUND");
        await s.Mcp.ErrorAsync("rule_list", new { source = "mine" }, "VALIDATION_FAILED", "source");
        await s.Mcp.ErrorAsync("rule_list", new { limit = 0 }, "VALIDATION_FAILED", "limit");
        await s.Mcp.ErrorAsync("rule_change_list", new { foo = 1 }, "VALIDATION_FAILED", "foo");

        // Nothing changed.
        Assert.Equal(0, (await Rules.ChangesAsync(s.Http)).Total());
        Rules.AssertAtDefault(await Rules.GetAsync(s.Http, key), key);
    }

    // ---- AC-112 ----

    [Fact]
    public async Task AC112_A_tool_error_carries_rules_exactly_as_the_http_problem_does()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await Stock.ReceiveAsync(s.Http, w1, a, 10);

        // INSUFFICIENT_STOCK
        var aboveStock = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("issue", w1, (a, 11)));
        using (var http = await Stock.SendPostAsync(s.Http, aboveStock.Id()))
        {
            var problem = await Rules.InsufficientAsync(http, "lines[0].quantity");
            var error = await s.Mcp.ErrorAsync("stock_document_post", new { id = aboveStock.Id() }, "INSUFFICIENT_STOCK", "lines[0].quantity");
            Rules.AssertNames(error, Rules.NegativeStock, false, "lines[0].quantity");
            McpAssert.JsonEqual(problem.GetProperty("rules"), error.GetProperty("rules"), "rules of the tool error differ from HTTP");
            // Section 5: { code, detail, errors?, rules? }.
            Assert.Equal(new[] { "code", "detail", "errors", "rules" }, error.PropertyNames());
        }

        // The same for a reversal.
        var receipt = await Stock.ReceiveAsync(s.Http, w1, s.S.B, 3);
        await Stock.IssueAsync(s.Http, w1, s.S.B, 1);
        Rules.AssertNames(
            await s.Mcp.ErrorAsync("stock_document_reverse", new { id = receipt.Id(), documentDate = Stock.NextDay }, "INSUFFICIENT_STOCK", "lines[0].quantity"),
            Rules.NegativeStock, false, "lines[0].quantity");

        // QUANTITY_EXCEEDS_ORDER
        var order = await PO.OrderedAsync(s.S, (a, 10, null, 1m));
        var aboveOrder = await s.Mcp.OkAsync("stock_document_create", PO.Document(w1, order.Id(), (a, 11, 1, null)));
        Rules.AssertNames(
            await s.Mcp.ErrorAsync("stock_document_post", new { id = aboveOrder.Id() }, "QUANTITY_EXCEEDS_ORDER", "lines[0].quantity"),
            Rules.OverReceipt, false, "lines[0].quantity");

        // STOCK_RESERVED, with reserved stock protected.
        await s.Mcp.OkAsync("rule_set", new { key = Rules.Protected, value = true });
        await SO.OrderedAsync(s.S, (a, 8, null, 1m));
        var reserved = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("issue", w1, (a, 3)));
        using (var http = await Stock.SendPostAsync(s.Http, reserved.Id()))
        {
            var problem = await Rules.ReservedAsync(http, "lines[0].quantity");
            var error = await s.Mcp.ErrorAsync("stock_document_post", new { id = reserved.Id() }, "STOCK_RESERVED", "lines[0].quantity");
            Rules.AssertNames(error, Rules.Protected, true, "lines[0].quantity");
            McpAssert.JsonEqual(problem.GetProperty("rules"), error.GetProperty("rules"), "rules of the tool error differ from HTTP");
        }

        // VALIDATION_FAILED for an order without a partner.
        foreach (var o in new[] { PO, SO })
        {
            var error = await s.Mcp.ErrorAsync(o.Tool("create"), o.NoPartner(s.S, (a, 1, null, 1m)), "VALIDATION_FAILED", o.PartnerId);
            Rules.AssertNames(error, Rules.PartnerRule(o), true, o.PartnerId);
            Rules.AssertNames(
                await s.Mcp.ErrorAsync(o.Tool("create"), o.Body(s.S, (a, 1, null, 1m)).With(o.PartnerId, null), "VALIDATION_FAILED", o.PartnerId),
                Rules.PartnerRule(o), true, o.PartnerId);
            using var http = await o.PostAsync(s.Http, o.NoPartner(s.S, (a, 1, null, 1m)));
            McpAssert.JsonEqual((await Rules.PartnerRequiredAsync(http, o)).GetProperty("rules"), error.GetProperty("rules"));
        }

        // A tool error no rule caused has no rules member.
        Rules.AssertNamesNoRule(await s.Mcp.ErrorAsync("stock_document_post", new { id = receipt.Id() }, "INVALID_STATE"));
        Rules.AssertNamesNoRule(await s.Mcp.ErrorAsync("stock_document_create", Stock.Draft("receipt", w1, (a, -1)), "VALIDATION_FAILED", "lines[0].quantity"));
        Rules.AssertNamesNoRule(await s.Mcp.ErrorAsync(PO.Tool("create"), PO.Body(s.S.Cus.Id(), w1, (a, 1, null, 1m)), "PARTNER_ROLE_MISSING", "supplierId"));
        await Rules.AssertBalancedAsync(s.Http);
    }

    // ---- AC-113 ----

    [Fact]
    public async Task AC113_A_Set_a_reset_and_a_refused_posting_are_the_same_over_http_and_through_the_tool()
    {
        // Two tenants in the same state: one is changed over HTTP, the other through the tools.
        await using var tool = await AgentAsync();
        var http = await Orders.SetupAsync(app);
        string[] aside = ["updatedAt", "updatedBy"];

        var setByTool = await tool.Mcp.OkAsync("rule_set", new { key = Rules.OverReceipt, value = true });
        var setOverHttp = await Rules.SetAsync(http.Http, Rules.OverReceipt, true);

        AssertSame(Without(setOverHttp, aside), Without(setByTool, aside), "The Set differs between HTTP and the tool");
        Assert.Equal(setOverHttp.PropertyNames(), setByTool.PropertyNames());

        var resetByTool = await tool.Mcp.OkAsync("rule_reset", new { key = Rules.OverReceipt });
        var resetOverHttp = await Rules.ResetAsync(http.Http, Rules.OverReceipt);

        AssertSame(Without(resetOverHttp, aside), Without(resetByTool, aside), "The reset differs between HTTP and the tool");
        Assert.Equal(resetOverHttp.PropertyNames(), resetByTool.PropertyNames());

        string[] changeAside = ["id", "changedAt", "changedBy"];
        var historyByTool = (await tool.Mcp.OkAsync("rule_change_list", new { })).Items();
        var historyOverHttp = (await Rules.ChangesAsync(http.Http)).Items();
        Assert.Equal(2, historyByTool.Length);
        foreach (var (overHttp, byTool) in historyOverHttp.Zip(historyByTool))
            AssertSame(Without(overHttp, changeAside), Without(byTool, changeAside), "A change differs between HTTP and the tool");

        // A refused posting: the same draft, refused over HTTP and through the tool.
        var draft = await Stock.CreateAsync(tool.Http, "issue", tool.S.W1, (tool.S.A, 1), (tool.S.B, 2));
        using var response = await Stock.SendPostAsync(tool.Http, draft.Id());
        var problem = await Rules.InsufficientAsync(response, "lines[0].quantity", "lines[1].quantity");
        var error = await tool.Mcp.ErrorAsync("stock_document_post", new { id = draft.Id() }, "INSUFFICIENT_STOCK");

        foreach (var member in new[] { "code", "detail", "errors", "rules" })
            McpAssert.JsonEqual(problem.GetProperty(member), error.GetProperty(member), $"'{member}' differs between the problem and the tool error");
    }

    // ---- AC-114 ----

    private static string[] Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(r => r.GetString()!).ToArray() : [];

    private static async Task<Dictionary<string, JsonElement>> InputSchemasAsync(McpConnection mcp) =>
        (await mcp.Client.ListToolsAsync()).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema.Clone());

    [Fact]
    public async Task AC114_An_order_without_a_partner_through_the_tools_and_a_schema_that_does_not_depend_on_the_rule()
    {
        await using var s = await AgentAsync();
        var a = s.S.A;
        var before = await InputSchemasAsync(s.Mcp);

        foreach (var o in new[] { PO, SO })
        {
            await s.Mcp.OkAsync("rule_set", new { key = Rules.PartnerRule(o), value = false });

            var created = await s.Mcp.OkAsync(o.Tool("create"), o.NoPartner(s.S, (a, 10, null, 2.5m)));
            var asNull = await s.Mcp.OkAsync(o.Tool("create"), o.Body(s.S, (a, 1, null, 1m)).With(o.PartnerId, null));

            Rules.AssertNoPartner(o, created);
            Rules.AssertNoPartner(o, asNull);
            McpAssert.JsonEqual(await o.GetAsync(s.Http, created.Id()), created);
            McpAssert.JsonEqual(created, await s.Mcp.OkAsync(o.Tool("get"), new { id = created.Id() }));
            McpAssert.JsonEqual(await o.ListAsync(s.Http), await s.Mcp.OkAsync(o.Tool("list"), new { }));

            // Update: the argument must be present (replace carries every field) and may be null.
            var withoutArgument = await s.Mcp.ErrorAsync(o.Tool("update"),
                o.Replacement(s.S, (a, 10, null, 2.5m)).Without(o.PartnerId).WithId(created.Id()), "VALIDATION_FAILED", o.PartnerId);
            Rules.AssertNamesNoRule(withoutArgument);
            var named = await s.Mcp.OkAsync(o.Tool("update"), o.Replacement(s.S, (a, 10, null, 2.5m)).WithId(created.Id()));
            Assert.Equal(o.PartnerOf(s.S).Id(), named.GetProperty(o.Partner).Id());
            var removed = await s.Mcp.OkAsync(o.Tool("update"), o.NoPartnerReplacement(s.S, (a, 10, null, 2.5m)).WithId(created.Id()));
            Rules.AssertNoPartner(o, removed);

            var confirmed = await s.Mcp.OkAsync(o.Tool("confirm"), new { id = created.Id() });
            Assert.Equal(o.Number(1), confirmed.Number());
            Rules.AssertNoPartner(o, confirmed);
        }

        // A receipt against the purchase order without a supplier, through the tools: it has no partner.
        var order = await PO.GetAsync(s.Http, (await PO.ListAsync(s.Http, "?status=confirmed")).Items()[0].Id());
        var receipt = await s.Mcp.OkAsync("stock_document_create", PO.Document(s.S.W1, order.Id(), (a, 10, 1, null)));
        JsonBody.AssertNull(receipt, "partner");
        JsonBody.AssertNull(await s.Mcp.OkAsync("stock_document_post", new { id = receipt.Id() }), "partner");
        await s.Mcp.ErrorAsync("stock_document_create",
            PO.Document(s.S.W1, order.Id(), (a, 1, 1, null)).With("partnerId", s.S.Sup.Id().ToString()), "ORDER_MISMATCH", "partnerId");

        // The schema is the same for every tenant, whatever the rules' values (architecture §11).
        var after = await InputSchemasAsync(s.Mcp);
        foreach (var o in new[] { PO, SO })
        {
            foreach (var schemas in new[] { before, after })
            {
                Assert.DoesNotContain(o.PartnerId, Required(schemas[o.Tool("create")]));
                Assert.Contains(o.PartnerId, Required(schemas[o.Tool("update")]));
                Assert.True(schemas[o.Tool("create")].GetProperty("properties").TryGetProperty(o.PartnerId, out _), $"{o.Tool("create")} has no {o.PartnerId}.");
            }
            McpAssert.JsonEqual(before[o.Tool("create")], after[o.Tool("create")], $"The input schema of {o.Tool("create")} changed with the rule");
            McpAssert.JsonEqual(before[o.Tool("update")], after[o.Tool("update")], $"The input schema of {o.Tool("update")} changed with the rule");
        }
        Assert.Equal(62, after.Count);
        await Rules.AssertBalancedAsync(s.Http);
    }
}
