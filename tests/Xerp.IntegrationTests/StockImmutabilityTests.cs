using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-03 (builder) / S3: the DbContext itself refuses to save a changed or deleted ledger entry and
/// any change to a posted document or its lines, whatever code asks for it - and nothing is changed.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockImmutabilityTests(XerpFixture app)
{
    private sealed record FixedTenant(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private XerpDbContext NewDbContext(TestTenant tenant) =>
        new(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(app.ConnectionString).Options,
            new FixedTenant(tenant.Id, tenant.ApiKeyId));

    private async Task<(StockSetup Setup, Guid PostedId)> PostedReceiptAsync()
    {
        var setup = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(setup.Http, setup.W1, setup.A, 100m);
        return (setup, posted.Id());
    }

    private static async Task AssertStockIsAsync(StockSetup setup, decimal expected)
    {
        Assert.Equal(expected, await Stock.QuantityAsync(setup.Http, setup.A, setup.W1));
        Assert.Equal(expected, await Stock.LedgerSumAsync(setup.Http, setup.A, setup.W1));
    }

    [Fact]
    public async Task AC03_A_modified_ledger_entry_is_refused_and_nothing_changes()
    {
        var (setup, _) = await PostedReceiptAsync();
        await using var db = NewDbContext(setup.Tenant);
        var entry = await db.StockLedgerEntries.SingleAsync();

        db.Entry(entry).Property(e => e.Quantity).CurrentValue = 1m;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await AssertStockIsAsync(setup, 100m);
    }

    [Fact]
    public async Task AC03_A_deleted_ledger_entry_is_refused_and_nothing_changes()
    {
        var (setup, _) = await PostedReceiptAsync();
        await using var db = NewDbContext(setup.Tenant);
        db.StockLedgerEntries.Remove(await db.StockLedgerEntries.SingleAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await AssertStockIsAsync(setup, 100m);
    }

    [Fact]
    public async Task AC03_A_modified_or_deleted_header_of_a_posted_document_is_refused()
    {
        var (setup, postedId) = await PostedReceiptAsync();
        var before = await Stock.GetAsync(setup.Http, postedId);

        await using (var db = NewDbContext(setup.Tenant))
        {
            var document = await db.StockDocuments.SingleAsync(d => d.Id == postedId);
            db.Entry(document).Property(d => d.Note).CurrentValue = "rewritten";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

            db.Entry(document).Property(d => d.Status).CurrentValue = StockDocumentStatus.Draft; // not a way around it
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        await using (var db = NewDbContext(setup.Tenant))
        {
            db.StockDocuments.Remove(await db.StockDocuments.SingleAsync(d => d.Id == postedId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await Stock.AssertUnchangedAsync(setup.Http, before);
        await AssertStockIsAsync(setup, 100m);
    }

    [Fact]
    public async Task AC03_A_modified_or_deleted_line_of_a_posted_document_is_refused_also_when_the_document_is_not_loaded()
    {
        var (setup, postedId) = await PostedReceiptAsync();
        var before = await Stock.GetAsync(setup.Http, postedId);

        await using (var db = NewDbContext(setup.Tenant))
        {
            // Only the line is tracked: the context has to find out by itself that its document is posted.
            var line = await db.StockDocumentLines.SingleAsync(l => l.DocumentId == postedId);
            db.Entry(line).Property(l => l.Quantity).CurrentValue = 1m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        await using (var db = NewDbContext(setup.Tenant))
        {
            db.StockDocumentLines.Remove(await db.StockDocumentLines.SingleAsync(l => l.DocumentId == postedId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = NewDbContext(setup.Tenant))
        {
            // With the document loaded as well.
            var document = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == postedId);
            db.Entry(document.Lines[0]).Property(l => l.Quantity).CurrentValue = 1m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await Stock.AssertUnchangedAsync(setup.Http, before);
        await AssertStockIsAsync(setup, 100m);
    }

    [Fact]
    public async Task AC03_A_draft_and_its_lines_stay_editable_through_the_context()
    {
        var setup = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(setup.Http, "receipt", setup.W1, (setup.A, 5m));

        await using (var db = NewDbContext(setup.Tenant))
        {
            var line = await db.StockDocumentLines.SingleAsync(l => l.DocumentId == draft.Id());
            db.Entry(line).Property(l => l.Quantity).CurrentValue = 6m;
            Assert.Equal(1, await db.SaveChangesAsync());
        }

        Assert.Equal(6m, (await Stock.GetAsync(setup.Http, draft.Id())).DocumentLines()[0].Quantity());
        await AssertStockIsAsync(setup, 0m);
    }
}
