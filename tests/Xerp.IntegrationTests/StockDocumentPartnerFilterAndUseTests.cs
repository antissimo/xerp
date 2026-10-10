using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011a, AC-50 and AC-51 (the list filter <c>partnerId</c>, R16) and AC-60 and AC-61 (a partner named by
/// a stock document is in use, R15).
/// </summary>
[Collection(XerpCollection.Name)]
public class StockDocumentPartnerFilterAndUseTests(XerpFixture app)
{
    private static readonly MasterApi P = PartnerDocs.P;
    private static readonly OrderApi PO = OrderApi.Purchase;

    private static async Task AssertDeletedAsync(HttpClient client, Guid partner)
    {
        using var response = await P.DeleteAsync(client, partner);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"Expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var get = await client.GetAsync($"{P.Path}/{partner}");
        await HttpAssert.NotFoundAsync(get);
    }

    private static async Task AssertInUseAsync(HttpClient client, JsonElement partner)
    {
        using var response = await P.DeleteAsync(client, partner.Id());
        await HttpAssert.InUseAsync(response);
        await P.AssertUnchangedAsync(client, partner);
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_The_list_is_filtered_by_partner_linked_or_not_and_combined_with_the_other_filters()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup);
        var ofSup2 = await PartnerDocs.PostedReceiptAsync(s, s.Sup2, 20);
        var order = await PO.OrderedAsync(s.O, (s.A, 10, null, 1m));
        var linked = await PO.FulfilAsync(s.Http, order, 1, 4);
        var plain = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var issue = await PartnerDocs.IssueAsync(s, s.Cus);
        var transfer = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1));

        Assert.Equal(PartnerDocs.Set(draft, linked), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}"));
        Assert.Equal(PartnerDocs.Set(ofSup2), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup2}"));
        Assert.Equal(PartnerDocs.Set(issue), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Cus}"));
        Assert.Equal(PartnerDocs.Set(linked), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&status=posted"));
        Assert.Equal(PartnerDocs.Set(draft), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&status=draft"));
        Assert.Equal(PartnerDocs.Set(linked), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&purchaseOrderId={order.Id()}"));
        Assert.Equal(PartnerDocs.Set(draft, linked), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&type=receipt&warehouseId={s.W1}"));
        Assert.Empty(await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&type=issue"));
        Assert.Empty(await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&warehouseId={s.W2}"));
        Assert.Empty(await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup2}&purchaseOrderId={order.Id()}"));
        Assert.Empty(await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Both}"));

        // A random uuid, and an id that is another master: 200 and empty.
        var random = await Stock.DocumentsAsync(s.Http, $"?partnerId={Guid.NewGuid()}");
        Assert.Equal((0, 0), (random.Total(), random.Items().Length));
        Assert.Empty(await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.W1}"));

        // Without the filter everything is listed, and every summary carries the partner of its document.
        var all = await Balance.AllAsync(s.Http, Stock.Documents);
        Assert.Equal(PartnerDocs.Set(draft, ofSup2, linked, plain, issue, transfer), PartnerDocs.Set(all));
        foreach (var summary in all)
            McpAssert.JsonEqual((await Stock.GetAsync(s.Http, summary.Id())).GetProperty("partner"), summary.GetProperty("partner"),
                "Summary and document differ in 'partner'");
        Assert.All((await Stock.DocumentsAsync(s.Http, $"?partnerId={s.Sup}")).Items(), d => PartnerDocs.AssertPartner(d, s.O.Sup));
    }

    [Fact]
    public async Task AC50_R16_The_filter_finds_every_status_reversed_and_reversing_documents_included()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup);
        var posted = await PartnerDocs.PostedReceiptAsync(s, s.Sup, 5);
        var original = await PartnerDocs.PostedReceiptAsync(s, s.Sup, 3);
        var reversing = await Stock.ReverseAsync(s.Http, original.Id(), PartnerDocs.Date);
        await PartnerDocs.PostedReceiptAsync(s, s.Sup2, 2);

        Assert.Equal(PartnerDocs.Set(draft, posted, original, reversing), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}"));
        Assert.Equal(PartnerDocs.Set(original), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&status=reversed"));
        Assert.Equal(PartnerDocs.Set(posted, reversing), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}&status=posted"));

        // Removing the partner from the draft takes it off the filtered list.
        await Stock.ReplaceAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, null));
        Assert.Equal(PartnerDocs.Set(posted, original, reversing), await PartnerDocs.IdsAsync(s.Http, $"?partnerId={s.Sup}"));
        await PartnerDocs.AssertBalancedAsync(s, "AC-90");
    }

    // ---- AC-51 ----

    [Fact]
    public async Task AC51_The_filtered_list_follows_the_list_envelope_and_keeps_the_order_of_the_list()
    {
        var s = await PartnerDocs.SetupAsync(app);
        foreach (var partner in new[] { s.Sup, s.Sup2, s.Sup, s.Both, s.Sup })
            await PartnerDocs.ReceiptAsync(s, partner);
        await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));

        var firstPage = await Stock.DocumentsAsync(s.Http, $"?partnerId={s.Sup}&limit=2");
        var secondPage = await Stock.DocumentsAsync(s.Http, $"?partnerId={s.Sup}&limit=2&offset=2");
        var beyond = await Stock.DocumentsAsync(s.Http, $"?partnerId={s.Sup}&limit=2&offset=3");
        var whole = await Stock.DocumentsAsync(s.Http, $"?partnerId={s.Sup}");

        Assert.Equal((3, 2, 0, 2), (firstPage.Total(), firstPage.GetProperty("limit").GetInt32(), firstPage.GetProperty("offset").GetInt32(), firstPage.Items().Length));
        Assert.Equal((3, 2, 2, 1), (secondPage.Total(), secondPage.GetProperty("limit").GetInt32(), secondPage.GetProperty("offset").GetInt32(), secondPage.Items().Length));
        Assert.Equal((3, 0), (beyond.Total(), beyond.Items().Length));
        Assert.Equal((3, 3), (whole.Total(), whole.Items().Length));
        Assert.Equal(whole.PropertyNames(), (await Stock.DocumentsAsync(s.Http)).PropertyNames());

        // Ordering is that of the unfiltered list.
        var expected = (await Balance.AllAsync(s.Http, Stock.Documents))
            .Where(d => d.GetProperty("partner").ValueKind == JsonValueKind.Object && d.GetProperty("partner").Id() == s.Sup)
            .Select(d => d.Id()).ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected, whole.Items().Select(d => d.Id()).ToArray());
        Assert.Equal(expected, firstPage.Items().Concat(secondPage.Items()).Select(d => d.Id()).ToArray());
    }

    // ---- AC-60 ----

    [Fact]
    public async Task AC60_E10_A_partner_named_by_a_draft_is_in_use_until_the_draft_lets_it_go()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup2);
        var ofBoth = await PartnerDocs.IssueAsync(s, s.Both);

        await AssertInUseAsync(s.Http, s.Sup2Partner);
        await AssertInUseAsync(s.Http, s.BothPartner);
        await Stock.AssertUnchangedAsync(s.Http, draft);

        // Replaced with another partner or with null, the first one is free.
        await Stock.ReplaceAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, null));
        await AssertDeletedAsync(s.Http, s.Sup2);
        PartnerDocs.AssertNoPartner(await Stock.GetAsync(s.Http, draft.Id()));

        // E10 / 005/R27: deleting the last draft that names it makes it unused again.
        using (var delete = await Stock.DeleteAsync(s.Http, ofBoth.Id()))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await AssertDeletedAsync(s.Http, s.Both);
    }

    [Fact]
    public async Task AC60_R15_A_partner_is_in_use_while_any_of_several_drafts_names_it()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var first = await PartnerDocs.ReceiptAsync(s, s.Sup2);
        var second = await PartnerDocs.ReceiptAsync(s, s.Sup2);

        await Stock.ReplaceAsync(s.Http, first.Id(), PartnerDocs.Replacement(s, s.Sup));
        await AssertInUseAsync(s.Http, s.Sup2Partner);

        using (var delete = await Stock.DeleteAsync(s.Http, second.Id()))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await AssertDeletedAsync(s.Http, s.Sup2);
        // SUP now is named by the first draft.
        await AssertInUseAsync(s.Http, s.O.Sup);
    }

    // ---- AC-61 ----

    [Fact]
    public async Task AC61_A_partner_named_by_a_posted_document_is_in_use_for_good_but_can_be_changed()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var unused = await Orders.PartnerAsync(s.Http, "FREE", "Named by nothing", isSupplier: true, isCustomer: true);
        var posted = await PartnerDocs.PostedReceiptAsync(s, s.Sup2, 5);

        await AssertInUseAsync(s.Http, s.Sup2Partner);

        // Reversed, both documents still name it.
        await Stock.ReverseAsync(s.Http, posted.Id(), PartnerDocs.Date);
        await AssertInUseAsync(s.Http, s.Sup2Partner);

        // R15: renamed, re-coded, deactivated, its roles changed — all 200.
        var changed = await Orders.ChangePartnerAsync(s.Http, s.Sup2, b =>
        {
            b["name"] = "Renamed supplier";
            b["code"] = "SUP2-X";
        });
        changed = await Orders.ChangePartnerAsync(s.Http, s.Sup2, b => b["isActive"] = false);
        changed = await Orders.ChangePartnerAsync(s.Http, s.Sup2, b =>
        {
            b["isSupplier"] = false;
            b["isCustomer"] = true;
        });
        Assert.Equal(("SUP2-X", "Renamed supplier", false, false, true),
            (changed.Str("code"), changed.Str("name"), changed.Bool("isActive"), changed.Bool("isSupplier"), changed.Bool("isCustomer")));
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, posted.Id(), changed);
        await AssertInUseAsync(s.Http, changed);

        // 004: a partner named by nothing is deleted as before.
        await AssertDeletedAsync(s.Http, unused.Id());
        await PartnerDocs.AssertBalancedAsync(s, "AC-90");
    }
}
