using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Orders;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-02 (builder) / S3: through the DbContext a confirmed or closed purchase order and its lines
/// accept no modification other than <c>confirmed &lt;-&gt; closed</c> with <c>closedAt</c> / <c>closedBy</c> -
/// whatever code asks for it, and nothing is changed. The received quantity of a line is progress, not part of
/// what was ordered, and is the one column of a line that may move.
/// </summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderImmutabilityTests(XerpFixture app)
{
    private static readonly OrderApi o = OrderApi.Purchase;
    private static readonly DateTime Now = new(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc);

    private sealed record FixedTenant(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private XerpDbContext NewDbContext(TestTenant tenant) =>
        new(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(app.ConnectionString).Options,
            new FixedTenant(tenant.Id, tenant.ApiKeyId));

    private async Task AssertRefusedAsync(OrderSetup s, Guid orderId, Action<XerpDbContext, PurchaseOrder> change)
    {
        var before = await o.GetAsync(s.Http, orderId);
        await using (var db = NewDbContext(s.Tenant))
        {
            var order = await db.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == orderId);
            change(db, order);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        await o.AssertUnchangedAsync(s.Http, before);
    }

    [Fact]
    public async Task AC02_Close_and_reopen_are_accepted_and_change_only_status_and_closing_attribution()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10m, null, 1m));

        await using (var db = NewDbContext(s.Tenant))
        {
            var order = await db.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == confirmed.Id());
            order.Close(Now, s.Tenant.ApiKeyId);
            await db.SaveChangesAsync();
        }
        var closed = await o.GetAsync(s.Http, confirmed.Id());
        Assert.Equal("closed", closed.Str("status"));
        Assert.Equal(s.Tenant.ApiKeyId, closed.GetProperty("closedBy").GetGuid());
        foreach (var property in new[] { "number", "orderDate", "supplier", "warehouse", "confirmedAt", "confirmedBy", "updatedAt", "updatedBy", "totalAmount" })
            McpAssert.JsonEqual(confirmed.GetProperty(property), closed.GetProperty(property), $"Close changed '{property}'");

        await using (var db = NewDbContext(s.Tenant))
        {
            var order = await db.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == confirmed.Id());
            order.Reopen();
            await db.SaveChangesAsync();
        }
        McpAssert.JsonEqual(confirmed, await o.GetAsync(s.Http, confirmed.Id()), "Close and reopen did not restore the order");
    }

    [Fact]
    public async Task AC02_A_confirmed_order_accepts_no_other_modification_not_even_dressed_as_a_close()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10m, null, 1m), (s.B, 5m, null, 2m));
        var id = confirmed.Id();

        await AssertRefusedAsync(s, id, (db, order) => db.Entry(order).Property(x => x.Note).CurrentValue = "changed");
        await AssertRefusedAsync(s, id, (db, order) => db.Entry(order).Property(x => x.Number).CurrentValue = "PO-999999");
        await AssertRefusedAsync(s, id, (db, order) => db.Entry(order).Property(x => x.WarehouseId).CurrentValue = s.W2);
        // Back to a draft, and the status of a close without its attribution.
        await AssertRefusedAsync(s, id, (db, order) => db.Entry(order).Property(x => x.Status).CurrentValue = OrderStatus.Draft);
        await AssertRefusedAsync(s, id, (db, order) => db.Entry(order).Property(x => x.Status).CurrentValue = OrderStatus.Closed);
        await AssertRefusedAsync(s, id, (db, order) => db.Entry(order).Property(x => x.ClosedAt).CurrentValue = Now);
        // A real close that also changes something else.
        await AssertRefusedAsync(s, id, (db, order) =>
        {
            order.Close(Now, s.Tenant.ApiKeyId);
            db.Entry(order).Property(x => x.Reference).CurrentValue = "slipped in";
        });
        await AssertRefusedAsync(s, id, (db, order) => db.PurchaseOrders.Remove(order));
    }

    [Fact]
    public async Task AC02_The_lines_of_a_confirmed_or_closed_order_keep_what_was_ordered()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10m, null, 1m), (s.B, 5m, null, 2m));
        var closed = await o.CloseAsync(s.Http, (await o.OrderedAsync(s, (s.A, 3m, null, 1m))).Id());

        foreach (var id in new[] { confirmed.Id(), closed.Id() })
        {
            await AssertRefusedAsync(s, id, (db, order) => db.Entry(order.Lines[0]).Property(l => l.Quantity).CurrentValue = 99m);
            await AssertRefusedAsync(s, id, (db, order) => db.Entry(order.Lines[0]).Property(l => l.UnitPrice).CurrentValue = 99m);
            await AssertRefusedAsync(s, id, (db, order) => db.Entry(order.Lines[0]).Property(l => l.BaseQuantity).CurrentValue = 99m);
            await AssertRefusedAsync(s, id, (db, order) => db.Entry(order.Lines[0]).Property(l => l.Factor).CurrentValue = 2m);
            await AssertRefusedAsync(s, id, (db, order) => db.Entry(order.Lines[0]).Property(l => l.ArticleId).CurrentValue = s.B);
            await AssertRefusedAsync(s, id, (db, order) => db.PurchaseOrderLines.Remove(order.Lines[0]));
        }

        // The same when the order itself is not tracked: the line is looked up against its order.
        var before = await o.GetAsync(s.Http, confirmed.Id());
        await using (var db = NewDbContext(s.Tenant))
        {
            var line = await db.PurchaseOrderLines.FirstAsync(l => l.OrderId == confirmed.Id());
            db.Entry(line).Property(l => l.Quantity).CurrentValue = 99m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        await o.AssertUnchangedAsync(s.Http, before);
    }

    [Fact]
    public async Task AC02_A_draft_is_freely_changed_and_the_received_quantity_is_progress()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10m, null, 1m));
        await using (var db = NewDbContext(s.Tenant))
        {
            var order = await db.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.Id == draft.Id());
            db.Entry(order).Property(x => x.Note).CurrentValue = "a draft may change";
            db.Entry(order.Lines[0]).Property(l => l.Quantity).CurrentValue = 7m;
            await db.SaveChangesAsync();
        }
        var changed = await o.GetAsync(s.Http, draft.Id());
        Assert.Equal("a draft may change", changed.Str("note"));
        Assert.Equal(7m, changed.OrderLines()[0].Dec("quantity"));

        // Posting a receipt moves the received quantity of a confirmed line, and nothing else of the order.
        var confirmed = await o.ConfirmAsync(s.Http, draft.Id());
        await o.FulfilAsync(s.Http, confirmed, 1, 4m);
        var received = await o.GetAsync(s.Http, draft.Id());
        Assert.Equal(4m, received.OrderLines()[0].Dec("receivedBaseQuantity"));
        Assert.Equal(3m, received.OrderLines()[0].Outstanding());
        foreach (var property in new[] { "status", "number", "updatedAt", "updatedBy", "confirmedAt", "totalAmount" })
            McpAssert.JsonEqual(confirmed.GetProperty(property), received.GetProperty(property), $"A receipt changed '{property}' of the order");
    }
}
