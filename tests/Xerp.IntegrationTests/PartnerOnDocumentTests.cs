using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

// Spec 011a: the rules that are the same for a receipt and its supplier as for an issue and its customer are
// written once, against OrderApi (ADR-0016), and run for both.

[Collection(XerpCollection.Name)]
public class ReceiptPartnerTests(XerpFixture app) : PartnerOnDocumentTests(app, OrderApi.Purchase);

[Collection(XerpCollection.Name)]
public class IssuePartnerTests(XerpFixture app) : PartnerOnDocumentTests(app, OrderApi.Sales);

/// <summary>
/// Spec 011a, AC-24, AC-31, AC-33 to AC-35 (the role by type, the re-check at posting of a manual document,
/// reversal) and AC-40 to AC-45 (a document linked to an order has the order's partner and no other).
/// "The partner" is a supplier for a receipt and a customer for an issue; "the wrong partner" has only the
/// other role.
/// </summary>
public abstract class PartnerOnDocumentTests(XerpFixture app, OrderApi o)
{
    private sealed record Setup(PartnerSetup S, JsonElement Right, JsonElement Wrong, JsonElement Other)
    {
        public HttpClient Http => S.Http;
        public Guid A => S.A;
        public Guid W1 => S.W1;
        public Guid W2 => S.W2;
    }

    /// <summary>The setup of section 10 with stock for issues, and a second partner with the role.</summary>
    private async Task<Setup> SetupAsync()
    {
        var s = await PartnerDocs.SetupAsync(app);
        await o.PrepareStockAsync(s.O);
        return new Setup(s, o.PartnerOf(s.O), o.WrongPartnerOf(s.O), await o.NewPartnerAsync(s.Http, "OTHER", "Another partner with the role"));
    }

    private JsonObject Body(Setup s, Guid partner, decimal quantity = 1) => PartnerDocs.Body(s.S, o.DocumentType, partner.ToString(), quantity);

    private Task<JsonElement> DraftAsync(Setup s, Guid partner, decimal quantity = 1) => Stock.CreateAsync(s.Http, Body(s, partner, quantity));

    private async Task<JsonElement> PostedAsync(Setup s, Guid partner, decimal quantity = 1) =>
        await Stock.PostDocumentAsync(s.Http, (await DraftAsync(s, partner, quantity)).Id());

    private static JsonObject Replacement(Setup s, Guid? partner, decimal quantity = 1) =>
        PartnerDocs.Replacement(s.S, partner?.ToString(), quantity);

    private Task<JsonElement> OrderAsync(Setup s) => o.OrderedAsync(s.S.O, (s.A, 10, null, 1m));

    /// <summary>A create body of a document linked to the order: one line of 1 A against order line 1.</summary>
    private JsonObject Linked(Setup s, JsonElement order, Guid? warehouse = null) =>
        o.Document(warehouse ?? s.W1, order.Id(), (s.A, 1, 1, null));

    private static JsonObject LinkedReplacement(Setup s, decimal quantity = 2) => OrderApi.DocumentReplacement(s.W1, (s.A, quantity, 1, null));

    // ---- AC-24 ----

    [Fact]
    public async Task AC24_E1_A_partner_without_the_role_of_the_type_is_refused_and_nothing_is_created()
    {
        var s = await SetupAsync();
        var before = (await Stock.DocumentsAsync(s.Http)).Total();

        using var response = await Stock.PostAsync(s.Http, Body(s, s.Wrong.Id()));

        await PartnerDocs.ConflictExactlyAsync(response, "PARTNER_ROLE_MISSING", "partnerId");
        await PartnerDocs.AssertDocumentCountAsync(s.Http, before);

        // The same partner fits the other type.
        var mirror = o == OrderApi.Purchase ? "issue" : "receipt";
        PartnerDocs.AssertPartner(await Stock.CreateAsync(s.Http, PartnerDocs.Body(s.S, mirror, s.Wrong.Id().ToString())), s.Wrong);
    }

    [Fact]
    public async Task AC24_Replace_with_a_partner_without_the_role_is_refused_and_the_draft_is_unchanged()
    {
        var s = await SetupAsync();
        var draft = await DraftAsync(s, s.Right.Id());
        var plain = await Stock.CreateAsync(s.Http, Stock.Draft(o.DocumentType, s.W1, (s.A, 1)));

        using var replace = await Stock.PutAsync(s.Http, draft.Id(), Replacement(s, s.Wrong.Id(), 9));
        using var assign = await Stock.PutAsync(s.Http, plain.Id(), Replacement(s, s.Wrong.Id(), 9));

        await PartnerDocs.ConflictExactlyAsync(replace, "PARTNER_ROLE_MISSING", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(assign, "PARTNER_ROLE_MISSING", "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await Stock.AssertUnchangedAsync(s.Http, plain);
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_E3_A_partner_deactivated_since_stays_on_the_draft_but_stops_the_posting()
    {
        var s = await SetupAsync();
        var partner = await o.NewPartnerAsync(s.Http, "P1", "Deactivated later");
        var draft = await DraftAsync(s, partner.Id(), 5);
        var second = await DraftAsync(s, partner.Id());
        var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);
        var entries = (await Stock.LedgerAsync(s.Http)).Total();
        var number = (await Stock.DocumentsAsync(s.Http, $"?type={o.DocumentType}&status=posted")).Total();
        await Orders.SetPartnerActiveAsync(s.Http, partner.Id(), false);
        await Orders.SetPartnerActiveAsync(s.Http, s.Other.Id(), false);

        // Read and replaced keeping the partner: 200 (R5 — not a new assignment).
        PartnerDocs.AssertPartner(await Stock.GetAsync(s.Http, draft.Id()), partner);
        var kept = await Stock.ReplaceAsync(s.Http, draft.Id(), Replacement(s, partner.Id(), 5).With("note", "kept"));
        PartnerDocs.AssertPartner(kept, partner);
        Assert.Equal("kept", kept.Str("note"));

        // Another inactive partner is a new assignment.
        using (var another = await Stock.PutAsync(s.Http, draft.Id(), Replacement(s, s.Other.Id(), 5)))
            await PartnerDocs.ConflictExactlyAsync(another, "REFERENCE_INACTIVE", "partnerId");
        // And so is the same partner on a new document.
        using (var create = await Stock.PostAsync(s.Http, Body(s, partner.Id())))
            await PartnerDocs.ConflictExactlyAsync(create, "REFERENCE_INACTIVE", "partnerId");

        using (var post = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(post, "REFERENCE_INACTIVE", "partnerId");

        McpAssert.JsonEqual(kept, await Stock.AssertDraftAsync(s.Http, draft.Id()), "The refused posting changed the draft");
        Assert.Equal(stock, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(entries, (await Stock.LedgerAsync(s.Http)).Total());

        // E3: the draft can still be deleted.
        using (var delete = await Stock.DeleteAsync(s.Http, second.Id()))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await Orders.SetPartnerActiveAsync(s.Http, partner.Id(), true);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        // No number was consumed by the refused posting.
        Assert.Equal(("posted", o.DocumentNumber(number + 1)), (posted.Str("status"), posted.Number()));
        PartnerDocs.AssertPartner(posted, partner);
        Assert.Equal(stock + (o.ConsumesStock ? -5m : 5m), await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    // ---- AC-33 ----

    [Fact]
    public async Task AC33_E4_A_partner_that_lost_the_role_stops_replace_and_posting_until_it_is_removed()
    {
        var s = await SetupAsync();
        var draft = await DraftAsync(s, s.S.Both, 5);
        var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);
        var entries = (await Stock.LedgerAsync(s.Http)).Total();

        await o.RemoveRoleAsync(s.Http, s.S.Both);

        using (var post = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(post, "PARTNER_ROLE_MISSING", "partnerId");
        // R6: the role is checked on every replace, also when the partner is kept.
        using (var keep = await Stock.PutAsync(s.Http, draft.Id(), Replacement(s, s.S.Both, 6)))
            await PartnerDocs.ConflictExactlyAsync(keep, "PARTNER_ROLE_MISSING", "partnerId");
        var still = await Stock.AssertDraftAsync(s.Http, draft.Id());
        McpAssert.JsonEqual(draft.GetProperty("lines"), still.GetProperty("lines"), "The refused replace changed the lines");
        Assert.Equal(s.S.Both, still.GetProperty("partner").Id());
        Assert.Equal(stock, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(entries, (await Stock.LedgerAsync(s.Http)).Total());

        var removed = await Stock.ReplaceAsync(s.Http, draft.Id(), Replacement(s, null, 5));
        PartnerDocs.AssertNoPartner(removed);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("posted", posted.Str("status"));
        PartnerDocs.AssertNoPartner(posted);
        Assert.Equal(stock + (o.ConsumesStock ? -5m : 5m), await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    [Fact]
    public async Task AC33_R12_After_the_role_is_given_back_the_posting_succeeds_with_the_partner()
    {
        var s = await SetupAsync();
        var draft = await DraftAsync(s, s.S.Both, 5);
        await o.RemoveRoleAsync(s.Http, s.S.Both);
        using (var post = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(post, "PARTNER_ROLE_MISSING", "partnerId");

        var restored = await o.RestoreRoleAsync(s.Http, s.S.Both);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        PartnerDocs.AssertPartner(posted, restored);
        // R13: taking the role away afterwards leaves the posted document untouched.
        await o.RemoveRoleAsync(s.Http, s.S.Both);
        McpAssert.JsonEqual(posted, await Stock.GetAsync(s.Http, draft.Id()), "The posted document changed");
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_Reversal_copies_the_partner_of_the_original()
    {
        var s = await SetupAsync();
        var original = await PostedAsync(s, s.Right.Id(), 5);
        var plain = await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, Stock.Draft(o.DocumentType, s.W1, (s.A, 2)))).Id());
        var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);

        using (var withPartner = await Stock.SendReverseAsync(s.Http, original.Id(), Stock.ReverseBody(PartnerDocs.Date).With("partnerId", s.Right.Id().ToString())))
            await HttpAssert.ValidationAsync(withPartner, "partnerId");
        using (var withNull = await Stock.SendReverseAsync(s.Http, original.Id(), Stock.ReverseBody(PartnerDocs.Date).With("partnerId", null)))
            await HttpAssert.ValidationAsync(withNull, "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, original);

        using var response = await Stock.SendReverseAsync(s.Http, original.Id(), PartnerDocs.Date);

        var reversing = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        PartnerDocs.AssertPartner(reversing, s.Right);
        Stock.AssertLink(reversing, "reversalOf", original);
        var reversed = await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, original.Id(), s.Right);
        Assert.Equal("reversed", reversed.Str("status"));
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, reversing.Id(), s.Right);

        var reversingPlain = await Stock.ReverseAsync(s.Http, plain.Id(), PartnerDocs.Date);

        PartnerDocs.AssertNoPartner(reversingPlain);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, plain.Id(), null);
        Assert.Equal(stock + (o.ConsumesStock ? 7m : -7m), await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    // ---- AC-35 ----

    [Fact]
    public async Task AC35_Reversal_checks_nothing_about_the_partner()
    {
        var s = await SetupAsync();
        var partner = await o.NewPartnerAsync(s.Http, "P1", "Deactivated after posting");
        var ofInactive = await PostedAsync(s, partner.Id(), 3);
        var ofRoleless = await PostedAsync(s, s.S.Both, 4);
        var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);

        await Orders.SetPartnerActiveAsync(s.Http, partner.Id(), false);
        var roleless = await o.RemoveRoleAsync(s.Http, s.S.Both);
        // R13: the posted documents are untouched by either.
        await Stock.AssertUnchangedAsync(s.Http, ofInactive);
        await Stock.AssertUnchangedAsync(s.Http, ofRoleless);

        var first = await Stock.ReverseAsync(s.Http, ofInactive.Id(), PartnerDocs.Date);
        var second = await Stock.ReverseAsync(s.Http, ofRoleless.Id(), PartnerDocs.Date);

        PartnerDocs.AssertPartner(first, partner);
        PartnerDocs.AssertPartner(second, roleless);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, ofInactive.Id(), partner);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, ofRoleless.Id(), roleless);
        Assert.Equal(stock + (o.ConsumesStock ? 7m : -7m), await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    // ---- AC-40, AC-41 ----

    [Fact]
    public async Task AC40_AC41_A_linked_document_has_the_partner_of_its_order()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s);
        Assert.Equal(s.Right.Id(), order.GetProperty(o.Partner).Id());

        var omitted = await Stock.CreateAsync(s.Http, Linked(s, order));
        var asNull = await Stock.CreateAsync(s.Http, Linked(s, order).With("partnerId", null));
        var same = await Stock.CreateAsync(s.Http, Linked(s, order).With("partnerId", s.Right.Id().ToString()));

        foreach (var document in new[] { omitted, asNull, same })
        {
            PartnerDocs.AssertPartner(document, s.Right);
            o.AssertLinked(document, order);
            await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, document.Id(), s.Right);
        }
        // The partner of the document is the partner the order shows.
        McpAssert.JsonEqual(order.GetProperty(o.Partner), omitted.GetProperty("partner"), "The document's partner differs from the order's");
    }

    // ---- AC-42 ----

    [Fact]
    public async Task AC42_E5_A_linked_document_cannot_name_another_partner()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s);
        var inactive = await o.NewPartnerAsync(s.Http, "GONE", "Inactive partner", isActive: false);

        async Task RefusedAsync(JsonObject body, params string[] keys)
        {
            using var response = await Stock.PostAsync(s.Http, body);
            await PartnerDocs.ConflictExactlyAsync(response, "ORDER_MISMATCH", keys);
        }

        await RefusedAsync(Linked(s, order).With("partnerId", s.Other.Id().ToString()), "partnerId");
        await RefusedAsync(Linked(s, order).With("partnerId", Guid.NewGuid().ToString()), "partnerId");
        // R9: the value is only compared — without the role, inactive or with both roles it is the same answer.
        await RefusedAsync(Linked(s, order).With("partnerId", s.Wrong.Id().ToString()), "partnerId");
        await RefusedAsync(Linked(s, order).With("partnerId", inactive.Id().ToString()), "partnerId");
        await RefusedAsync(Linked(s, order).With("partnerId", s.S.Both.ToString()), "partnerId");
        await RefusedAsync(Linked(s, order, s.W2).With("partnerId", s.Other.Id().ToString()), "partnerId", "warehouseId");
        await RefusedAsync(o.Document(s.W2, order.Id(), (s.S.B, 1, 1, null)).With("partnerId", s.Other.Id().ToString()),
            "lines[0].articleId", "partnerId", "warehouseId");

        Assert.Empty(await o.DocumentsAsync(s.Http, order.Id()));
        await o.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        await o.AssertUnchangedAsync(s.Http, order);
    }

    [Fact]
    public async Task AC42_R9_A_malformed_partnerId_on_a_linked_document_is_a_validation_error()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s);

        using var response = await Stock.PostAsync(s.Http, Linked(s, order).With("partnerId", "abc"));

        await HttpAssert.ValidationAsync(response, "partnerId");
        Assert.Empty(await o.DocumentsAsync(s.Http, order.Id()));
    }

    // ---- AC-43 ----

    [Fact]
    public async Task AC43_E6_Replace_of_a_linked_draft_keeps_the_partner_of_the_order()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s);
        var draft = await Stock.CreateAsync(s.Http, Linked(s, order));
        Assert.True(LinkedReplacement(s).ContainsKey("partnerId"));

        var withNull = await Stock.ReplaceAsync(s.Http, draft.Id(), LinkedReplacement(s, 2));

        PartnerDocs.AssertPartner(withNull, s.Right);
        o.AssertLinked(withNull, order);
        Assert.Equal(2m, Assert.Single(withNull.DocumentLines()).Quantity());

        var withSame = await Stock.ReplaceAsync(s.Http, draft.Id(), LinkedReplacement(s, 3).With("partnerId", s.Right.Id().ToString()));
        PartnerDocs.AssertPartner(withSame, s.Right);

        using (var other = await Stock.PutAsync(s.Http, draft.Id(), LinkedReplacement(s, 4).With("partnerId", s.Other.Id().ToString())))
            await PartnerDocs.ConflictExactlyAsync(other, "ORDER_MISMATCH", "partnerId");
        using (var unknown = await Stock.PutAsync(s.Http, draft.Id(), LinkedReplacement(s, 4).With("partnerId", Guid.NewGuid().ToString())))
            await PartnerDocs.ConflictExactlyAsync(unknown, "ORDER_MISMATCH", "partnerId");
        using (var otherAndWarehouse = await Stock.PutAsync(s.Http, draft.Id(),
                   OrderApi.DocumentReplacement(s.W2, (s.A, 4, 1, null)).With("partnerId", s.Other.Id().ToString())))
            await PartnerDocs.ConflictExactlyAsync(otherAndWarehouse, "ORDER_MISMATCH", "partnerId", "warehouseId");
        using (var missing = await Stock.PutAsync(s.Http, draft.Id(), LinkedReplacement(s, 4).Without("partnerId")))
            await HttpAssert.ValidationAsync(missing, "partnerId");

        await Stock.AssertUnchangedAsync(s.Http, withSame);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, draft.Id(), s.Right);
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_E7_The_partner_of_the_order_is_not_checked_again_on_a_linked_document()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s);
        var before = await Stock.CreateAsync(s.Http, Linked(s, order));
        var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);

        await Orders.SetPartnerActiveAsync(s.Http, s.Right.Id(), false);

        // Created after the deactivation, without and with the order's partner named.
        var draft = await Stock.CreateAsync(s.Http, o.Document(s.W1, order.Id(), (s.A, 4, 1, null)));
        var named = await Stock.CreateAsync(s.Http, Linked(s, order).With("partnerId", s.Right.Id().ToString()));
        PartnerDocs.AssertPartner(draft, s.Right);
        PartnerDocs.AssertPartner(named, s.Right);
        // Replaced, with null and with the partner.
        PartnerDocs.AssertPartner(await Stock.ReplaceAsync(s.Http, before.Id(), LinkedReplacement(s, 2)), s.Right);
        PartnerDocs.AssertPartner(
            await Stock.ReplaceAsync(s.Http, named.Id(), LinkedReplacement(s, 1).With("partnerId", s.Right.Id().ToString())), s.Right);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("posted", posted.Str("status"));
        PartnerDocs.AssertPartner(posted, s.Right);
        await o.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m));
        Assert.Equal(stock + (o.ConsumesStock ? -4m : 4m), await Stock.QuantityAsync(s.Http, s.A, s.W1));

        // An unlinked document for the same, now inactive, partner is still refused (R5): the exemption is the link's.
        using (var manual = await Stock.PostAsync(s.Http, Body(s, s.Right.Id())))
            await PartnerDocs.ConflictExactlyAsync(manual, "REFERENCE_INACTIVE", "partnerId");
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }

    // ---- AC-45 ----

    [Fact]
    public async Task AC45_A_posted_linked_document_and_its_reversal_show_the_partner_and_the_order()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s);
        var stock = await Stock.QuantityAsync(s.Http, s.A, s.W1);

        var posted = await o.FulfilAsync(s.Http, order, 1, 4);

        PartnerDocs.AssertPartner(posted, s.Right);
        o.AssertLinked(posted, order);
        Assert.Equal(1, Assert.Single(posted.DocumentLines()).GetProperty("orderLineNo").GetInt32());
        await o.AssertProgressAsync(s.Http, order.Id(), "confirmed", "partial", (4m, 6m));

        // Reversed after the partner was deactivated: nothing is checked (R14).
        await Orders.SetPartnerActiveAsync(s.Http, s.Right.Id(), false);
        var reversing = await Stock.ReverseAsync(s.Http, posted.Id(), Stock.NextDay);

        PartnerDocs.AssertPartner(reversing, s.Right);
        o.AssertLinked(reversing, order);
        Assert.Equal(1, Assert.Single(reversing.DocumentLines()).GetProperty("orderLineNo").GetInt32());
        Stock.AssertLink(reversing, "reversalOf", posted);
        var reversed = await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, posted.Id(), s.Right);
        Assert.Equal("reversed", reversed.Str("status"));
        o.AssertLinked(reversed, order);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, reversing.Id(), s.Right);
        // 009/R33: the reversal gives the quantity back to the order.
        await o.AssertProgressAsync(s.Http, order.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(stock, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(PartnerDocs.Set(posted, reversing), PartnerDocs.Set(await o.DocumentsAsync(s.Http, order.Id())));
        await PartnerDocs.AssertBalancedAsync(s.S, "AC-90");
    }
}
