using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-30 to AC-35 (confirmation), AC-63 (refused transitions) and AC-65 (lists): an order is a draft
/// until it is confirmed; confirmation gives it its number and makes it immutable; close and reopen switch it
/// between <c>confirmed</c> and <c>closed</c>. Written once for every kind of order (ADR-0016).
/// </summary>
public abstract class OrderLifecycleTests(XerpFixture app, OrderApi o)
{
    // ---- AC-30 ----

    [Fact]
    public async Task AC30_Confirm_assigns_the_number_and_opens_the_outstanding_quantity()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        using var response = await o.SendAsync(s.Http, draft.Id(), "confirm");

        var confirmed = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(draft.Id(), confirmed.Id());
        Assert.Equal("confirmed", confirmed.Str("status"));
        Assert.Equal(o.Number(1), confirmed.Number());
        Assert.Equal(JsonValueKind.String, confirmed.GetProperty("confirmedAt").ValueKind);
        Assert.True(confirmed.GetProperty("confirmedAt").GetDateTimeOffset() >= draft.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(s.Tenant.ApiKeyId, confirmed.GetProperty("confirmedBy").GetGuid());
        JsonBody.AssertNull(confirmed, "closedAt", "closedBy");
        var line = Assert.Single(confirmed.OrderLines());
        Assert.Equal((0m, 10m), (o.DoneOf(line), line.Outstanding()));
        Units.AssertLine(line, "pcs", quantity: 10m, factor: 1m, baseQuantity: 10m);
        Assert.Equal("none", o.ProgressOf(confirmed));
        Assert.Equal(25m, confirmed.Dec("totalAmount"));
        // R17: confirmation is attributed by confirmedBy / confirmedAt, not by the audit fields of an edit.
        Assert.Equal(draft.GetProperty("updatedAt").GetDateTimeOffset(), confirmed.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(draft.GetProperty("updatedBy").GetGuid(), confirmed.GetProperty("updatedBy").GetGuid());

        McpAssert.JsonEqual(confirmed, await o.GetAsync(s.Http, draft.Id()));
        using var byNumber = await o.ByNumberAsync(s.Http, o.Number(1));
        using var lowerCase = await o.ByNumberAsync(s.Http, o.Number(1).ToLowerInvariant());
        McpAssert.JsonEqual(confirmed, await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK));
        McpAssert.JsonEqual(confirmed, await HttpAssert.JsonAsync(lowerCase, HttpStatusCode.OK));

        // An order moves no stock (S2).
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC30_By_number_knows_only_confirmed_numbers()
    {
        var s = await Orders.SetupAsync(app);
        await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        using var none = await o.ByNumberAsync(s.Http, o.Number(1));
        using var documentNumber = await o.ByNumberAsync(s.Http, o.DocumentNumber(1));

        await HttpAssert.NotFoundAsync(none);
        await HttpAssert.NotFoundAsync(documentNumber);
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_A_confirmed_order_is_immutable()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));

        using var put = await o.PutAsync(s.Http, confirmed.Id(), o.Replacement(s, (s.B, 1, null, 1m)));
        using var sameValues = await o.PutAsync(s.Http, confirmed.Id(), o.Replacement(s, (s.A, 10, null, 2.5m)));
        using var delete = await o.DeleteAsync(s.Http, confirmed.Id());
        using var confirmAgain = await o.SendAsync(s.Http, confirmed.Id(), "confirm");

        await Stock.ConflictAsync(put, "INVALID_STATE");
        await Stock.ConflictAsync(sameValues, "INVALID_STATE");
        await Stock.ConflictAsync(delete, "INVALID_STATE");
        await Stock.ConflictAsync(confirmAgain, "INVALID_STATE");
        await o.AssertUnchangedAsync(s.Http, confirmed);
        // The refused second confirmation consumed no number.
        Assert.Equal(o.Number(2), (await o.OrderedAsync(s, (s.B, 1, null, 1m))).Number());
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_E8_The_factor_follows_the_article_until_confirmation_and_is_frozen_by_it()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 5, s.Box, 30m));
        Units.AssertLine(draft.OrderLines()[0], "box", quantity: 5m, factor: 12m, baseQuantity: 60m);

        await Units.SetAsync(s.Http, s.A, s.Box, 10);
        var read = await o.GetAsync(s.Http, draft.Id());

        Units.AssertLine(read.OrderLines()[0], "box", quantity: 5m, factor: 10m, baseQuantity: 50m);
        Assert.Equal((150m, 150m), (read.OrderLines()[0].Dec("lineAmount"), read.Dec("totalAmount")));
        Assert.Equal(0m, read.OrderLines()[0].Outstanding());

        var confirmed = await o.ConfirmAsync(s.Http, draft.Id());

        Units.AssertLine(confirmed.OrderLines()[0], "box", quantity: 5m, factor: 10m, baseQuantity: 50m);
        Assert.Equal(50m, confirmed.OrderLines()[0].Outstanding());

        await Units.SetAsync(s.Http, s.A, s.Box, 20);

        var after = await o.GetAsync(s.Http, draft.Id());
        McpAssert.JsonEqual(confirmed, after, "A factor change altered a confirmed order");
        Assert.Equal(50m, await o.OnHandAsync(s.Http, s.A, s.W1));
        // The same holds while the order is closed and after it is reopened.
        await o.CloseAsync(s.Http, draft.Id());
        await Units.SetAsync(s.Http, s.A, s.Box, 7);
        var reopened = await o.ReopenAsync(s.Http, draft.Id());
        Units.AssertLine(reopened.OrderLines()[0], "box", quantity: 5m, factor: 10m, baseQuantity: 50m);
        Assert.Equal(150m, reopened.Dec("totalAmount"));
    }

    // ---- AC-33 ----

    [Theory]
    [InlineData("partner inactive")]
    [InlineData("warehouse inactive")]
    [InlineData("second article inactive")]
    [InlineData("partner without the role")]
    public async Task AC33_Confirmation_needs_active_masters_and_the_role(string @case)
    {
        var s = await Orders.SetupAsync(app);
        var partner = o.PartnerOf(s).Id();
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m), (s.B, 1, null, 1m));

        async Task SetAsync(bool restored)
        {
            switch (@case)
            {
                case "partner inactive": await Orders.SetPartnerActiveAsync(s.Http, partner, restored); break;
                case "warehouse inactive": await Stock.SetWarehouseActiveAsync(s.Http, s.W1, restored); break;
                case "second article inactive": await Stock.SetArticleActiveAsync(s.Http, s.B, restored); break;
                default:
                    if (restored) await o.RestoreRoleAsync(s.Http, partner);
                    else await o.RemoveRoleAsync(s.Http, partner);
                    break;
            }
        }

        var (code, key) = @case switch
        {
            "partner inactive" => ("REFERENCE_INACTIVE", o.PartnerId),
            "warehouse inactive" => ("REFERENCE_INACTIVE", "warehouseId"),
            "second article inactive" => ("REFERENCE_INACTIVE", "lines[1].articleId"),
            _ => ("PARTNER_ROLE_MISSING", o.PartnerId),
        };
        await SetAsync(restored: false);

        using var refused = await o.SendAsync(s.Http, draft.Id(), "confirm");

        var problem = await Stock.ConflictAsync(refused, code, key);
        Assert.Equal(new[] { key }, McpAssert.ErrorKeys(problem));
        var still = await o.GetAsync(s.Http, draft.Id());
        Assert.Equal("draft", still.Str("status"));
        JsonBody.AssertNull(still, "number", "confirmedAt", "confirmedBy");
        Assert.Equal(0m, await o.OnHandAsync(s.Http, s.A, s.W1));

        await SetAsync(restored: true);
        var confirmed = await o.ConfirmAsync(s.Http, draft.Id());

        // The refused confirmation consumed no number.
        Assert.Equal(o.Number(1), confirmed.Number());
    }

    [Fact]
    public async Task AC33_R12_Order_of_checks_at_confirmation()
    {
        // Header keys together first, then lines (005/R13), then the role, then conversion.
        var s = await Orders.SetupAsync(app);
        var partner = o.PartnerOf(s).Id();
        await Units.SetAsync(s.Http, s.A, s.Pack, 1);
        var draft = await o.DraftAsync(s, (s.B, 1, null, 1m), (s.A, 0.000001m, s.Pack, 1m));
        // Not convertible any more, partner without the role and inactive, warehouse inactive, article inactive.
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        await o.RemoveRoleAsync(s.Http, partner);
        await Orders.SetPartnerActiveAsync(s.Http, partner, false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);

        async Task<string[]> RefusedAsync(string code)
        {
            using var response = await o.SendAsync(s.Http, draft.Id(), "confirm");
            return McpAssert.ErrorKeys(await Stock.ConflictAsync(response, code));
        }

        Assert.Equal(new[] { o.PartnerId, "warehouseId" }, await RefusedAsync("REFERENCE_INACTIVE"));
        await Orders.SetPartnerActiveAsync(s.Http, partner, true);
        Assert.Equal(new[] { "warehouseId" }, await RefusedAsync("REFERENCE_INACTIVE"));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        Assert.Equal(new[] { "lines[0].articleId" }, await RefusedAsync("REFERENCE_INACTIVE"));
        await Stock.SetArticleActiveAsync(s.Http, s.B, true);
        Assert.Equal(new[] { o.PartnerId }, await RefusedAsync("PARTNER_ROLE_MISSING"));
        await o.RestoreRoleAsync(s.Http, partner);
        Assert.Equal(new[] { "lines[1].quantity" }, await RefusedAsync("QUANTITY_NOT_CONVERTIBLE"));
        Assert.Equal("draft", (await o.GetAsync(s.Http, draft.Id())).Str("status"));

        await Units.SetAsync(s.Http, s.A, s.Pack, 2);
        var confirmed = await o.ConfirmAsync(s.Http, draft.Id());

        Assert.Equal(o.Number(1), confirmed.Number());
        Units.AssertLine(confirmed.OrderLines()[1], "pack", quantity: 0.000001m, factor: 2m, baseQuantity: 0.000002m);
    }

    [Fact]
    public async Task AC33_R2_A_partner_that_loses_the_role_or_is_deactivated_later_leaves_confirmed_orders_alone()
    {
        var s = await Orders.SetupAsync(app);
        var partner = o.PartnerOf(s).Id();
        var confirmed = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));

        await o.RemoveRoleAsync(s.Http, partner);
        await Orders.SetPartnerActiveAsync(s.Http, partner, false);

        await o.AssertUnchangedAsync(s.Http, confirmed);
        var closed = await o.CloseAsync(s.Http, confirmed.Id());
        var reopened = await o.ReopenAsync(s.Http, confirmed.Id());
        Assert.Equal(("closed", "confirmed"), (closed.Str("status"), reopened.Str("status")));
        Assert.Equal(10m, await o.OnHandAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_Numbers_are_gapless_in_confirmation_order_and_independent_of_stock_documents()
    {
        var s = await Orders.SetupAsync(app);
        var first = await o.DraftAsync(s, (s.A, 1, null, 1m));
        var second = await o.DraftAsync(s, (s.A, 2, null, 1m));
        var third = await o.DraftAsync(s, (s.A, 3, null, 1m));
        var deleted = await o.DraftAsync(s, (s.A, 4, null, 1m));
        var left = await o.DraftAsync(s, (s.A, 5, null, 1m));

        // Confirmed in another order than created; a stock document is posted and a draft deleted in between.
        var n1 = (await o.ConfirmAsync(s.Http, third.Id())).Number();
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.B, 5);
        using var delete = await o.DeleteAsync(s.Http, deleted.Id());
        var n2 = (await o.ConfirmAsync(s.Http, first.Id())).Number();
        using var refused = await o.SendAsync(s.Http, first.Id(), "confirm");
        var n3 = (await o.ConfirmAsync(s.Http, second.Id())).Number();

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(new[] { o.Number(1), o.Number(2), o.Number(3) }, new[] { n1, n2, n3 });
        Assert.Equal("SR-000001", receipt.Number());
        Assert.Null((await o.GetAsync(s.Http, left.Id())).Number());
        // Close and reopen keep the number and consume none.
        await o.CloseAsync(s.Http, first.Id());
        Assert.Equal(o.Number(2), (await o.ReopenAsync(s.Http, first.Id())).Number());
        Assert.Equal(o.Number(4), (await o.ConfirmAsync(s.Http, left.Id())).Number());
    }

    [Fact]
    public async Task AC34_Ten_drafts_confirmed_in_parallel_get_ten_consecutive_numbers()
    {
        var s = await Orders.SetupAsync(app);
        var drafts = new List<Guid>();
        for (var i = 0; i < 10; i++)
            drafts.Add((await o.DraftAsync(s, (s.A, i + 1, null, 1m))).Id());

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => o.SendAsync(s.Http, id, "confirm"))));

        var numbers = new List<string?>();
        foreach (var response in responses)
        {
            numbers.Add((await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Number());
            response.Dispose();
        }
        Assert.Equal(Enumerable.Range(1, 10).Select(o.Number), numbers.Order(StringComparer.Ordinal));
        Assert.Equal(10, (await o.ListAsync(s.Http, "?status=confirmed")).Total());
    }

    [Fact]
    public async Task AC34_E17_The_same_draft_confirmed_five_times_in_parallel_is_confirmed_once()
    {
        var s = await Orders.SetupAsync(app);
        for (var round = 1; round <= 3; round++)
        {
            var draft = await o.DraftAsync(s, (s.A, 10, null, 1m));

            var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => o.SendAsync(s.Http, draft.Id(), "confirm"))));

            Assert.Equal(new[] { 200, 409, 409, 409, 409 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
            foreach (var response in responses)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                    await Stock.ConflictAsync(response, "INVALID_STATE");
                else
                    Assert.Equal(o.Number(round), (await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Number());
                response.Dispose();
            }
            Assert.Equal(o.Number(round), (await o.GetAsync(s.Http, draft.Id())).Number());
        }
        // One number per order: the fifteen attempts consumed three.
        Assert.Equal(o.Number(4), (await o.OrderedAsync(s, (s.B, 1, null, 1m))).Number());
        Assert.Equal(10m * 3, await o.OnHandAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-35 ----

    [Fact]
    public async Task AC35_S1_Each_step_is_attributed_to_the_key_that_made_it()
    {
        var s = await Orders.SetupAsync(app);
        var k2 = await Keys.CreateAsync(app, s.Http, "approver", "human");
        var k3 = await Keys.CreateAsync(app, s.Http, "agent", "agent");
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        var confirmed = await o.ConfirmAsync(k2.Client, draft.Id());
        var closed = await o.CloseAsync(k3.Client, draft.Id());

        Assert.Equal(s.Tenant.ApiKeyId, confirmed.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, confirmed.GetProperty("updatedBy").GetGuid());
        Assert.Equal(k2.Id, confirmed.GetProperty("confirmedBy").GetGuid());
        Assert.Equal(k2.Id, closed.GetProperty("confirmedBy").GetGuid());
        Assert.Equal(k3.Id, closed.GetProperty("closedBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, closed.GetProperty("updatedBy").GetGuid());
        Assert.Equal(draft.GetProperty("updatedAt").GetDateTimeOffset(), closed.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(confirmed.GetProperty("confirmedAt").GetDateTimeOffset(), closed.GetProperty("confirmedAt").GetDateTimeOffset());

        var reopened = await o.ReopenAsync(s.Http, draft.Id());

        // R17: who reopened is the audit log's; the order shows only that it is no longer closed.
        JsonBody.AssertNull(reopened, "closedAt", "closedBy");
        Assert.Equal(k2.Id, reopened.GetProperty("confirmedBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, reopened.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(confirmed, reopened, "Close and reopen changed more than the status");
    }

    // ---- AC-63 ----

    [Fact]
    public async Task AC63_Refused_transitions_change_nothing()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));
        var confirmed = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));
        var closed = await o.CloseAsync(s.Http, (await o.OrderedAsync(s, (s.A, 10, null, 2.5m))).Id());

        async Task RefusedAsync(JsonElement order, string action)
        {
            using var response = action switch
            {
                "put" => await o.PutAsync(s.Http, order.Id(), o.Replacement(s, (s.B, 1, null, 1m))),
                "delete" => await o.DeleteAsync(s.Http, order.Id()),
                _ => await o.SendAsync(s.Http, order.Id(), action),
            };
            var problem = await Stock.ConflictAsync(response, "INVALID_STATE");
            Assert.Empty(McpAssert.ErrorKeys(problem));
            await o.AssertUnchangedAsync(s.Http, order);
        }

        await RefusedAsync(draft, "close");
        await RefusedAsync(draft, "reopen");
        await RefusedAsync(confirmed, "reopen");
        await RefusedAsync(closed, "close");
        await RefusedAsync(closed, "confirm");
        await RefusedAsync(closed, "put");
        await RefusedAsync(closed, "delete");

        // Only the confirmed order is on order; no number was consumed by anything refused.
        Assert.Equal(10m, await o.OnHandAsync(s.Http, s.A, s.W1));
        Assert.Equal(o.Number(3), (await o.ConfirmAsync(s.Http, draft.Id())).Number());
    }

    [Fact]
    public async Task AC63_R15_R16_Close_and_reopen_can_be_repeated_and_keep_number_and_lines()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 5, s.Box, 30m), (s.B, 2, null, 1m));

        for (var round = 0; round < 3; round++)
        {
            var closed = await o.CloseAsync(s.Http, confirmed.Id());

            Assert.Equal("closed", closed.Str("status"));
            Assert.Equal(JsonValueKind.String, closed.GetProperty("closedAt").ValueKind);
            Assert.Equal(s.Tenant.ApiKeyId, closed.GetProperty("closedBy").GetGuid());
            Assert.Equal(confirmed.Number(), closed.Number());
            Assert.Equal([0m, 0m], closed.OrderLines().Select(l => l.Outstanding()));
            Assert.Equal([60m, 2m], closed.OrderLines().Select(l => l.Dec("baseQuantity")));
            Assert.Equal(152m, closed.Dec("totalAmount"));
            Assert.Equal(0m, await o.OnHandAsync(s.Http, s.A, s.W1));
            McpAssert.JsonEqual(closed, await o.GetAsync(s.Http, confirmed.Id()));

            var reopened = await o.ReopenAsync(s.Http, confirmed.Id());

            McpAssert.JsonEqual(confirmed, reopened, "Close and reopen changed the order");
            Assert.Equal(60m, await o.OnHandAsync(s.Http, s.A, s.W1));
        }
    }

    [Fact]
    public async Task AC63_R30_An_order_closed_five_times_in_parallel_is_closed_once()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10, null, 1m));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => o.SendAsync(s.Http, confirmed.Id(), "close"))));

        Assert.Equal(new[] { 200, 409, 409, 409, 409 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
                await Stock.ConflictAsync(response, "INVALID_STATE");
            response.Dispose();
        }
        Assert.Equal("closed", (await o.GetAsync(s.Http, confirmed.Id())).Str("status"));
    }

    // ---- AC-65 (spec 010: AC-64) ----

    private sealed record Listed(OrderSetup S, JsonElement Second, JsonElement Draft, JsonElement None, JsonElement Partial,
        JsonElement Full, JsonElement Closed);

    /// <summary>
    /// Five orders, created in this order: a draft with a reference; confirmed, nothing fulfilled; confirmed,
    /// partly fulfilled; confirmed, fully fulfilled; closed, partly fulfilled, for a second partner and W2.
    /// </summary>
    private async Task<Listed> ListedAsync()
    {
        var s = await Orders.SetupAsync(app);
        await o.PrepareStockAsync(s);
        var second = await o.NewPartnerAsync(s.Http, "P2", "Second partner");
        var draft = await o.CreateAsync(s.Http, o.Body(s, (s.A, 1, null, 1m), (s.B, 1, null, 1m)).With("reference", "Offer-ABC/7"));
        var none = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));
        var partial = await o.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 2m), (s.A, 3, null, 0m));
        await o.FulfilAsync(s.Http, partial, 2, 5);
        var full = await o.OrderedAsync(s, (s.B, 4, null, 1m));
        await o.FulfilAsync(s.Http, full, 1, 4);
        var closed = await o.ConfirmAsync(s.Http, (await o.CreateAsync(s.Http, o.Body(second.Id(), s.W2, (s.A, 10, null, 1m)))).Id());
        await o.FulfilAsync(s.Http, closed, 1, 4);
        await o.CloseAsync(s.Http, closed.Id());
        return new Listed(s, second, draft, none, partial, full, closed);
    }

    private async Task<Guid[]> IdsAsync(HttpClient client, string query)
    {
        var list = await o.ListAsync(client, query);
        Assert.Equal(list.Items().Length, list.Total());
        return list.Items().Select(i => i.Id()).ToArray();
    }

    [Fact]
    public async Task AC65_S010_AC64_Lists_filter_combine_search_and_are_newest_first()
    {
        var l = await ListedAsync();
        var http = l.S.Http;
        Guid[] Ids(params JsonElement[] orders) => orders.Select(x => x.Id()).ToArray();

        // Newest first.
        Assert.Equal(Ids(l.Closed, l.Full, l.Partial, l.None, l.Draft), await IdsAsync(http, ""));
        Assert.Equal(Ids(l.Draft), await IdsAsync(http, "?status=draft"));
        Assert.Equal(Ids(l.Full, l.Partial, l.None), await IdsAsync(http, "?status=confirmed"));
        Assert.Equal(Ids(l.Closed), await IdsAsync(http, "?status=closed"));
        Assert.Equal(Ids(l.None, l.Draft), await IdsAsync(http, $"?{o.Progress}=none"));
        Assert.Equal(Ids(l.Closed, l.Partial), await IdsAsync(http, $"?{o.Progress}=partial"));
        Assert.Equal(Ids(l.Full), await IdsAsync(http, $"?{o.Progress}=full"));
        // Combined.
        Assert.Equal(Ids(l.Partial), await IdsAsync(http, $"?status=confirmed&{o.Progress}=partial"));
        Assert.Equal(Ids(l.None), await IdsAsync(http, $"?status=confirmed&{o.Progress}=none"));
        Assert.Empty(await IdsAsync(http, $"?status=draft&{o.Progress}=full"));
        Assert.Equal(Ids(l.Closed), await IdsAsync(http, $"?{o.PartnerId}={l.Second.Id()}"));
        Assert.Equal(Ids(l.Full, l.Partial, l.None, l.Draft), await IdsAsync(http, $"?{o.PartnerId}={o.PartnerOf(l.S).Id()}"));
        Assert.Equal(Ids(l.Closed), await IdsAsync(http, $"?warehouseId={l.S.W2}"));
        Assert.Equal(Ids(l.Full, l.Partial, l.None), await IdsAsync(http, $"?warehouseId={l.S.W1}&status=confirmed"));
        Assert.Empty(await IdsAsync(http, $"?{o.PartnerId}={l.Second.Id()}&warehouseId={l.S.W1}"));
        Assert.Empty(await IdsAsync(http, $"?{o.PartnerId}={Guid.CreateVersion7()}"));
        Assert.Empty(await IdsAsync(http, $"?warehouseId={Guid.CreateVersion7()}"));
        // Search: a substring of the number or of the reference, case-insensitive.
        Assert.Equal(Ids(l.Closed, l.Full, l.Partial, l.None), await IdsAsync(http, $"?search={o.Prefix.ToLowerInvariant()}0000"));
        Assert.Equal(Ids(l.Partial), await IdsAsync(http, "?search=000002"));
        Assert.Equal(Ids(l.Draft), await IdsAsync(http, "?search=offer-abc"));
        Assert.Equal(Ids(l.Draft), await IdsAsync(http, "?search=ABC%2F7"));
        Assert.Empty(await IdsAsync(http, "?search=offer-abc&status=confirmed"));
        // Not searched: partner and warehouse names.
        Assert.Empty(await IdsAsync(http, "?search=Second"));
    }

    [Fact]
    public async Task AC65_S010_AC64_A_list_item_is_the_order_without_lines_and_with_a_line_count()
    {
        var l = await ListedAsync();

        var list = await o.ListAsync(l.S.Http);

        Assert.Equal(["items", "limit", "offset", "total"], list.PropertyNames());
        foreach (var item in list.Items())
        {
            var order = await o.GetAsync(l.S.Http, item.Id());
            Assert.False(item.TryGetProperty("lines", out _), $"A list item has lines: {item}");
            Assert.Equal(order.OrderLines().Length, item.GetProperty("lineCount").GetInt32());
            // "The same object without lines and with lineCount."
            Assert.Equal(order.PropertyNames().Where(p => p != "lines").Append("lineCount").Order(StringComparer.Ordinal), item.PropertyNames());
            foreach (var property in order.EnumerateObject().Where(p => p.Name != "lines"))
                McpAssert.JsonEqual(property.Value, item.GetProperty(property.Name), $"List item and order differ in '{property.Name}'");
        }
        var partial = list.Items().Single(i => i.Id() == l.Partial.Id());
        Assert.Equal((3, 20m, "partial"), (partial.GetProperty("lineCount").GetInt32(), partial.Dec("totalAmount"), o.ProgressOf(partial)));
    }

    [Fact]
    public async Task AC65_S010_AC64_Lists_page_and_reject_unknown_filter_values()
    {
        var l = await ListedAsync();
        var http = l.S.Http;

        var page = await o.ListAsync(http, "?limit=2&offset=1");
        using var badStatus = await http.GetAsync($"{o.Path}?status=posted");
        using var badProgress = await http.GetAsync($"{o.Path}?{o.Progress}=all");
        using var badPartner = await http.GetAsync($"{o.Path}?{o.PartnerId}=abc");
        using var badWarehouse = await http.GetAsync($"{o.Path}?warehouseId=abc");
        using var badLimit = await http.GetAsync($"{o.Path}?limit=0");

        Assert.Equal((5, 2, 1), (page.Total(), page.GetProperty("limit").GetInt32(), page.GetProperty("offset").GetInt32()));
        Assert.Equal(new[] { l.Full.Id(), l.Partial.Id() }, page.Items().Select(i => i.Id()));
        await HttpAssert.ValidationAsync(badStatus, "status");
        await HttpAssert.ValidationAsync(badProgress, o.Progress);
        await HttpAssert.ValidationAsync(badPartner, o.PartnerId);
        await HttpAssert.ValidationAsync(badWarehouse, "warehouseId");
        await HttpAssert.ValidationAsync(badLimit, "limit");
    }

    [Fact]
    public async Task AC65_R36_Summaries_show_the_current_code_and_name_of_the_masters()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));

        await Orders.ChangePartnerAsync(s.Http, o.PartnerOf(s).Id(), b => { b["code"] = "RENAMED"; b["name"] = "New name Ltd"; });
        await MasterApi.Warehouses.ReplaceAsync(s.Http, s.W1,
            Stock.WarehouseBody(await MasterApi.Warehouses.GetAsync(s.Http, s.W1)).With("code", "WH-NEW").With("name", "New warehouse"));

        var item = Assert.Single((await o.ListAsync(s.Http)).Items());
        var order = await o.GetAsync(s.Http, confirmed.Id());
        foreach (var read in new[] { item, order })
        {
            Assert.Equal(("RENAMED", "New name Ltd"), (read.GetProperty(o.Partner).Str("code"), read.GetProperty(o.Partner).Str("name")));
            Assert.Equal(("WH-NEW", "New warehouse"), (read.GetProperty("warehouse").Str("code"), read.GetProperty("warehouse").Str("name")));
        }
    }
}
