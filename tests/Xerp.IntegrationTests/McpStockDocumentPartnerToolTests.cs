using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011a, AC-80 to AC-83: the stock document tools take and return the partner by the same rules as HTTP
/// and with the same error codes and keys; <c>partner_delete</c> knows the further <c>IN_USE</c> case. The
/// MCP client uses its own <c>agent</c> key.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpStockDocumentPartnerToolTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;

    private sealed record Session(PartnerSetup S, TestKey Agent, McpConnection Mcp) : IAsyncDisposable
    {
        /// <summary>HTTP client with the tenant's first key (not the MCP key).</summary>
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await PartnerDocs.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-partner", "agent");
        return new Session(setup, agent, await app.McpAsync(agent.Key));
    }

    /// <summary>The same refused input over both surfaces: same code, exactly the same <c>errors</c> keys.</summary>
    private static async Task AssertParityAsync(HttpResponseMessage http, CallToolResult tool, string code, params string[] errorKeys)
    {
        var status = code == "VALIDATION_FAILED" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict;
        var problem = await HttpAssert.ProblemAsync(http, status, code);
        var error = McpAssert.Error(tool, code, errorKeys);
        Assert.Equal(errorKeys.Order(StringComparer.Ordinal).ToArray(), McpAssert.ErrorKeys(problem));
        Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(error));
    }

    // ---- AC-80 ----

    [Fact]
    public async Task AC80_Create_with_partnerId_returns_the_partner_and_equals_http()
    {
        await using var s = await AgentAsync();

        var receipt = await s.Mcp.OkAsync("stock_document_create", PartnerDocs.Receipt(s.S, s.S.Sup, 5));

        PartnerDocs.AssertPartner(receipt, s.S.O.Sup);
        Assert.Equal(s.Agent.Id, receipt.GetProperty("createdBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, receipt.Id()), receipt, "stock_document_create differs from HTTP");
        McpAssert.JsonEqual(receipt, await s.Mcp.OkAsync("stock_document_get", new { id = receipt.Id() }), "stock_document_get differs");

        // Without partnerId and with null: no partner; both roles fit an issue.
        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_create", Stock.Draft("receipt", s.S.W1, (s.S.A, 1))));
        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_create", PartnerDocs.Body(s.S, "receipt", null)));
        PartnerDocs.AssertPartner(await s.Mcp.OkAsync("stock_document_create", PartnerDocs.Issue(s.S, s.S.Both)), s.S.BothPartner);

        // Posted and reversed through the tools, the partner stays.
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = receipt.Id() });
        PartnerDocs.AssertPartner(posted, s.S.O.Sup);
        var reversing = await s.Mcp.OkAsync("stock_document_reverse", new { id = receipt.Id(), documentDate = PartnerDocs.Date });
        PartnerDocs.AssertPartner(reversing, s.S.O.Sup);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, reversing.Id()), reversing, "stock_document_reverse differs from HTTP");
        await s.Mcp.ErrorAsync("stock_document_reverse",
            new { id = posted.Id(), documentDate = PartnerDocs.Date, partnerId = s.S.Sup }, "VALIDATION_FAILED", "partnerId");
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    [Fact]
    public async Task AC80_Create_is_refused_with_the_code_and_keys_of_http()
    {
        await using var s = await AgentAsync();
        var inactive = await Orders.PartnerAsync(s.Http, "GONE", "Inactive partner", isSupplier: true, isCustomer: true, isActive: false);
        var order = await PO.OrderedAsync(s.S.O, (s.S.A, 10, null, 1m));

        async Task RefusedAsync(JsonObject body, string code, params string[] keys)
        {
            using var http = await Stock.PostAsync(s.Http, body.DeepClone().AsObject());
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_create", body), code, keys);
        }

        await RefusedAsync(Stock.Transfer(s.S.W1, s.S.W2, (s.S.A, 1)).With("partnerId", s.S.Sup.ToString()), "VALIDATION_FAILED", "partnerId");
        await RefusedAsync(Counts.Draft(s.S.W1, (s.S.A, 1, null)).With("partnerId", s.S.Sup.ToString()), "VALIDATION_FAILED", "partnerId");
        await RefusedAsync(PartnerDocs.Body(s.S, "receipt", "abc"), "VALIDATION_FAILED", "partnerId");
        await RefusedAsync(PartnerDocs.Receipt(s.S, s.S.Cus), "PARTNER_ROLE_MISSING", "partnerId");
        await RefusedAsync(PartnerDocs.Issue(s.S, s.S.Sup), "PARTNER_ROLE_MISSING", "partnerId");
        await RefusedAsync(PartnerDocs.Receipt(s.S, Guid.NewGuid()), "REFERENCE_NOT_FOUND", "partnerId");
        await RefusedAsync(PartnerDocs.Receipt(s.S, inactive.Id()), "REFERENCE_INACTIVE", "partnerId");
        await RefusedAsync(PO.Document(s.S.W1, order.Id(), (s.S.A, 1, 1, null)).With("partnerId", s.S.Sup2.ToString()), "ORDER_MISMATCH", "partnerId");
        await RefusedAsync(PO.Document(s.S.W2, order.Id(), (s.S.A, 1, 1, null)).With("partnerId", s.S.Sup2.ToString()),
            "ORDER_MISMATCH", "partnerId", "warehouseId");
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 0);

        // A transfer and a count with null are created; a linked receipt takes the order's supplier.
        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_create", Stock.Transfer(s.S.W1, s.S.W2, (s.S.A, 1)).With("partnerId", null)));
        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_create", Counts.Draft(s.S.W1, (s.S.A, 1, null))));
        var linked = await s.Mcp.OkAsync("stock_document_create", PO.Document(s.S.W1, order.Id(), (s.S.A, 1, 1, null)));
        PartnerDocs.AssertPartner(linked, s.S.O.Sup);
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, linked.Id()), linked, "The linked receipt differs from HTTP");
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_Update_of_a_receipt_must_send_partnerId_and_null_removes_the_partner()
    {
        await using var s = await AgentAsync();
        var draft = await PartnerDocs.ReceiptAsync(s.S, s.S.Sup);
        var issue = await PartnerDocs.IssueAsync(s.S, s.S.Cus);

        using (var http = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s.S, s.S.Sup2).Without("partnerId")))
            await AssertParityAsync(http,
                await s.Mcp.CallAsync("stock_document_update", PartnerDocs.Replacement(s.S, s.S.Sup2).Without("partnerId").WithId(draft.Id())),
                "VALIDATION_FAILED", "partnerId");
        await s.Mcp.ErrorAsync("stock_document_update", PartnerDocs.Replacement(s.S, null).Without("partnerId").WithId(issue.Id()),
            "VALIDATION_FAILED", "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await Stock.AssertUnchangedAsync(s.Http, issue);

        var changed = await s.Mcp.OkAsync("stock_document_update", PartnerDocs.Replacement(s.S, s.S.Sup2).WithId(draft.Id()));
        PartnerDocs.AssertPartner(changed, s.S.Sup2Partner);
        Assert.Equal(s.Agent.Id, changed.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(await Stock.GetAsync(s.Http, draft.Id()), changed, "stock_document_update differs from HTTP");

        using (var http = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s.S, s.S.Cus)))
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_update", PartnerDocs.Replacement(s.S, s.S.Cus).WithId(draft.Id())),
                "PARTNER_ROLE_MISSING", "partnerId");

        var removed = await s.Mcp.OkAsync("stock_document_update", PartnerDocs.Replacement(s.S, null).WithId(draft.Id()));
        PartnerDocs.AssertNoPartner(removed);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, draft.Id(), null);
    }

    [Fact]
    public async Task AC81_Update_of_a_transfer_or_a_count_needs_no_partnerId_and_a_linked_draft_keeps_the_orders()
    {
        await using var s = await AgentAsync();
        var transfer = await Stock.CreateTransferAsync(s.Http, s.S.W1, s.S.W2, (s.S.A, 1));
        var count = await Counts.CountAsync(s.Http, s.S.W1, s.S.A, 1);
        var order = await PO.OrderedAsync(s.S.O, (s.S.A, 10, null, 1m));
        var linked = await PO.DraftDocumentAsync(s.Http, order, 1, 1);

        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_update",
            Stock.TransferReplacement(s.S.W1, s.S.W2, (s.S.A, 2)).WithId(transfer.Id())));
        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_update", Counts.SameValues(count).WithId(count.Id())));
        await s.Mcp.ErrorAsync("stock_document_update",
            Stock.TransferReplacement(s.S.W1, s.S.W2, (s.S.A, 2)).With("partnerId", s.S.Sup.ToString()).WithId(transfer.Id()),
            "VALIDATION_FAILED", "partnerId");

        var kept = await s.Mcp.OkAsync("stock_document_update", OrderApi.DocumentReplacement(s.S.W1, (s.S.A, 2, 1, null)).WithId(linked.Id()));
        PartnerDocs.AssertPartner(kept, s.S.O.Sup);
        await s.Mcp.ErrorAsync("stock_document_update",
            OrderApi.DocumentReplacement(s.S.W1, (s.S.A, 2, 1, null)).With("partnerId", s.S.Sup2.ToString()).WithId(linked.Id()),
            "ORDER_MISMATCH", "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, kept);
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_List_with_partnerId_returns_what_the_http_filter_returns()
    {
        await using var s = await AgentAsync();
        await PartnerDocs.ReceiptAsync(s.S, s.S.Sup);
        await PartnerDocs.PostedReceiptAsync(s.S, s.S.Sup2, 20);
        var order = await PO.OrderedAsync(s.S.O, (s.S.A, 10, null, 1m));
        await PO.FulfilAsync(s.Http, order, 1, 4);
        await Stock.CreateAsync(s.Http, "receipt", s.S.W1, (s.S.A, 1));
        await PartnerDocs.IssueAsync(s.S, s.S.Cus);

        var ofSup = await s.Mcp.OkAsync("stock_document_list", new { partnerId = s.S.Sup });
        Assert.Equal(2, ofSup.Total());
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, $"?partnerId={s.S.Sup}"), ofSup, "stock_document_list differs from HTTP");
        Assert.All(ofSup.Items(), d => PartnerDocs.AssertPartner(d, s.S.O.Sup));
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, $"?partnerId={s.S.Sup}&status=posted"),
            await s.Mcp.OkAsync("stock_document_list", new { partnerId = s.S.Sup, status = "posted" }), "The combined filter differs from HTTP");
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http, $"?partnerId={s.S.Cus}"),
            await s.Mcp.OkAsync("stock_document_list", new { partnerId = s.S.Cus }), "The customer's documents differ from HTTP");
        McpAssert.JsonEqual(await Stock.DocumentsAsync(s.Http),
            await s.Mcp.OkAsync("stock_document_list"), "The unfiltered list differs from HTTP");
        Assert.Equal(0, (await s.Mcp.OkAsync("stock_document_list", new { partnerId = s.S.Sup, type = "issue" })).Total());
        Assert.Equal(0, (await s.Mcp.OkAsync("stock_document_list", new { partnerId = Guid.NewGuid() })).Total());
        await s.Mcp.ErrorAsync("stock_document_list", new { partnerId = "not-a-uuid" }, "VALIDATION_FAILED", "partnerId");
    }

    [Fact]
    public async Task AC82_Post_of_a_draft_whose_partner_was_deactivated_or_lost_the_role_is_refused_as_over_http()
    {
        await using var s = await AgentAsync();
        var draft = await PartnerDocs.ReceiptAsync(s.S, s.S.Sup, 5);
        var ofBoth = await PartnerDocs.ReceiptAsync(s.S, s.S.Both, 3);
        await Orders.SetPartnerActiveAsync(s.Http, s.S.Sup, false);
        await PO.RemoveRoleAsync(s.Http, s.S.Both);

        using (var http = await Stock.SendPostAsync(s.Http, draft.Id()))
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_post", new { id = draft.Id() }), "REFERENCE_INACTIVE", "partnerId");
        using (var http = await Stock.SendPostAsync(s.Http, ofBoth.Id()))
            await AssertParityAsync(http, await s.Mcp.CallAsync("stock_document_post", new { id = ofBoth.Id() }), "PARTNER_ROLE_MISSING", "partnerId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        await Stock.AssertDraftAsync(s.Http, ofBoth.Id());
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());

        // The remedy an agent has: reactivate the partner, or remove it from the document.
        await Orders.SetPartnerActiveAsync(s.Http, s.S.Sup, true);
        var posted = await s.Mcp.OkAsync("stock_document_post", new { id = draft.Id() });
        Assert.Equal(("posted", PO.DocumentNumber(1)), (posted.Str("status"), posted.Number()));
        Assert.Equal(s.Agent.Id, posted.GetProperty("postedBy").GetGuid());
        await s.Mcp.OkAsync("stock_document_update", PartnerDocs.Replacement(s.S, null, 3).WithId(ofBoth.Id()));
        PartnerDocs.AssertNoPartner(await s.Mcp.OkAsync("stock_document_post", new { id = ofBoth.Id() }));
        Assert.Equal(8m, await Stock.QuantityAsync(s.Http, s.S.A, s.S.W1));
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    // ---- AC-83 ----

    [Fact]
    public async Task AC83_Partner_delete_of_a_partner_named_by_a_stock_document_is_IN_USE()
    {
        await using var s = await AgentAsync();
        var draft = await PartnerDocs.ReceiptAsync(s.S, s.S.Sup2);
        await PartnerDocs.PostedReceiptAsync(s.S, s.S.Both, 2);

        await s.Mcp.ErrorAsync("partner_delete", new { id = s.S.Sup2 }, "IN_USE");
        await s.Mcp.ErrorAsync("partner_delete", new { id = s.S.Both }, "IN_USE");
        await PartnerDocs.P.AssertUnchangedAsync(s.Http, s.S.Sup2Partner);

        await s.Mcp.OkAsync("stock_document_update", PartnerDocs.Replacement(s.S, null).WithId(draft.Id()));
        await s.Mcp.OkAsync("partner_delete", new { id = s.S.Sup2 });
        using var gone = await s.Http.GetAsync($"{PartnerDocs.P.Path}/{s.S.Sup2}");
        await HttpAssert.NotFoundAsync(gone);
        await s.Mcp.ErrorAsync("partner_delete", new { id = s.S.Both }, "IN_USE");
    }
}
