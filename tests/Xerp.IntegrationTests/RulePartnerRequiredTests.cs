using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-80 to AC-89 and AC-120: <c>purchase.partnerRequired</c> and <c>sales.partnerRequired</c>
/// (R32–R37, E13, E14). Written once for both kinds of order (ADR-0016; section 6.7: "it holds mirrored"). At the
/// default an order without a partner is refused, naming the rule; with <c>false</c> it is an order like any
/// other — created, confirmed, fulfilled by a document without a partner — and a later switch back judges only
/// its next save.
/// </summary>
public abstract class RulePartnerRequiredTests(XerpFixture app, OrderApi o)
{
    private string Rule => Rules.PartnerRule(o);

    private async Task<OrderSetup> OptionalAsync()
    {
        var s = await Orders.SetupAsync(app);
        Rules.AssertTenantValue(await Rules.SetAsync(s.Http, Rule, false), Rule, false, s.Tenant.ApiKeyId);
        return s;
    }

    private async Task<int> OrderCountAsync(OrderSetup s) => (await o.ListAsync(s.Http)).Total();

    /// <summary>"PO [lines] without supplier": created without the partner property and confirmed.</summary>
    private async Task<JsonElement> OrderedWithoutPartnerAsync(OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        await o.ConfirmAsync(s.Http, (await o.CreateAsync(s.Http, o.NoPartner(s, lines))).Id());

    // ---- AC-80 ----

    [Fact]
    public async Task AC80_At_the_default_an_order_without_a_partner_is_refused_by_the_rule_on_create_and_on_replace()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        using var omitted = await o.PostAsync(http, o.NoPartner(s, (s.A, 1, null, 1m)));
        using var asNull = await o.PostAsync(http, o.Body(s, (s.A, 1, null, 1m)).With(o.PartnerId, null));
        using var replace = await o.PutAsync(http, draft.Id(), o.NoPartnerReplacement(s, (s.B, 1, null, 1m)));

        await Rules.PartnerRequiredAsync(omitted, o);
        await Rules.PartnerRequiredAsync(asNull, o);
        await Rules.PartnerRequiredAsync(replace, o);
        Assert.Equal(1, await OrderCountAsync(s));
        await o.AssertUnchangedAsync(http, draft);
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_With_the_rule_off_an_order_without_a_partner_is_created_and_read_with_a_null_partner()
    {
        var s = await OptionalAsync();
        var http = s.Http;

        var omitted = await o.CreateAsync(http, o.NoPartner(s, (s.A, 10, null, 2.5m)));
        var asNull = await o.CreateAsync(http, o.Body(s, (s.A, 1, null, 1m)).With(o.PartnerId, null));
        var withPartner = await o.CreateAsync(http, o.Body(s, (s.A, 1, null, 1m)));

        foreach (var order in new[] { omitted, asNull })
        {
            Rules.AssertNoPartner(o, order);
            Assert.Equal("draft", order.Str("status"));
            McpAssert.JsonEqual(order, await o.GetAsync(http, order.Id()), "GET differs from the created order");
        }
        Assert.Equal(25m, omitted.Dec("totalAmount"));
        // An order with a partner has exactly the properties of one without: the property is always present.
        Assert.Equal(withPartner.PropertyNames(), omitted.PropertyNames());
        Assert.Equal(o.PartnerOf(s).Id(), withPartner.GetProperty(o.Partner).Id());
        Assert.Equal(new[] { "code", "id", "name" }, withPartner.GetProperty(o.Partner).PropertyNames());

        var list = await o.ListAsync(http);
        Assert.Equal(3, list.Total());
        var items = list.Items().ToDictionary(i => i.Id());
        Rules.AssertNoPartner(o, items[omitted.Id()]);
        Rules.AssertNoPartner(o, items[asNull.Id()]);
        Assert.Equal(o.PartnerOf(s).Id(), items[withPartner.Id()].GetProperty(o.Partner).Id());
        Assert.Equal(items[withPartner.Id()].PropertyNames(), items[omitted.Id()].PropertyNames());
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_With_the_rule_off_a_partner_that_is_named_is_still_checked()
    {
        var s = await OptionalAsync();
        var http = s.Http;
        var inactive = await o.NewPartnerAsync(http, "GONE", "Inactive partner", isActive: false);
        var draft = await o.CreateAsync(http, o.NoPartner(s, (s.A, 1, null, 1m)));
        JsonObject With(JsonNode? partner) => o.Body(s, (s.A, 1, null, 1m)).With(o.PartnerId, partner);
        JsonObject ReplaceWith(JsonNode? partner) => o.Replacement(s, (s.A, 1, null, 1m)).With(o.PartnerId, partner);

        using var unknown = await o.PostAsync(http, With(Guid.CreateVersion7().ToString()));
        using var wrongRole = await o.PostAsync(http, With(o.WrongPartnerOf(s).Id().ToString()));
        using var notActive = await o.PostAsync(http, With(inactive.Id().ToString()));
        using var malformed = await o.PostAsync(http, With("abc"));
        using var empty = await o.PostAsync(http, With(""));
        using var number = await o.PostAsync(http, With(5));
        using var replaceWithout = await o.PutAsync(http, draft.Id(), o.Replacement(s, (s.A, 1, null, 1m)).Without(o.PartnerId));
        using var replaceUnknown = await o.PutAsync(http, draft.Id(), ReplaceWith(Guid.CreateVersion7().ToString()));
        using var replaceWrongRole = await o.PutAsync(http, draft.Id(), ReplaceWith(o.WrongPartnerOf(s).Id().ToString()));
        using var replaceInactive = await o.PutAsync(http, draft.Id(), ReplaceWith(inactive.Id().ToString()));
        using var replaceMalformed = await o.PutAsync(http, draft.Id(), ReplaceWith("abc"));

        async Task ConflictAsync(HttpResponseMessage response, string code)
        {
            var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, code);
            Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(problem));
            Rules.AssertNamesNoRule(problem);
        }

        await ConflictAsync(unknown, "REFERENCE_NOT_FOUND");
        await ConflictAsync(wrongRole, "PARTNER_ROLE_MISSING");
        await ConflictAsync(notActive, "REFERENCE_INACTIVE");
        await ConflictAsync(replaceUnknown, "REFERENCE_NOT_FOUND");
        await ConflictAsync(replaceWrongRole, "PARTNER_ROLE_MISSING");
        await ConflictAsync(replaceInactive, "REFERENCE_INACTIVE");
        foreach (var response in new[] { malformed, empty, number, replaceWithout, replaceMalformed })
        {
            var problem = await HttpAssert.ValidationAsync(response, o.PartnerId);
            Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(problem));
            Rules.AssertNamesNoRule(problem);
        }
        Assert.Equal(1, await OrderCountAsync(s));
        await o.AssertUnchangedAsync(http, draft);
    }

    // ---- AC-83 ----

    [Fact]
    public async Task AC83_With_the_rule_off_a_replace_removes_the_partner_of_a_draft_and_gives_one_back()
    {
        var s = await OptionalAsync();
        var http = s.Http;
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));
        Assert.Equal(o.PartnerOf(s).Id(), draft.GetProperty(o.Partner).Id());

        var removed = await o.ReplaceAsync(http, draft.Id(), o.NoPartnerReplacement(s, (s.A, 10, null, 2.5m)));

        Rules.AssertNoPartner(o, removed);
        Assert.Equal(draft.PropertyNames(), removed.PropertyNames());
        McpAssert.JsonEqual(removed, await o.GetAsync(http, draft.Id()));

        var given = await o.ReplaceAsync(http, draft.Id(), o.Replacement(s, (s.A, 10, null, 2.5m)));

        Assert.Equal(o.PartnerOf(s).Id(), given.GetProperty(o.Partner).Id());
        McpAssert.JsonEqual(given, await o.GetAsync(http, draft.Id()));

        // And a draft created without a partner gets one by a replace.
        var without = await o.CreateAsync(http, o.NoPartner(s, (s.B, 1, null, 1m)));
        var named = await o.ReplaceAsync(http, without.Id(), o.Replacement(s, (s.B, 1, null, 1m)));
        Assert.Equal(o.PartnerOf(s).Id(), named.GetProperty(o.Partner).Id());
    }

    // ---- AC-84, AC-87 ----

    [Fact]
    public async Task AC84_AC87_With_the_rule_off_an_order_without_a_partner_is_confirmed_fulfilled_closed_and_reopened()
    {
        var s = await OptionalAsync();
        var http = s.Http;
        await o.PrepareStockAsync(s);
        var draft = await o.CreateAsync(http, o.NoPartner(s, (s.A, 10, null, 2.5m)));

        var confirmed = await o.ConfirmAsync(http, draft.Id());

        Assert.Equal(("confirmed", o.Number(1)), (confirmed.Str("status"), confirmed.Number()));
        Rules.AssertNoPartner(o, confirmed);
        using (var byNumber = await o.ByNumberAsync(http, o.Number(1)))
            McpAssert.JsonEqual(confirmed, await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK));
        // R35: its outstanding quantity counts as incoming (sales: reserved) like any other order's.
        Assert.Equal(10m, await o.OnHandAsync(http, s.A, s.W1));

        var document = await o.FulfilAsync(http, confirmed, 1, 10);

        Assert.Equal(("posted", o.DocumentType), (document.Str("status"), document.Str("type")));
        // R36 (011a/R8): the partner of a linked document is the order's partner — null when the order has none.
        JsonBody.AssertNull(document, "partner");
        o.AssertLinked(document, confirmed);
        JsonBody.AssertNull(await Stock.GetAsync(http, document.Id()), "partner");
        var full = await o.AssertProgressAsync(http, confirmed.Id(), "confirmed", "full", (10m, 0m));
        Rules.AssertNoPartner(o, full);
        Assert.Equal(0m, await o.OnHandAsync(http, s.A, s.W1));

        var closed = await o.CloseAsync(http, confirmed.Id());
        Assert.Equal("closed", closed.Str("status"));
        Rules.AssertNoPartner(o, closed);
        var reopened = await o.ReopenAsync(http, confirmed.Id());
        Assert.Equal("confirmed", reopened.Str("status"));
        Rules.AssertNoPartner(o, reopened);

        var reversing = await Stock.ReverseAsync(http, document.Id(), Stock.NextDay);

        // 011a/R14: the reversing document has the partner of the original — none.
        JsonBody.AssertNull(reversing, "partner");
        o.AssertLinked(reversing, confirmed);
        await o.AssertProgressAsync(http, confirmed.Id(), "confirmed", "none", (0m, 10m));
        Assert.Equal(10m, await o.OnHandAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-85 ----

    [Fact]
    public async Task AC85_A_document_linked_to_an_order_without_a_partner_cannot_name_one()
    {
        var s = await OptionalAsync();
        var http = s.Http;
        await o.PrepareStockAsync(s);
        var order = await OrderedWithoutPartnerAsync(s, (s.A, 10, null, 1m));
        JsonObject Linked() => o.Document(s.W1, order.Id(), (s.A, 1, 1, null));
        var documents = (await Stock.DocumentsAsync(http)).Total();

        using var named = await Stock.PostAsync(http, Linked().With("partnerId", o.PartnerOf(s).Id().ToString()));

        var problem = await Stock.ConflictAsync(named, "ORDER_MISMATCH", "partnerId");
        Assert.Equal(new[] { "partnerId" }, McpAssert.ErrorKeys(problem));
        Rules.AssertNamesNoRule(problem);
        Assert.Equal(documents, (await Stock.DocumentsAsync(http)).Total());

        var asNull = await Stock.CreateAsync(http, Linked().With("partnerId", null));
        var omitted = await Stock.CreateAsync(http, Linked());

        foreach (var document in new[] { asNull, omitted })
        {
            JsonBody.AssertNull(document, "partner");
            o.AssertLinked(document, order);
        }
        // On replace of the linked draft: the same (011a/R9).
        using var replaceNamed = await Stock.PutAsync(http, omitted.Id(),
            OrderApi.DocumentReplacement(s.W1, (s.A, 2, 1, null)).With("partnerId", o.PartnerOf(s).Id().ToString()));
        Assert.Equal(new[] { "partnerId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(replaceNamed, "ORDER_MISMATCH", "partnerId")));
        var replaced = await Stock.ReplaceAsync(http, omitted.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 2, 1, null)));
        JsonBody.AssertNull(replaced, "partner");
        JsonBody.AssertNull(await Stock.PostDocumentAsync(http, omitted.Id()), "partner");
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-86 ----

    [Fact]
    public async Task AC86_The_list_has_the_order_without_a_partner_and_the_partner_filter_never_returns_it()
    {
        var s = await OptionalAsync();
        var http = s.Http;
        var without = await OrderedWithoutPartnerAsync(s, (s.A, 10, null, 1m));
        var draftWithout = await o.CreateAsync(http, o.NoPartner(s, (s.B, 1, null, 1m)));
        var withPartner = await o.OrderedAsync(s, (s.A, 1, null, 1m));
        var second = await o.NewPartnerAsync(http, "P2");
        var ofSecond = await o.CreateAsync(http, o.Body(second.Id(), s.W1, (s.A, 1, null, 1m)));

        async Task<Guid[]> IdsAsync(string query) =>
            (await o.ListAsync(http, query)).Items().Select(i => i.Id()).Order().ToArray();
        Guid[] Ids(params JsonElement[] orders) => orders.Select(x => x.Id()).Order().ToArray();

        Assert.Equal(Ids(without, draftWithout, withPartner, ofSecond), await IdsAsync(""));
        Assert.Equal(Ids(withPartner), await IdsAsync($"?{o.PartnerId}={o.PartnerOf(s).Id()}"));
        Assert.Equal(Ids(ofSecond), await IdsAsync($"?{o.PartnerId}={second.Id()}"));
        Assert.Empty(await IdsAsync($"?{o.PartnerId}={Guid.CreateVersion7()}"));
        // The other filters and the search find it like any other order.
        Assert.Equal(Ids(without, withPartner), await IdsAsync("?status=confirmed"));
        Assert.Equal(Ids(without, draftWithout, withPartner, ofSecond), await IdsAsync($"?warehouseId={s.W1}"));
        Assert.Equal(Ids(without, withPartner), await IdsAsync($"?status=confirmed&warehouseId={s.W1}"));
    }

    // ---- AC-88 ----

    [Fact]
    public async Task AC88_The_two_partner_rules_are_separate()
    {
        var s = await OptionalAsync();
        var other = o == OrderApi.Purchase ? OrderApi.Sales : OrderApi.Purchase;

        using var refused = await other.PostAsync(s.Http, other.NoPartner(s, (s.A, 1, null, 1m)));
        var accepted = await o.CreateAsync(s.Http, o.NoPartner(s, (s.A, 1, null, 1m)));

        await Rules.PartnerRequiredAsync(refused, other);
        Assert.Equal(0, (await other.ListAsync(s.Http)).Total());
        Rules.AssertNoPartner(o, accepted);
        Rules.AssertAtDefault(await Rules.GetAsync(s.Http, Rules.PartnerRule(other)), Rules.PartnerRule(other));
    }

    // ---- AC-89 ----

    [Fact]
    public async Task AC89_Back_on_the_rule_judges_the_next_save_and_not_the_life_of_an_order_without_a_partner()
    {
        var s = await OptionalAsync();
        var http = s.Http;
        await o.PrepareStockAsync(s);
        var draft = await o.CreateAsync(http, o.NoPartner(s, (s.A, 10, null, 2.5m)));
        var keptDraft = await o.CreateAsync(http, o.NoPartner(s, (s.B, 3, null, 1m)));
        var confirmed = await OrderedWithoutPartnerAsync(s, (s.A, 10, null, 1m));
        var list = await o.ListAsync(http);

        Rules.AssertTenantValue(await Rules.SetAsync(http, Rule, true), Rule, true, s.Tenant.ApiKeyId);

        // R15: both read unchanged, updatedAt included.
        McpAssert.JsonEqual(draft, await o.GetAsync(http, draft.Id()), "The draft changed with the rule");
        McpAssert.JsonEqual(confirmed, await o.GetAsync(http, confirmed.Id()), "The confirmed order changed with the rule");
        McpAssert.JsonEqual(list, await o.ListAsync(http), "The list changed with the rule");

        // R37: the draft confirms as it is and gets its number.
        var nowConfirmed = await o.ConfirmAsync(http, draft.Id());

        Assert.Equal(("confirmed", o.Number(2)), (nowConfirmed.Str("status"), nowConfirmed.Number()));
        Rules.AssertNoPartner(o, nowConfirmed);

        // The confirmed one is fulfilled, closed and reopened as before.
        var document = await o.FulfilAsync(http, confirmed, 1, 4);
        JsonBody.AssertNull(document, "partner");
        await o.AssertProgressAsync(http, confirmed.Id(), "confirmed", "partial", (4m, 6m));
        Assert.Equal("closed", (await o.CloseAsync(http, confirmed.Id())).Str("status"));
        Assert.Equal("confirmed", (await o.ReopenAsync(http, confirmed.Id())).Str("status"));
        JsonBody.AssertNull(await Stock.ReverseAsync(http, document.Id(), Stock.NextDay), "partner");

        // Only a save is judged: replacing a draft without a partner with its own body, and a new order.
        using var replace = await o.PutAsync(http, keptDraft.Id(), o.NoPartnerReplacement(s, (s.B, 3, null, 1m)));
        using var create = await o.PostAsync(http, o.NoPartner(s, (s.A, 1, null, 1m)));

        await Rules.PartnerRequiredAsync(replace, o);
        await Rules.PartnerRequiredAsync(create, o);
        await o.AssertUnchangedAsync(http, keptDraft);
        Assert.Equal(3, await OrderCountAsync(s));
        // The draft is not stuck: it can be given a partner, or deleted, or confirmed as it is.
        var named = await o.ReplaceAsync(http, keptDraft.Id(), o.Replacement(s, (s.B, 3, null, 1m)));
        Assert.Equal(o.PartnerOf(s).Id(), named.GetProperty(o.Partner).Id());
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC89_R16_Reset_has_the_same_effect_as_setting_the_rule_back()
    {
        var s = await OptionalAsync();
        var draft = await o.CreateAsync(s.Http, o.NoPartner(s, (s.A, 1, null, 1m)));

        Rules.AssertAtDefault(await Rules.ResetAsync(s.Http, Rule), Rule);

        using var create = await o.PostAsync(s.Http, o.NoPartner(s, (s.A, 1, null, 1m)));
        await Rules.PartnerRequiredAsync(create, o);
        McpAssert.JsonEqual(draft, await o.GetAsync(s.Http, draft.Id()));
        Assert.Equal("confirmed", (await o.ConfirmAsync(s.Http, draft.Id())).Str("status"));
    }
}

/// <summary>Spec 012, AC-80 to AC-86, AC-88, AC-89: <c>purchase.partnerRequired</c>.</summary>
[Collection(XerpCollection.Name)]
public class PurchasePartnerRequiredTests(XerpFixture app) : RulePartnerRequiredTests(app, OrderApi.Purchase);

/// <summary>Spec 012, AC-87 and the mirror of AC-80 to AC-89: <c>sales.partnerRequired</c>.</summary>
[Collection(XerpCollection.Name)]
public class SalesPartnerRequiredTests(XerpFixture app) : RulePartnerRequiredTests(app, OrderApi.Sales);
