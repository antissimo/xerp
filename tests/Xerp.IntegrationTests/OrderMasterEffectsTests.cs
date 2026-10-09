using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-80 to AC-82: a partner, warehouse, article and unit of measure named by an order — draft,
/// confirmed or closed — cannot be deleted; an order line freezes its article's type and base unit; a draft
/// order line holds the conversion it uses. Written once for every kind of order (ADR-0016).
/// </summary>
public abstract class OrderMasterEffectsTests(XerpFixture app, OrderApi o)
{
    private static readonly MasterApi P = MasterApi.Partners;
    private static readonly MasterApi W = MasterApi.Warehouses;

    private static Task<HttpResponseMessage> DeleteArticleAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Art.Path}/{id}");

    private static async Task AssertDeletedAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task AssertInUseAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        await HttpAssert.InUseAsync(response);
    }

    // ---- AC-80 ----

    [Fact]
    public async Task AC80_A_partner_on_a_draft_order_cannot_be_deleted_until_the_draft_is_deleted()
    {
        var s = await Orders.SetupAsync(app);
        var partner = o.PartnerOf(s);
        var first = await o.DraftAsync(s, (s.A, 1, null, 1m));
        var second = await o.DraftAsync(s, (s.B, 1, null, 1m));

        await AssertInUseAsync(P.DeleteAsync(s.Http, partner.Id()));
        await P.AssertUnchangedAsync(s.Http, partner);
        // R37: used until the last draft that names it is gone.
        await AssertDeletedAsync(o.DeleteAsync(s.Http, first.Id()));
        await AssertInUseAsync(P.DeleteAsync(s.Http, partner.Id()));
        await AssertDeletedAsync(o.DeleteAsync(s.Http, second.Id()));

        await AssertDeletedAsync(P.DeleteAsync(s.Http, partner.Id()));
        // The partner the orders never named was unused all along (spec 004 unchanged).
        await AssertDeletedAsync(P.DeleteAsync(s.Http, o.WrongPartnerOf(s).Id()));
    }

    [Fact]
    public async Task AC80_R37_A_partner_replaced_on_a_draft_is_unused_again()
    {
        var s = await Orders.SetupAsync(app);
        var second = await o.NewPartnerAsync(s.Http, "P2");
        var draft = await o.DraftAsync(s, (s.A, 1, null, 1m));

        await o.ReplaceAsync(s.Http, draft.Id(), o.Replacement(second.Id(), s.W1, (s.A, 1, null, 1m)));

        await AssertDeletedAsync(P.DeleteAsync(s.Http, o.PartnerOf(s).Id()));
        await AssertInUseAsync(P.DeleteAsync(s.Http, second.Id()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC80_A_partner_on_a_confirmed_or_closed_order_can_be_changed_but_not_deleted(bool closed)
    {
        var s = await Orders.SetupAsync(app);
        var partner = o.PartnerOf(s).Id();
        var order = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));
        if (closed)
            order = await o.CloseAsync(s.Http, order.Id());

        await AssertInUseAsync(P.DeleteAsync(s.Http, partner));
        // Renamed, deactivated and changed to the other role only: all allowed (R2, R37).
        await Orders.ChangePartnerAsync(s.Http, partner, b => { b["code"] = "NEW-CODE"; b["name"] = "Renamed Ltd"; });
        await Orders.SetPartnerActiveAsync(s.Http, partner, false);
        var changed = await o.RemoveRoleAsync(s.Http, partner);

        Assert.False(changed.Bool(o.Role));
        Assert.False(changed.Bool("isActive"));
        var read = await o.GetAsync(s.Http, order.Id());
        var summary = read.GetProperty(o.Partner);
        Assert.Equal((partner, "NEW-CODE", "Renamed Ltd"), (summary.Id(), summary.Str("code"), summary.Str("name")));
        Assert.Equal(order.Str("status"), read.Str("status"));
        Assert.Equal(order.Number(), read.Number());
        await AssertInUseAsync(P.DeleteAsync(s.Http, partner));
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_A_warehouse_and_an_article_on_a_draft_order_cannot_be_deleted_until_the_draft_is_deleted()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.CreateAsync(s.Http, o.Body(o.PartnerOf(s).Id(), s.W2, (s.B, 1, null, 1m)));

        await AssertInUseAsync(W.DeleteAsync(s.Http, s.W2));
        await AssertInUseAsync(DeleteArticleAsync(s.Http, s.B));
        await W.AssertUnchangedAsync(s.Http, s.U.S.Warehouse2);
        McpAssert.JsonEqual(s.U.S.ArticleB, await Art.GetAsync(s.Http, s.B), "A refused delete changed the article");

        await AssertDeletedAsync(o.DeleteAsync(s.Http, draft.Id()));

        await AssertDeletedAsync(W.DeleteAsync(s.Http, s.W2));
        await AssertDeletedAsync(DeleteArticleAsync(s.Http, s.B));
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("closed")]
    public async Task AC81_R37_A_warehouse_and_an_article_on_a_confirmed_or_closed_order_stay_used(string status)
    {
        var s = await Orders.SetupAsync(app);
        var order = await o.OrderedAsync(s, s.W2, (s.B, 1, null, 1m));
        if (status == "closed")
            await o.CloseAsync(s.Http, order.Id());

        await AssertInUseAsync(W.DeleteAsync(s.Http, s.W2));
        await AssertInUseAsync(DeleteArticleAsync(s.Http, s.B));
        // They can be renamed and deactivated; the order shows the current code and name.
        await W.ReplaceAsync(s.Http, s.W2, Stock.WarehouseBody(s.U.S.Warehouse2).With("name", "Renamed").With("isActive", false));
        await Stock.ReplaceArticleAsync(s.Http, s.B, Stock.ArticleBody(s.U.S.ArticleB).With("code", "B-NEW").With("isActive", false));

        var read = await o.GetAsync(s.Http, order.Id());
        Assert.Equal("Renamed", read.GetProperty("warehouse").Str("name"));
        Assert.Equal("B-NEW", read.OrderLines()[0].GetProperty("article").Str("code"));
        Assert.Equal(status, read.Str("status"));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("confirmed")]
    [InlineData("closed")]
    public async Task AC81_R38_An_order_line_freezes_the_type_and_base_unit_of_its_article(string status)
    {
        var s = await Orders.SetupAsync(app);
        var order = await o.DraftAsync(s, (s.A, 1, null, 1m), (s.B, 1, null, 1m));
        if (status != "draft")
            order = await o.ConfirmAsync(s.Http, order.Id());
        if (status == "closed")
            order = await o.CloseAsync(s.Http, order.Id());

        using var type = await Stock.PutArticleAsync(s.Http, s.B, Stock.ArticleBody(s.U.S.ArticleB).With("type", "service"));
        using var baseUnit = await Stock.PutArticleAsync(s.Http, s.B, Stock.ArticleBody(s.U.S.ArticleB).With("baseUnitId", s.Pack.ToString()));

        Assert.Equal(new[] { "type" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(type, "IN_USE", "type")));
        Assert.Equal(new[] { "baseUnitId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(baseUnit, "IN_USE", "baseUnitId")));
        McpAssert.JsonEqual(s.U.S.ArticleB, await Art.GetAsync(s.Http, s.B), "A refused change altered the article");
        // Name, code, description and isActive stay editable.
        await Stock.ReplaceArticleAsync(s.Http, s.B, Stock.ArticleBody(s.U.S.ArticleB).With("name", "Renamed B"));
        Assert.Equal("Renamed B", (await o.GetAsync(s.Http, order.Id())).OrderLines()[1].GetProperty("article").Str("name"));
    }

    [Fact]
    public async Task AC81_R38_An_article_removed_from_every_draft_order_line_is_free_again()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 1, null, 1m), (s.B, 1, null, 1m));

        await o.ReplaceAsync(s.Http, draft.Id(), o.Replacement(s, (s.A, 1, null, 1m)));

        var changed = await Stock.ReplaceArticleAsync(s.Http, s.B, Stock.ArticleBody(s.U.S.ArticleB).With("type", "service"));
        Assert.Equal("service", changed.Str("type"));
        await AssertDeletedAsync(DeleteArticleAsync(s.Http, s.B));
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_A_draft_order_line_holds_its_conversion_and_a_confirmed_one_carries_its_own_factor()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 5, s.Box, 30m));

        using var refused = await Units.DeleteAsync(s.Http, s.A, s.Box);
        await HttpAssert.InUseAsync(refused);
        Assert.Equal(12m, await Units.FactorOrNullAsync(s.Http, s.A, s.Box));

        var confirmed = await o.ConfirmAsync(s.Http, draft.Id());
        await Units.RemoveAsync(s.Http, s.A, s.Box);

        var read = await o.GetAsync(s.Http, draft.Id());
        McpAssert.JsonEqual(confirmed, read, "Deleting the conversion changed the confirmed order");
        Units.AssertLine(read.OrderLines()[0], "box", quantity: 5m, factor: 12m, baseQuantity: 60m);
        Assert.Equal(60m, read.OrderLines()[0].Outstanding());

        // R37: the confirmed order line still names the unit of measure.
        await AssertInUseAsync(Units.DeleteUnitAsync(s.Http, s.Box));
        await o.CloseAsync(s.Http, draft.Id());
        await AssertInUseAsync(Units.DeleteUnitAsync(s.Http, s.Box));
        // A unit no order and no article names is deleted as before.
        await AssertDeletedAsync(Units.DeleteUnitAsync(s.Http, s.Pack));
    }

    [Fact]
    public async Task AC82_R38_The_conversion_is_free_again_when_no_draft_order_line_uses_it()
    {
        var s = await Orders.SetupAsync(app);
        var first = await o.DraftAsync(s, (s.A, 5, s.Box, 30m));
        var second = await o.DraftAsync(s, (s.A, 1, s.Box, 30m), (s.B, 1, null, 1m));

        await AssertInUseAsync(Units.DeleteAsync(s.Http, s.A, s.Box));
        await AssertDeletedAsync(o.DeleteAsync(s.Http, first.Id()));
        await AssertInUseAsync(Units.DeleteAsync(s.Http, s.A, s.Box));
        // The second draft stops using boxes: the same article in its base unit does not hold the conversion.
        await o.ReplaceAsync(s.Http, second.Id(), o.Replacement(s, (s.A, 12, null, 2.5m)));

        await Units.RemoveAsync(s.Http, s.A, s.Box);
        Assert.Null(await Units.FactorOrNullAsync(s.Http, s.A, s.Box));
    }

    [Fact]
    public async Task AC82_E8_A_draft_order_line_does_not_stop_its_factor_from_changing()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 5, s.Box, 30m));

        await Units.SetAsync(s.Http, s.A, s.Box, 24);

        var read = await o.GetAsync(s.Http, draft.Id());
        Units.AssertLine(read.OrderLines()[0], "box", quantity: 5m, factor: 24m, baseQuantity: 120m);
        Assert.Equal(draft.Dec("totalAmount"), read.Dec("totalAmount"));
    }
}
