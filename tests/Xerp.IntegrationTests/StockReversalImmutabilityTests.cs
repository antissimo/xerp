using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 006, AC-02 (builder) / S2: through the DbContext a posted document accepts no modification other than
/// <c>posted -> reversed</c> with <c>reversedBy</c>, a reversed and a reversing document accept none, and a
/// ledger entry accepts none - whatever code asks for it, and nothing is changed.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockReversalImmutabilityTests(XerpFixture app)
{
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly DateTime Now = new(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc);

    private sealed record FixedTenant(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private XerpDbContext NewDbContext(TestTenant tenant) =>
        new(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(app.ConnectionString).Options,
            new FixedTenant(tenant.Id, tenant.ApiKeyId));

    private static async Task AssertStockIsAsync(StockSetup setup, decimal expected)
    {
        Assert.Equal(expected, await Stock.QuantityAsync(setup.Http, setup.A, setup.W1));
        Assert.Equal(expected, await Stock.LedgerSumAsync(setup.Http, setup.A, setup.W1));
    }

    [Fact]
    public async Task AC02_The_reversal_of_a_posted_document_is_accepted_and_changes_only_status_and_link()
    {
        var setup = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(setup.Http, setup.W1, setup.A, 100m);
        Guid reversalId;

        await using (var db = NewDbContext(setup.Tenant))
        {
            var original = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == posted.Id());
            var entries = await db.StockLedgerEntries.Where(e => e.DocumentId == original.Id).ToListAsync();
            var (reversal, reversing) = original.Reverse(entries, Day, "by the context", "SR-000002", Now, setup.Tenant.ApiKeyId);
            db.StockDocuments.Add(reversal);
            db.StockLedgerEntries.AddRange(reversing);
            await db.SaveChangesAsync();
            reversalId = reversal.Id;
        }

        var after = await Stock.GetAsync(setup.Http, posted.Id());
        Assert.Equal("reversed", after.Str("status"));
        Assert.Equal(reversalId, after.GetProperty("reversedBy").GetProperty("id").GetGuid());
        foreach (var property in new[] { "number", "documentDate", "postedAt", "postedBy", "updatedAt", "updatedBy", "note", "lines" })
            McpAssert.JsonEqual(posted.GetProperty(property), after.GetProperty(property), $"The reversal changed '{property}' of the original");
        await AssertStockIsAsync(setup, 0m);
        await Stock.AssertStockEqualsLedgerAsync(setup.Http);
    }

    [Fact]
    public async Task AC02_A_posted_document_accepts_no_other_modification_not_even_dressed_as_a_reversal()
    {
        var setup = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(setup.Http, setup.W1, setup.A, 100m);
        var other = await Stock.ReceiveAsync(setup.Http, setup.W2, setup.B, 1m);

        // The status alone, without a reversing document.
        await using (var db = NewDbContext(setup.Tenant))
        {
            var document = await db.StockDocuments.SingleAsync(d => d.Id == posted.Id());
            db.Entry(document).Property(d => d.Status).CurrentValue = StockDocumentStatus.Reversed;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        // The link alone, and status and link naming a document that is not its reversal.
        await using (var db = NewDbContext(setup.Tenant))
        {
            var document = await db.StockDocuments.SingleAsync(d => d.Id == posted.Id());
            db.Entry(document).Property(d => d.ReversedById).CurrentValue = other.Id();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.Entry(document).Property(d => d.Status).CurrentValue = StockDocumentStatus.Reversed;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        // A real reversal that changes something else on the way.
        await using (var db = NewDbContext(setup.Tenant))
        {
            var original = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == posted.Id());
            var entries = await db.StockLedgerEntries.Where(e => e.DocumentId == original.Id).ToListAsync();
            var (reversal, reversing) = original.Reverse(entries, Day, null, "SR-000009", Now, setup.Tenant.ApiKeyId);
            db.StockDocuments.Add(reversal);
            db.StockLedgerEntries.AddRange(reversing);
            db.Entry(original).Property(d => d.Note).CurrentValue = "rewritten";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        // Making a posted document a reversing one.
        await using (var db = NewDbContext(setup.Tenant))
        {
            var document = await db.StockDocuments.SingleAsync(d => d.Id == posted.Id());
            db.Entry(document).Property(d => d.ReversalOfId).CurrentValue = other.Id();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        // A destination added to a posted receipt.
        await using (var db = NewDbContext(setup.Tenant))
        {
            var document = await db.StockDocuments.SingleAsync(d => d.Id == posted.Id());
            db.Entry(document).Property(d => d.ToWarehouseId).CurrentValue = setup.W2;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await Stock.AssertUnchangedAsync(setup.Http, posted);
        await Stock.AssertUnchangedAsync(setup.Http, other);
        Assert.Equal(2, (await Stock.DocumentsAsync(setup.Http)).Total());
        await AssertStockIsAsync(setup, 100m);
    }

    [Fact]
    public async Task AC02_Reversed_and_reversing_documents_their_lines_and_entries_accept_no_modification()
    {
        var setup = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(setup.Http, setup.W1, setup.A, 100m);
        var reversing = await Stock.ReverseAsync(setup.Http, posted.Id());
        var reversed = await Stock.GetAsync(setup.Http, posted.Id());

        foreach (var id in new[] { reversed.Id(), reversing.Id() })
        {
            await using (var db = NewDbContext(setup.Tenant))
            {
                var document = await db.StockDocuments.SingleAsync(d => d.Id == id);
                db.Entry(document).Property(d => d.Note).CurrentValue = "rewritten";
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
                Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            }
            await using (var db = NewDbContext(setup.Tenant))
            {
                // Un-reversing, or reversing once more, is not a way around it either.
                var document = await db.StockDocuments.SingleAsync(d => d.Id == id);
                db.Entry(document).Property(d => d.Status).CurrentValue =
                    document.Status == StockDocumentStatus.Reversed ? StockDocumentStatus.Posted : StockDocumentStatus.Reversed;
                db.Entry(document).Property(d => d.ReversedById).CurrentValue = document.ReversedById is null ? reversed.Id() : null;
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
            await using (var db = NewDbContext(setup.Tenant))
            {
                db.StockDocuments.Remove(await db.StockDocuments.SingleAsync(d => d.Id == id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
            await using (var db = NewDbContext(setup.Tenant))
            {
                // Only the line is tracked: the context finds out by itself that its document is not a draft.
                var line = await db.StockDocumentLines.SingleAsync(l => l.DocumentId == id);
                db.Entry(line).Property(l => l.Quantity).CurrentValue = 1m;
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
                Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            }
            await using (var db = NewDbContext(setup.Tenant))
            {
                db.StockDocumentLines.Remove(await db.StockDocumentLines.SingleAsync(l => l.DocumentId == id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
            await using (var db = NewDbContext(setup.Tenant))
            {
                var entry = await db.StockLedgerEntries.SingleAsync(e => e.DocumentId == id);
                db.Entry(entry).Property(e => e.Quantity).CurrentValue = 0m;
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
            await using (var db = NewDbContext(setup.Tenant))
            {
                db.StockLedgerEntries.Remove(await db.StockLedgerEntries.SingleAsync(e => e.DocumentId == id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
        }

        await Stock.AssertUnchangedAsync(setup.Http, reversed);
        await Stock.AssertUnchangedAsync(setup.Http, reversing);
        Assert.Equal(2, (await Stock.LedgerAsync(setup.Http)).Total());
        await AssertStockIsAsync(setup, 0m);
    }

    [Fact]
    public async Task AC02_The_database_allows_one_reversing_document_per_original()
    {
        var setup = await Stock.SetupAsync(app);
        var posted = await Stock.ReceiveAsync(setup.Http, setup.W1, setup.A, 100m);
        await Stock.ReceiveAsync(setup.Http, setup.W1, setup.A, 100m); // so that stock could not be what stops the second one

        // Two units of work that both saw the document as posted, without the lock that Application takes.
        await using var first = NewDbContext(setup.Tenant);
        await using var second = NewDbContext(setup.Tenant);
        foreach (var (db, number) in new[] { (first, "SR-000003"), (second, "SR-000004") })
        {
            var original = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == posted.Id());
            var entries = await db.StockLedgerEntries.Where(e => e.DocumentId == original.Id).ToListAsync();
            var (reversal, reversing) = original.Reverse(entries, Day, null, number, Now, setup.Tenant.ApiKeyId);
            db.StockDocuments.Add(reversal);
            db.StockLedgerEntries.AddRange(reversing);
        }

        await first.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => second.SaveChangesAsync());

        Assert.Equal(3, (await Stock.DocumentsAsync(setup.Http)).Total());
        Assert.Equal(3, (await Stock.LedgerAsync(setup.Http)).Total());
        await AssertStockIsAsync(setup, 100m);
    }
}
