using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-90 to AC-93: another tenant's orders do not exist — not listed, not counted, <c>404</c> for
/// every operation, <c>REFERENCE_NOT_FOUND</c> as a reference — and progress, numbers and "used" are per
/// tenant. Over HTTP and through the tools. Written once for every kind of order (ADR-0016).
/// </summary>
public abstract class OrderIsolationTests(XerpFixture app, OrderApi o)
{
    protected sealed record Side(OrderSetup S, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    protected async Task<Side> SideAsync()
    {
        var setup = await Orders.SetupAsync(app);
        return new Side(setup, await app.McpAsync(setup.Tenant.Key));
    }

    // ---- AC-90 ----

    [Fact]
    public async Task AC90_Another_tenants_orders_and_fulfilment_are_invisible()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await o.PrepareStockAsync(x.S);
        var order = await o.OrderedAsync(x.S, (x.S.A, 10, null, 2.5m));
        await o.FulfilAsync(x.Http, order, 1, 4);
        await o.DraftAsync(x.S, (x.S.B, 1, null, 1m));

        foreach (var query in new[]
                 {
                     "", "?status=confirmed", $"?{o.Progress}=partial", $"?{o.PartnerId}={o.PartnerOf(x.S).Id()}",
                     $"?warehouseId={x.S.W1}", $"?search={o.Prefix}",
                 })
        {
            var list = await o.ListAsync(y.Http, query);
            Assert.True(list.Total() == 0 && list.Items().Length == 0, $"Y sees X's orders with '{query}': {list}");
        }
        Assert.Equal(0, (await Stock.DocumentsAsync(y.Http, $"?{o.LinkId}={order.Id()}")).Total());
        Assert.Equal(0, (await Stock.DocumentsAsync(y.Http)).Total());
        Assert.Equal(0, (await Stock.OnHandAsync(y.Http)).Total());
        Assert.Equal(0, (await Stock.LedgerAsync(y.Http)).Total());

        // The same through the tools.
        Assert.Equal(0, (await y.Mcp.OkAsync(o.Tool("list"))).Total());
        Assert.Equal(0, (await y.Mcp.OkAsync(o.Tool("list"), new Dictionary<string, object>
        {
            [o.PartnerId] = o.PartnerOf(x.S).Id(), ["warehouseId"] = x.S.W1,
        })).Total());
        Assert.Equal(0, (await y.Mcp.OkAsync("stock_document_list", new Dictionary<string, object> { [o.LinkId] = order.Id() })).Total());
        Assert.Equal(0, (await y.Mcp.OkAsync("stock_on_hand_list")).Total());
        // X still sees its own.
        Assert.Equal(2, (await o.ListAsync(x.Http)).Total());
        Assert.Equal(2, (await x.Mcp.OkAsync(o.Tool("list"))).Total());
    }

    // ---- AC-91 ----

    [Theory]
    [InlineData("draft")]
    [InlineData("confirmed")]
    [InlineData("closed")]
    public async Task AC91_Another_tenants_order_is_not_found_for_every_operation(string status)
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var order = await o.DraftAsync(x.S, (x.S.A, 10, null, 2.5m));
        if (status != "draft")
            order = await o.ConfirmAsync(x.Http, order.Id());
        if (status == "closed")
            order = await o.CloseAsync(x.Http, order.Id());
        var id = order.Id();
        var body = o.Replacement(y.S, (y.S.A, 1, null, 1m));

        using var get = await o.SendGetAsync(y.Http, id);
        using var put = await o.PutAsync(y.Http, id, body);
        using var delete = await o.DeleteAsync(y.Http, id);
        using var confirm = await o.SendAsync(y.Http, id, "confirm");
        using var close = await o.SendAsync(y.Http, id, "close");
        using var reopen = await o.SendAsync(y.Http, id, "reopen");
        using var byNumber = await o.ByNumberAsync(y.Http, o.Number(1));
        foreach (var response in new[] { get, put, delete, confirm, close, reopen, byNumber })
            await HttpAssert.NotFoundAsync(response);

        await y.Mcp.ErrorAsync(o.Tool("get"), new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync(o.Tool("get"), new { number = o.Number(1) }, "NOT_FOUND");
        await y.Mcp.ErrorAsync(o.Tool("update"), body.WithId(id), "NOT_FOUND");
        await y.Mcp.ErrorAsync(o.Tool("delete"), new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync(o.Tool("confirm"), new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync(o.Tool("close"), new { id }, "NOT_FOUND");
        await y.Mcp.ErrorAsync(o.Tool("reopen"), new { id }, "NOT_FOUND");

        await o.AssertUnchangedAsync(x.Http, order);
        Assert.Equal(1, (await o.ListAsync(x.Http)).Total());
    }

    // ---- AC-92 ----

    [Fact]
    public async Task AC92_Another_tenants_masters_cannot_be_named_on_an_order()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        var partner = o.PartnerOf(y.S).Id();

        using var ofPartner = await o.PostAsync(y.Http, o.Body(o.PartnerOf(x.S).Id(), y.S.W1, (y.S.A, 1, null, 1m)));
        using var ofWarehouse = await o.PostAsync(y.Http, o.Body(partner, x.S.W1, (y.S.A, 1, null, 1m)));
        using var ofArticle = await o.PostAsync(y.Http, o.Body(partner, y.S.W1, (x.S.A, 1, null, 1m)));
        using var ofUnit = await o.PostAsync(y.Http, o.Body(partner, y.S.W1, (y.S.A, 1, x.S.Box, 1m)));

        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(ofPartner, o.PartnerId)));
        await HttpAssert.ReferenceNotFoundAsync(ofWarehouse, "warehouseId");
        await HttpAssert.ReferenceNotFoundAsync(ofArticle, "lines[0].articleId");
        await HttpAssert.ReferenceNotFoundAsync(ofUnit, "lines[0].unitId");
        await y.Mcp.ErrorAsync(o.Tool("create"), o.Body(o.PartnerOf(x.S).Id(), y.S.W1, (y.S.A, 1, null, 1m)), "REFERENCE_NOT_FOUND", o.PartnerId);
        Assert.Equal(0, (await o.ListAsync(y.Http)).Total());

        // The same on replace of Y's own draft.
        var draft = await o.DraftAsync(y.S, (y.S.A, 1, null, 1m));
        using var replace = await o.PutAsync(y.Http, draft.Id(), o.Replacement(o.PartnerOf(x.S).Id(), y.S.W1, (y.S.A, 1, null, 1m)));
        await HttpAssert.ReferenceNotFoundAsync(replace, o.PartnerId);
        await o.AssertUnchangedAsync(y.Http, draft);
    }

    [Fact]
    public async Task AC92_Another_tenants_order_cannot_be_fulfilled_or_filtered_by()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await o.PrepareStockAsync(x.S);
        await o.PrepareStockAsync(y.S);
        var order = await o.OrderedAsync(x.S, (x.S.A, 10, null, 2.5m));
        await o.FulfilAsync(x.Http, order, 1, 4);
        var before = await o.GetAsync(x.Http, order.Id());
        var documentsOfY = (await Stock.DocumentsAsync(y.Http)).Total();

        using var linked = await Stock.PostAsync(y.Http, o.Document(y.S.W1, order.Id(), (y.S.A, 1, 1, null)));
        var problem = await HttpAssert.ReferenceNotFoundAsync(linked, o.LinkId);
        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(problem));
        await y.Mcp.ErrorAsync("stock_document_create", o.Document(y.S.W1, order.Id(), (y.S.A, 1, 1, null)), "REFERENCE_NOT_FOUND", o.LinkId);

        Assert.Equal(documentsOfY, (await Stock.DocumentsAsync(y.Http)).Total());
        await o.AssertUnchangedAsync(x.Http, before);
        Assert.Equal((4m, 6m), (await o.LinesAsync(x.Http, order.Id()))[0]);
        // X's fulfilment documents are not Y's to post, change or reverse.
        var documentOfX = (await o.DocumentsAsync(x.Http, order.Id()))[0].Id();
        using var post = await Stock.SendPostAsync(y.Http, documentOfX);
        using var reverse = await Stock.SendReverseAsync(y.Http, documentOfX);
        await HttpAssert.NotFoundAsync(post);
        await HttpAssert.NotFoundAsync(reverse);
        Assert.Equal((4m, 6m), (await o.LinesAsync(x.Http, order.Id()))[0]);
    }

    // ---- AC-93 ----

    [Fact]
    public async Task AC93_Numbers_progress_and_quantities_on_order_are_per_tenant()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        await o.PrepareStockAsync(x.S);
        var ofX = await o.OrderedAsync(x.S, (x.S.A, 10, null, 2.5m));
        await o.FulfilAsync(x.Http, ofX, 1, 4);

        // Y's article has the same code "A"; X's order gives it nothing.
        Assert.Equal(x.S.U.S.ArticleA.Str("code"), y.S.U.S.ArticleA.Str("code"));
        Assert.Equal(0m, await o.OnHandAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(6m, await o.OnHandAsync(x.Http, x.S.A, x.S.W1));

        var ofY = await o.OrderedAsync(y.S, (y.S.A, 3, null, 1m));

        Assert.Equal(o.Number(1), ofX.Number());
        Assert.Equal(o.Number(1), ofY.Number());
        using var byNumber = await o.ByNumberAsync(y.Http, o.Number(1));
        Assert.Equal(ofY.Id(), (await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK)).Id());
        Assert.Equal(3m, await o.OnHandAsync(y.Http, y.S.A, y.S.W1));
        Assert.Equal(6m, await o.OnHandAsync(x.Http, x.S.A, x.S.W1));
        await o.AssertProgressAsync(y.Http, ofY.Id(), "confirmed", "none", (0m, 3m));
        await o.AssertProgressAsync(x.Http, ofX.Id(), "confirmed", "partial", (4m, 6m));
    }

    [Fact]
    public async Task AC93_T4_One_tenants_orders_never_make_another_tenants_masters_used()
    {
        await using var x = await SideAsync();
        await using var y = await SideAsync();
        // Y uses its partner, W2, B and box on orders in every status.
        await o.CreateAsync(y.Http, o.Body(o.PartnerOf(y.S).Id(), y.S.W2, (y.S.B, 1, null, 1m), (y.S.A, 1, y.S.Box, 1m)));
        var confirmed = await o.OrderedAsync(y.S, y.S.W2, (y.S.B, 1, null, 1m));
        await o.CloseAsync(y.Http, (await o.OrderedAsync(y.S, y.S.W2, (y.S.B, 1, null, 1m))).Id());

        // X deletes its own unused masters with the same codes.
        using var partner = await MasterApi.Partners.DeleteAsync(x.Http, o.PartnerOf(x.S).Id());
        using var warehouse = await MasterApi.Warehouses.DeleteAsync(x.Http, x.S.W2);
        using var article = await x.Http.DeleteAsync($"{Art.Path}/{x.S.B}");
        using var conversion = await Units.DeleteAsync(x.Http, x.S.A, x.S.Box);
        using var unit = await Units.DeleteUnitAsync(x.Http, x.S.Box);

        foreach (var response in new[] { partner, warehouse, article, conversion, unit })
            Assert.True(response.StatusCode == HttpStatusCode.NoContent,
                $"Expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        // And Y's are used.
        using var ofY = await MasterApi.Partners.DeleteAsync(y.Http, o.PartnerOf(y.S).Id());
        await HttpAssert.InUseAsync(ofY);
        await o.AssertUnchangedAsync(y.Http, confirmed);
    }
}
