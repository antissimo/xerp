using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xerp.Application.Ports;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, the builder's own criteria - what cannot be seen or provoked through the public surface:
/// AC-02 (the database as the second barrier), AC-03 (a corrupted balance row is read, found by verify and
/// repaired by rebuild) and AC-04 (the migration of existing tenants and stock). The rows are corrupted
/// directly in the database, because no operation can do it.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockBalanceBuilderTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    private sealed record NoTenant(Guid? TenantId = null, Guid? ApiKeyId = null) : ITenantContext;

    private Task<decimal?> StoredAsync(Guid tenant, Guid warehouse, Guid article) =>
        app.ScalarAsync<decimal?>(
            """SELECT "Quantity" FROM "StockBalances" WHERE "TenantId" = @t AND "WarehouseId" = @w AND "ArticleId" = @a""",
            ("t", tenant), ("w", warehouse), ("a", article));

    private static (decimal Stored, decimal Ledger, decimal Difference) Numbers(System.Text.Json.JsonElement difference) =>
        (difference.Dec("storedQuantity"), difference.Dec("ledgerQuantity"), difference.Dec("differenceQuantity"));

    // ---- AC-02 ----

    [Fact]
    public async Task AC02_The_database_refuses_a_second_default_warehouse_an_inactive_default_and_a_negative_balance()
    {
        var s = await Stock.SetupAsync(app);
        var dw = await Balance.DefaultIdAsync(s.Http);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        var second = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync("""UPDATE "Warehouses" SET "IsDefault" = TRUE WHERE "Id" = @id""", ("id", s.W1)));
        var inactive = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync("""UPDATE "Warehouses" SET "IsActive" = FALSE WHERE "Id" = @id""", ("id", dw)));
        var negative = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync(
                """UPDATE "StockBalances" SET "Quantity" = -0.000001 WHERE "TenantId" = @t AND "WarehouseId" = @w AND "ArticleId" = @a""",
                ("t", s.Tenant.Id), ("w", s.W1), ("a", s.A)));
        var negativeInsert = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync(
                """INSERT INTO "StockBalances" ("TenantId", "WarehouseId", "ArticleId", "Quantity") VALUES (@t, @w, @a, -1)""",
                ("t", s.Tenant.Id), ("w", s.W2), ("a", s.A)));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, DbNames.DefaultWarehouseIndex), (second.SqlState, second.ConstraintName));
        Assert.Equal((PostgresErrorCodes.CheckViolation, DbNames.DefaultWarehouseIsActiveCheck), (inactive.SqlState, inactive.ConstraintName));
        Assert.Equal((PostgresErrorCodes.CheckViolation, DbNames.StockBalanceNotNegativeCheck), (negative.SqlState, negative.ConstraintName));
        Assert.Equal((PostgresErrorCodes.CheckViolation, DbNames.StockBalanceNotNegativeCheck), (negativeInsert.SqlState, negativeInsert.ConstraintName));
        // Nothing of it happened.
        Assert.Equal(dw, await Balance.DefaultIdAsync(s.Http));
        await Balance.AssertBalancedAsync(s, "after the refused statements", dw);
    }

    [Fact]
    public async Task AC02_A_balance_cannot_name_a_warehouse_or_an_article_of_another_tenant()
    {
        var x = await Stock.SetupAsync(app);
        var y = await Stock.SetupAsync(app);

        var foreignWarehouse = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync(
                """INSERT INTO "StockBalances" ("TenantId", "WarehouseId", "ArticleId", "Quantity") VALUES (@t, @w, @a, 1)""",
                ("t", x.Tenant.Id), ("w", y.W1), ("a", x.A)));
        var foreignArticle = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync(
                """INSERT INTO "StockBalances" ("TenantId", "WarehouseId", "ArticleId", "Quantity") VALUES (@t, @w, @a, 1)""",
                ("t", x.Tenant.Id), ("w", x.W1), ("a", y.A)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignWarehouse.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignArticle.SqlState);
    }

    [Fact]
    public async Task R1_A_new_tenant_has_its_default_warehouse_in_the_database_and_no_balance_rows()
    {
        var tenant = await app.NewTenantAsync();

        var warehouses = await app.ScalarAsync<long>("""SELECT count(*) FROM "Warehouses" WHERE "TenantId" = @t""", ("t", tenant.Id));
        var defaults = await app.ScalarAsync<long>(
            """SELECT count(*) FROM "Warehouses" WHERE "TenantId" = @t AND "IsDefault" AND "IsActive" AND "Code" = 'CENTRAL' AND "CreatedBy" = @k AND "UpdatedBy" = @k""",
            ("t", tenant.Id), ("k", tenant.ApiKeyId));
        var balances = await app.ScalarAsync<long>("""SELECT count(*) FROM "StockBalances" WHERE "TenantId" = @t""", ("t", tenant.Id));

        Assert.Equal((1L, 1L, 0L), (warehouses, defaults, balances));
    }

    // ---- AC-03 ----

    [Fact]
    public async Task AC03_A_corrupted_and_a_deleted_balance_row_are_read_found_by_verify_and_repaired_by_rebuild()
    {
        var x = await Stock.SetupAsync(app);
        var y = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(x.Http, x.W1, x.A, 10);
        await Stock.ReceiveAsync(x.Http, x.W1, x.B, 7);
        await Stock.ReceiveAsync(x.Http, x.W2, x.A, 3);
        await Stock.ReceiveAsync(y.Http, y.W1, y.A, 5);
        var ledgerBefore = await Balance.LedgerAsync(x.Http);
        var documentsBefore = await Stock.DocumentsAsync(x.Http);

        // Stored 12 where the ledger sums to 10; and the row of (B, W1), whose ledger sums to 7, is gone.
        Assert.Equal(1, await app.ExecuteSqlAsync(
            """UPDATE "StockBalances" SET "Quantity" = 12 WHERE "TenantId" = @t AND "WarehouseId" = @w AND "ArticleId" = @a""",
            ("t", x.Tenant.Id), ("w", x.W1), ("a", x.A)));
        Assert.Equal(1, await app.ExecuteSqlAsync(
            """DELETE FROM "StockBalances" WHERE "TenantId" = @t AND "WarehouseId" = @w AND "ArticleId" = @a""",
            ("t", x.Tenant.Id), ("w", x.W1), ("a", x.B)));

        // R23: every reader shows the stored balance, not the ledger sum.
        Assert.Equal(12m, await Stock.QuantityAsync(x.Http, x.A, x.W1));
        Assert.Equal(12m, await Balance.ListedQuantityAsync(x.Http, x.A, x.W1));
        Assert.Equal(0m, await Balance.ListedQuantityAsync(x.Http, x.B, x.W1));
        Assert.Equal(0, (await Stock.OnHandAsync(x.Http, $"?articleId={x.B}&warehouseId={x.W1}")).Total());
        Assert.Equal(10m, await Stock.LedgerSumAsync(x.Http, x.A, x.W1));
        // ... and so does every decision: the book quantity of a count, the sufficiency check of an issue.
        var count = await Stock.CreateAsync(x.Http, "count", x.W1, (x.A, 12), (x.B, 0));
        Assert.Equal(new[] { 12m, 0m }, count.DocumentLines().Select(l => l.Dec("bookQuantity")).ToArray());
        using (var deleted = await Stock.DeleteAsync(x.Http, count.Id()))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var issue = await Stock.CreateAsync(x.Http, "issue", x.W1, (x.B, 1));
        using (var refused = await Stock.SendPostAsync(x.Http, issue.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        using (var deleted = await Stock.DeleteAsync(x.Http, issue.Id()))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // R24: verify lists exactly the two pairs, by article code, with stored, ledger and stored - ledger.
        var differences = await Balance.DifferenceListAsync(x.Http);
        Assert.Equal(2, differences.Total());
        var (first, second) = (differences.Items()[0], differences.Items()[1]);
        Assert.Equal(new[] { "article", "differenceQuantity", "ledgerQuantity", "storedQuantity", "unit", "warehouse" }, first.PropertyNames());
        Assert.Equal((x.A, x.W1, "pcs"), (first.GetProperty("article").Id(), first.GetProperty("warehouse").Id(), first.GetProperty("unit").Str("code")));
        Assert.Equal((12m, 10m, 2m), Numbers(first));
        Assert.Equal((x.B, x.W1), (second.GetProperty("article").Id(), second.GetProperty("warehouse").Id()));
        Assert.Equal((0m, 7m, -7m), Numbers(second));
        var paged = await Balance.DifferenceListAsync(x.Http, "?limit=1&offset=1");
        Assert.Equal(2, paged.Total());
        Assert.Equal(x.B, Assert.Single(paged.Items()).GetProperty("article").Id());
        // Verify changes nothing, and is the caller's tenant's alone (T5).
        Assert.Equal(12m, await StoredAsync(x.Tenant.Id, x.W1, x.A));
        Assert.Equal(0, await Balance.DifferencesAsync(y.Http));

        // T5: another tenant's rebuild corrects nothing of X.
        Assert.Equal((1, 0), await Balance.RebuildAsync(y.Http));
        Assert.Equal(2, await Balance.DifferencesAsync(x.Http));

        // R25: rebuild corrects the two pairs; the third pair, which was right, is not counted.
        Assert.Equal((3, 2), await Balance.RebuildAsync(x.Http));

        Assert.Equal(0, await Balance.DifferencesAsync(x.Http));
        Assert.Equal(10m, await Stock.QuantityAsync(x.Http, x.A, x.W1));
        Assert.Equal(7m, await Stock.QuantityAsync(x.Http, x.B, x.W1));
        Assert.Equal(3m, await Stock.QuantityAsync(x.Http, x.A, x.W2));
        Assert.Equal((10m, 7m, 3m),
            (await StoredAsync(x.Tenant.Id, x.W1, x.A), await StoredAsync(x.Tenant.Id, x.W1, x.B), await StoredAsync(x.Tenant.Id, x.W2, x.A)));
        await Balance.AssertBalancedAsync(x, "after the rebuild");
        Assert.Equal((3, 0), await Balance.RebuildAsync(x.Http));
        // It wrote balances only: the ledger and the documents are as they were.
        Assert.Equal(ledgerBefore.Select(e => e.GetRawText()), (await Balance.LedgerAsync(x.Http)).Select(e => e.GetRawText()));
        McpAssert.JsonEqual(documentsBefore, await Stock.DocumentsAsync(x.Http), "The rebuild changed a document");
        // Another tenant's rows are untouched throughout.
        Assert.Equal(5m, await StoredAsync(y.Tenant.Id, y.W1, y.A));
        await Balance.AssertBalancedAsync(y, "the other tenant");

        // And posting goes on from the repaired balance.
        await Stock.IssueAsync(x.Http, x.W1, x.B, 7);
        await Balance.AssertBalancedAsync(x, "after an issue that follows the rebuild");
    }

    [Fact]
    public async Task AC03_A_balance_without_ledger_entries_is_a_difference_and_rebuild_removes_it()
    {
        var s = await Stock.SetupAsync(app);
        await app.ExecuteSqlAsync(
            """INSERT INTO "StockBalances" ("TenantId", "WarehouseId", "ArticleId", "Quantity") VALUES (@t, @w, @a, 3)""",
            ("t", s.Tenant.Id), ("w", s.W2), ("a", s.A));

        var difference = Assert.Single((await Balance.DifferenceListAsync(s.Http)).Items());
        Assert.Equal((s.A, s.W2), (difference.GetProperty("article").Id(), difference.GetProperty("warehouse").Id()));
        Assert.Equal((3m, 0m, 3m), Numbers(difference));
        Assert.Equal(3m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        // The stray row names the warehouse: the foreign key answers as "in use", never as a 500 (S4).
        using (var blocked = await W.DeleteAsync(s.Http, s.W2))
            await HttpAssert.InUseAsync(blocked);

        Assert.Equal((0, 1), await Balance.RebuildAsync(s.Http));

        Assert.Equal(0, await Balance.DifferencesAsync(s.Http));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        Assert.Null(await StoredAsync(s.Tenant.Id, s.W2, s.A));
        using var deleted = await W.DeleteAsync(s.Http, s.W2);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task R21_R22_Posting_and_reversal_write_the_balance_row_and_a_refused_posting_does_not()
    {
        var s = await Stock.SetupAsync(app);
        Assert.Null(await StoredAsync(s.Tenant.Id, s.W1, s.A));

        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        Assert.Equal(10m, await StoredAsync(s.Tenant.Id, s.W1, s.A));

        // A draft writes nothing; a refused posting leaves the row as it was.
        var tooMuch = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 11));
        using (var refused = await Stock.SendPostAsync(s.Http, tooMuch.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(10m, await StoredAsync(s.Tenant.Id, s.W1, s.A));

        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 4);
        Assert.Equal((6m, 4m), (await StoredAsync(s.Tenant.Id, s.W1, s.A), await StoredAsync(s.Tenant.Id, s.W2, s.A)));

        using (var refused = await Stock.SendReverseAsync(s.Http, receipt.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal((6m, 4m), (await StoredAsync(s.Tenant.Id, s.W1, s.A), await StoredAsync(s.Tenant.Id, s.W2, s.A)));

        await Stock.IssueAsync(s.Http, s.W2, s.A, 4);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 6);
        // Zero is a balance like any other; the rows of other pairs were never written.
        Assert.Equal((0m, 0m), (await StoredAsync(s.Tenant.Id, s.W1, s.A), await StoredAsync(s.Tenant.Id, s.W2, s.A)));
        Assert.Null(await StoredAsync(s.Tenant.Id, s.W1, s.B));
        Assert.Equal((2, 0), await Balance.RebuildAsync(s.Http));
        await Balance.AssertBalancedAsync(s, "at the end");
    }

    // ---- AC-04 ----

    private sealed record MigratedWarehouse(Guid TenantId, Guid Id, string Code, string Name, bool IsActive, bool IsDefault, Guid CreatedBy, Guid UpdatedBy);

    private sealed record MigratedBalance(Guid TenantId, Guid WarehouseId, Guid ArticleId, decimal Quantity);

    [Fact]
    public async Task AC04_The_migration_gives_every_tenant_a_default_warehouse_and_fills_the_balances_from_the_ledger()
    {
        // A database of its own, migrated to the state before this spec, filled, then migrated to the end.
        var database = "xerp_mig_" + Guid.NewGuid().ToString("N");
        await app.ExecuteSqlAsync($"""CREATE DATABASE "{database}" """);
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using var db = new XerpDbContext(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(connectionString).Options, new NoTenant());
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("SalesOrders");

            // "two": two active warehouses (and an older inactive one) with posted stock.
            // "dead": its only warehouse is inactive and has the code CENTRAL (in another letter case). Two keys.
            // "none": no warehouse at all.
            var (two, dead, none) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (twoKey, deadKeyOld, deadKeyNew, noneKey) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (pcs, a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (retired, older, newer, central) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (document, draft) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Tenants" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES
                    ({two}, 'two', 'Two warehouses', true, now()), ({dead}, 'dead', 'Inactive central', true, now()), ({none}, 'none', 'No warehouse', true, now());
                INSERT INTO "ApiKeys" ("Id", "TenantId", "Name", "ActorType", "KeyHash", "IsActive", "CreatedAt") VALUES
                    ({twoKey}, {two}, 'first', 'human', {XerpFixture.Sha256Hex(database + "1")}, true, now()),
                    ({deadKeyNew}, {dead}, 'second', 'agent', {XerpFixture.Sha256Hex(database + "2")}, true, now()),
                    ({deadKeyOld}, {dead}, 'first', 'human', {XerpFixture.Sha256Hex(database + "3")}, false, now() - interval '1 day'),
                    ({noneKey}, {none}, 'first', 'human', {XerpFixture.Sha256Hex(database + "4")}, true, now());
                INSERT INTO "UnitsOfMeasure" ("Id", "TenantId", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy")
                    VALUES ({pcs}, {two}, 'pcs', 'Piece', true, now(), now(), {twoKey}, {twoKey});
                INSERT INTO "Articles" ("Id", "TenantId", "Code", "Name", "Type", "BaseUnitId", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({a}, {two}, 'A', 'Article A', 'stock', {pcs}, true, now(), now(), {twoKey}, {twoKey}),
                    ({b}, {two}, 'B', 'Article B', 'stock', {pcs}, true, now(), now(), {twoKey}, {twoKey});
                INSERT INTO "Warehouses" ("Id", "TenantId", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({retired}, {two}, 'W0', 'Retired', false, now() - interval '3 days', now(), {twoKey}, {twoKey}),
                    ({newer}, {two}, 'W2', 'Newer', true, now() - interval '1 day', now(), {twoKey}, {twoKey}),
                    ({older}, {two}, 'W1', 'Older', true, now() - interval '2 days', now(), {twoKey}, {twoKey}),
                    ({central}, {dead}, 'Central', 'Closed', false, now(), now(), {deadKeyNew}, {deadKeyNew});
                INSERT INTO "StockDocuments" ("Id", "TenantId", "Type", "Status", "Number", "DocumentDate", "WarehouseId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "PostedAt", "PostedBy") VALUES
                    ({document}, {two}, 'receipt', 'posted', 'SR-000001', '2026-10-09', {older}, now(), now(), {twoKey}, {twoKey}, now(), {twoKey}),
                    ({draft}, {two}, 'receipt', 'draft', NULL, '2026-10-09', {retired}, now(), now(), {twoKey}, {twoKey}, NULL, NULL);
                INSERT INTO "StockLedgerEntries" ("Id", "TenantId", "ArticleId", "WarehouseId", "Quantity", "DocumentId", "LineNo", "DocumentDate", "PostedAt", "PostedBy") VALUES
                    (gen_random_uuid(), {two}, {a}, {older}, 100, {document}, 1, '2026-10-09', now(), {twoKey}),
                    (gen_random_uuid(), {two}, {a}, {older}, -30.5, {document}, 2, '2026-10-09', now(), {twoKey}),
                    (gen_random_uuid(), {two}, {a}, {newer}, 0.000001, {document}, 3, '2026-10-09', now(), {twoKey}),
                    (gen_random_uuid(), {two}, {b}, {newer}, 2.5, {document}, 4, '2026-10-09', now(), {twoKey}),
                    (gen_random_uuid(), {two}, {b}, {newer}, -2.5, {document}, 5, '2026-10-09', now(), {twoKey}),
                    (gen_random_uuid(), {two}, {b}, {retired}, 999999999.999999, {document}, 6, '2026-10-09', now(), {twoKey});
                """);

            await migrator.MigrateAsync();

            var warehouses = await db.Database.SqlQuery<MigratedWarehouse>($"""
                SELECT "TenantId", "Id", "Code", "Name", "IsActive", "IsDefault", "CreatedBy", "UpdatedBy" FROM "Warehouses"
                """).ToListAsync();
            // R29: exactly one default per tenant, and it is active.
            foreach (var tenant in new[] { two, dead, none })
                Assert.True(Assert.Single(warehouses, w => w.TenantId == tenant && w.IsDefault).IsActive);
            // The older of the active warehouses - not the oldest warehouse, which is inactive; nothing else of the tenant changed.
            Assert.Equal(older, warehouses.Single(w => w.TenantId == two && w.IsDefault).Id);
            Assert.Equal(3, warehouses.Count(w => w.TenantId == two));
            // CENTRAL is taken (codes are case-insensitive): a new CENTRAL-2, written by the tenant's oldest key.
            var second = warehouses.Single(w => w.TenantId == dead && w.IsDefault);
            Assert.Equal(("CENTRAL-2", "Central warehouse", deadKeyOld, deadKeyOld), (second.Code, second.Name, second.CreatedBy, second.UpdatedBy));
            Assert.Equal(new[] { "CENTRAL-2", "Central" }, warehouses.Where(w => w.TenantId == dead).Select(w => w.Code).Order(StringComparer.Ordinal).ToArray());
            Assert.False(warehouses.Single(w => w.Id == central).IsActive);
            // No warehouse at all: a new CENTRAL.
            var created = Assert.Single(warehouses, w => w.TenantId == none);
            Assert.Equal(("CENTRAL", "Central warehouse", true, noneKey, noneKey), (created.Code, created.Name, created.IsDefault, created.CreatedBy, created.UpdatedBy));

            // R30: for every pair the stored balance equals the ledger sum - a pair that sums to zero included.
            var balances = await db.Database.SqlQuery<MigratedBalance>($"""
                SELECT "TenantId", "WarehouseId", "ArticleId", "Quantity" FROM "StockBalances"
                """).ToListAsync();
            Assert.Equal(
                new[]
                {
                    new MigratedBalance(two, older, a, 69.5m), new MigratedBalance(two, newer, a, 0.000001m),
                    new MigratedBalance(two, newer, b, 0m), new MigratedBalance(two, retired, b, 999999999.999999m),
                }.OrderBy(x => x.WarehouseId).ThenBy(x => x.ArticleId),
                balances.OrderBy(x => x.WarehouseId).ThenBy(x => x.ArticleId));
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM (
                    SELECT "TenantId", "WarehouseId", "ArticleId", SUM("Quantity") AS "Quantity" FROM "StockLedgerEntries" GROUP BY 1, 2, 3
                ) ledger
                FULL JOIN "StockBalances" stored USING ("TenantId", "WarehouseId", "ArticleId")
                WHERE ledger."Quantity" IS DISTINCT FROM stored."Quantity"
                """).SingleAsync());
            // R31: no document changed its warehouse.
            Assert.Equal(new[] { retired, older }.Order(), (await db.Database.SqlQuery<Guid>($"""
                SELECT "WarehouseId" AS "Value" FROM "StockDocuments"
                """).ToListAsync()).Order());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await app.ExecuteSqlAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        }
    }
}
