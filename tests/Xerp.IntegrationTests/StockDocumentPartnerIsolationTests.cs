using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011a, AC-70 and AC-71 (T1–T4): a stock document can never name another tenant's partner — as a
/// reference it does not exist, on a linked document it is "another id", as a filter it finds nothing — and
/// "in use" is decided by the caller's tenant's documents only. Over HTTP and through the tools.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockDocumentPartnerIsolationTests(XerpFixture app)
{
    private static readonly MasterApi P = PartnerDocs.P;
    private static readonly OrderApi PO = OrderApi.Purchase;

    private sealed record Side(PartnerSetup S, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Side> SideAsync()
    {
        var setup = await PartnerDocs.SetupAsync(app);
        return new Side(setup, await app.McpAsync(setup.Tenant.Key));
    }

    /// <summary>The problem of a refused request and its raw text.</summary>
    private static async Task<(JsonElement Problem, string Text)> RefusedAsync(HttpResponseMessage response, string code, params string[] keys)
    {
        var text = await response.Content.ReadAsStringAsync();
        return (await PartnerDocs.ConflictExactlyAsync(response, code, keys), text);
    }

    // ---- AC-70 ----

    [Fact]
    public async Task AC70_Another_tenants_partner_does_not_exist_as_the_partner_of_a_document()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        // Y's supplier has a code and a name X's tenant does not have.
        var foreign = await Orders.PartnerAsync(y.Http, "YSUPPLIER", "Ypsilon Trading", isSupplier: true, isCustomer: true);
        await PartnerDocs.ReceiptAsync(y.S, foreign.Id());
        var draft = await PartnerDocs.ReceiptAsync(x.S, x.S.Sup);

        using var withForeign = await Stock.PostAsync(x.Http, PartnerDocs.Receipt(x.S, foreign.Id()));
        using var withRandom = await Stock.PostAsync(x.Http, PartnerDocs.Receipt(x.S, Guid.NewGuid()));
        using var issueWithForeign = await Stock.PostAsync(x.Http, PartnerDocs.Issue(x.S, foreign.Id()));
        using var replaceWithForeign = await Stock.PutAsync(x.Http, draft.Id(), PartnerDocs.Replacement(x.S, foreign.Id()));

        var (ofForeign, foreignText) = await RefusedAsync(withForeign, "REFERENCE_NOT_FOUND", "partnerId");
        var (ofRandom, _) = await RefusedAsync(withRandom, "REFERENCE_NOT_FOUND", "partnerId");
        await RefusedAsync(issueWithForeign, "REFERENCE_NOT_FOUND", "partnerId");
        var (_, replaceText) = await RefusedAsync(replaceWithForeign, "REFERENCE_NOT_FOUND", "partnerId");
        // "A body equal in shape": the same properties, the same keys, the same title and status.
        Assert.Equal(ofRandom.PropertyNames(), ofForeign.PropertyNames());
        Assert.Equal(ofRandom.Str("title"), ofForeign.Str("title"));
        Assert.Equal(ofRandom.GetProperty("errors").GetProperty("partnerId").GetArrayLength(), ofForeign.GetProperty("errors").GetProperty("partnerId").GetArrayLength());
        // S2: the error names the key, never the partner.
        foreach (var text in new[] { foreignText, replaceText })
        {
            Assert.DoesNotContain("YSUPPLIER", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ypsilon", text, StringComparison.OrdinalIgnoreCase);
        }
        await Stock.AssertUnchangedAsync(x.Http, draft);
        await PartnerDocs.AssertDocumentCountAsync(x.Http, 1);

        // The same through the tools.
        await x.Mcp.ErrorAsync("stock_document_create", PartnerDocs.Receipt(x.S, foreign.Id()), "REFERENCE_NOT_FOUND", "partnerId");
        await x.Mcp.ErrorAsync("stock_document_update", PartnerDocs.Replacement(x.S, foreign.Id()).WithId(draft.Id()), "REFERENCE_NOT_FOUND", "partnerId");
        await PartnerDocs.AssertDocumentCountAsync(x.Http, 1);
        // Y's partner and document are untouched.
        await P.AssertUnchangedAsync(y.Http, foreign);
        Assert.Equal(1, (await Stock.DocumentsAsync(y.Http, $"?partnerId={foreign.Id()}")).Total());
    }

    [Fact]
    public async Task AC70_T2_On_a_linked_document_another_tenants_partner_is_ORDER_MISMATCH_like_any_other_id()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var order = await PO.OrderedAsync(x.S.O, (x.S.A, 10, null, 1m));
        var draft = await PO.DraftDocumentAsync(x.Http, order, 1, 1);
        JsonObject Linked(Guid partner) => PO.Document(x.S.W1, order.Id(), (x.S.A, 1, 1, null)).With("partnerId", partner.ToString());

        using var withForeign = await Stock.PostAsync(x.Http, Linked(y.S.Sup));
        using var withRandom = await Stock.PostAsync(x.Http, Linked(Guid.NewGuid()));
        using var replace = await Stock.PutAsync(x.Http, draft.Id(),
            OrderApi.DocumentReplacement(x.S.W1, (x.S.A, 2, 1, null)).With("partnerId", y.S.Sup.ToString()));

        var (ofForeign, _) = await RefusedAsync(withForeign, "ORDER_MISMATCH", "partnerId");
        var (ofRandom, _) = await RefusedAsync(withRandom, "ORDER_MISMATCH", "partnerId");
        await RefusedAsync(replace, "ORDER_MISMATCH", "partnerId");
        Assert.Equal(ofRandom.PropertyNames(), ofForeign.PropertyNames());
        await x.Mcp.ErrorAsync("stock_document_create", Linked(y.S.Sup), "ORDER_MISMATCH", "partnerId");
        await Stock.AssertUnchangedAsync(x.Http, draft);
        Assert.Equal(PartnerDocs.Set(draft), PartnerDocs.Set(await PO.DocumentsAsync(x.Http, order.Id())));
        PartnerDocs.AssertPartner(await Stock.GetAsync(x.Http, draft.Id()), x.S.O.Sup);
    }

    // ---- AC-71 ----

    [Fact]
    public async Task AC71_T3_The_filter_by_another_tenants_partner_is_empty()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var ofY = await PartnerDocs.PostedReceiptAsync(y.S, y.S.Sup, 5);
        var draftOfY = await PartnerDocs.ReceiptAsync(y.S, y.S.Sup);
        var ofX = await PartnerDocs.ReceiptAsync(x.S, x.S.Sup);

        foreach (var query in new[] { $"?partnerId={y.S.Sup}", $"?partnerId={y.S.Sup}&status=posted", $"?partnerId={y.S.Sup}&type=receipt" })
        {
            var list = await Stock.DocumentsAsync(x.Http, query);
            Assert.True(list.Total() == 0 && list.Items().Length == 0, $"X sees Y's documents with '{query}': {list}");
        }
        Assert.Equal(0, (await x.Mcp.OkAsync("stock_document_list", new { partnerId = y.S.Sup })).Total());

        // Each tenant finds its own, and only its own, by its own partner.
        Assert.Equal(PartnerDocs.Set(ofX), await PartnerDocs.IdsAsync(x.Http, $"?partnerId={x.S.Sup}"));
        Assert.Equal(PartnerDocs.Set(ofY, draftOfY), await PartnerDocs.IdsAsync(y.Http, $"?partnerId={y.S.Sup}"));
        Assert.Equal(0, (await Stock.DocumentsAsync(y.Http, $"?partnerId={x.S.Sup}")).Total());
        Assert.Equal(PartnerDocs.Set(ofX), PartnerDocs.Set(await Balance.AllAsync(x.Http, Stock.Documents)));
        // Y's documents do not exist for X.
        using var get = await x.Http.GetAsync($"{Stock.Documents}/{ofY.Id()}");
        await HttpAssert.NotFoundAsync(get);
    }

    [Fact]
    public async Task AC71_T4_In_use_is_decided_by_the_callers_tenants_documents_only()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        // X uses its SUP2 and BOTH; Y's partners of the same codes are named by nothing.
        await PartnerDocs.PostedReceiptAsync(x.S, x.S.Sup2, 5);
        await PartnerDocs.ReceiptAsync(x.S, x.S.Both);
        var named = await PartnerDocs.ReceiptAsync(y.S, y.S.Sup);

        using (var free = await P.DeleteAsync(y.Http, y.S.Sup2))
            Assert.Equal(HttpStatusCode.NoContent, free.StatusCode);
        await y.Mcp.OkAsync("partner_delete", new { id = y.S.Both });

        // Y's supplier is named by Y's receipt only: for X it does not exist, for Y it is in use.
        using (var byX = await P.DeleteAsync(x.Http, y.S.Sup))
            await HttpAssert.NotFoundAsync(byX);
        await x.Mcp.ErrorAsync("partner_delete", new { id = y.S.Sup }, "NOT_FOUND");
        using (var byY = await P.DeleteAsync(y.Http, y.S.Sup))
            await HttpAssert.InUseAsync(byY);
        await y.Mcp.ErrorAsync("partner_delete", new { id = y.S.Sup }, "IN_USE");

        // X's partners stay in use whatever Y deleted; Y's becomes free with its own draft.
        using (var ofX = await P.DeleteAsync(x.Http, x.S.Sup2))
            await HttpAssert.InUseAsync(ofX);
        using (var ofXDraft = await P.DeleteAsync(x.Http, x.S.Both))
            await HttpAssert.InUseAsync(ofXDraft);
        using (var deleteDraft = await Stock.DeleteAsync(y.Http, named.Id()))
            Assert.Equal(HttpStatusCode.NoContent, deleteDraft.StatusCode);
        using (var nowFree = await P.DeleteAsync(y.Http, y.S.Sup))
            Assert.Equal(HttpStatusCode.NoContent, nowFree.StatusCode);
        await P.AssertUnchangedAsync(x.Http, x.S.O.Sup);
        await PartnerDocs.AssertBalancedAsync(x.S, "AC-90");
    }
}
