using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Rules;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, the builder's own criteria - what cannot be seen or provoked through the public surface: AC-02
/// (the two tables are tenant-owned and filtered), AC-05 (a stored value whose key is no rule is ignored),
/// AC-06 (the migration), R3 (creating a tenant writes no rule value), R10 / S2 (the history is append-only) and
/// R19 (the database no longer refuses a balance below zero, and the stored balance still is the ledger's sum).
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleBuilderTests(XerpFixture app)
{
    private sealed record NoTenant(Guid? TenantId = null, Guid? ApiKeyId = null) : ITenantContext;

    private sealed record Actor(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private XerpDbContext Db(ITenantContext context) =>
        new(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(app.ConnectionString).Options, context);

    private Task<long> CountAsync(string table, Guid tenant) =>
        app.ScalarAsync<long>($"""SELECT count(*) FROM "{table}" WHERE "TenantId" = @t""", ("t", tenant));

    // ---- AC-02 ----

    [Fact]
    public void AC02_RuleValue_and_RuleChange_are_tenant_owned_filtered_and_keyed_with_the_tenant()
    {
        using var scope = app.Factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<XerpDbContext>().Model;

        foreach (var type in new[] { typeof(RuleValue), typeof(RuleChange) })
        {
            var entity = model.FindEntityType(type);
            Assert.True(entity is not null, $"{type.Name} is not part of the model.");
            Assert.True(typeof(ITenantOwned).IsAssignableFrom(type));
            Assert.NotEmpty(entity!.GetDeclaredQueryFilters());
            // The actor is a key of the same tenant, and deleting that key is restricted (architecture section 8).
            var toKey = Assert.Single(entity.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(Xerp.Domain.Tenancy.ApiKey));
            Assert.Equal("TenantId", toKey.Properties[0].Name);
            Assert.Equal(DeleteBehavior.Restrict, toKey.DeleteBehavior);
        }
        Assert.Equal(["TenantId", "Key"], model.FindEntityType(typeof(RuleValue))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        // No foreign key on Key: the registry is code.
        Assert.Equal(2, model.FindEntityType(typeof(RuleValue))!.GetForeignKeys().Count());
        Assert.Equal(2, model.FindEntityType(typeof(RuleChange))!.GetForeignKeys().Count());
    }

    [Fact]
    public async Task AC02_T1_The_values_and_the_history_of_one_tenant_are_invisible_to_the_context_of_another()
    {
        var x = await app.NewTenantAsync();
        var y = await app.NewTenantAsync();
        await Rules.SetAsync(x.Client, Rules.NegativeStock, true);
        await Rules.SetAsync(x.Client, Rules.Protected, true);
        await Rules.ResetAsync(x.Client, Rules.Protected);

        await using var asY = Db(new Actor(y.Id, y.ApiKeyId));
        await using var asX = Db(new Actor(x.Id, x.ApiKeyId));
        await using var asNobody = Db(new NoTenant());

        Assert.Empty(await asY.RuleValues.ToListAsync());
        Assert.Empty(await asY.RuleChanges.ToListAsync());
        Assert.Empty(await asNobody.RuleValues.ToListAsync());
        Assert.Empty(await asNobody.RuleChanges.ToListAsync());
        Assert.Equal([Rules.NegativeStock], (await asX.RuleValues.ToListAsync()).Select(v => v.Key));
        Assert.Equal(3, await asX.RuleChanges.CountAsync());
        // The port answers for the tenant of the context alone.
        Assert.True((await new Xerp.Infrastructure.Rules.TenantRuleReader(asX).ReadAsync())[RuleRegistry.NegativeStockAllowed]);
        Assert.False((await new Xerp.Infrastructure.Rules.TenantRuleReader(asY).ReadAsync())[RuleRegistry.NegativeStockAllowed]);
        Assert.False((await new Xerp.Infrastructure.Rules.TenantRuleReader(asNobody).ReadAsync())[RuleRegistry.NegativeStockAllowed]);
    }

    [Fact]
    public async Task AC02_The_database_refuses_a_value_or_a_change_attributed_to_a_key_of_another_tenant()
    {
        var x = await app.NewTenantAsync();
        var y = await app.NewTenantAsync();

        var value = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteSqlAsync(
            """INSERT INTO "RuleValues" ("TenantId", "Key", "Value", "UpdatedAt", "UpdatedBy") VALUES (@t, 'stock.negativeStockAllowed', TRUE, now(), @k)""",
            ("t", x.Id), ("k", y.ApiKeyId)));
        var change = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteSqlAsync(
            """INSERT INTO "RuleChanges" ("Id", "TenantId", "Key", "Action", "OldValue", "NewValue", "ChangedAt", "ChangedBy") VALUES (@id, @t, 'stock.negativeStockAllowed', 'set', FALSE, TRUE, now(), @k)""",
            ("id", Guid.CreateVersion7()), ("t", x.Id), ("k", y.ApiKeyId)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, value.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, change.SqlState);
        Assert.Equal((0L, 0L), (await CountAsync("RuleValues", x.Id), await CountAsync("RuleChanges", x.Id)));
    }

    // ---- R3, R8, R9, R10: what is stored ----

    [Fact]
    public async Task R3_Creating_a_tenant_writes_no_rule_value_and_a_value_is_stored_only_while_the_tenant_has_set_it()
    {
        var tenant = await app.NewTenantAsync();
        Assert.Equal((0L, 0L), (await CountAsync("RuleValues", tenant.Id), await CountAsync("RuleChanges", tenant.Id)));

        // Reading stores nothing.
        await Rules.ListAsync(tenant.Client);
        await Rules.GetAsync(tenant.Client, Rules.NegativeStock);
        await Rules.ResetAsync(tenant.Client, Rules.NegativeStock);
        Assert.Equal((0L, 0L), (await CountAsync("RuleValues", tenant.Id), await CountAsync("RuleChanges", tenant.Id)));

        await Rules.SetAsync(tenant.Client, Rules.NegativeStock, true);
        await Rules.SetAsync(tenant.Client, Rules.NegativeStock, true);
        Assert.Equal((1L, 1L), (await CountAsync("RuleValues", tenant.Id), await CountAsync("RuleChanges", tenant.Id)));

        // R9: reset removes the row; the history keeps both changes.
        await Rules.ResetAsync(tenant.Client, Rules.NegativeStock);
        Assert.Equal((0L, 2L), (await CountAsync("RuleValues", tenant.Id), await CountAsync("RuleChanges", tenant.Id)));
    }

    [Fact]
    public async Task R10_S2_The_history_of_rule_changes_is_append_only_whatever_code_asks_for_the_save()
    {
        var tenant = await app.NewTenantAsync();
        await Rules.SetAsync(tenant.Client, Rules.OverReceipt, true);

        await using var db = Db(new Actor(tenant.Id, tenant.ApiKeyId));
        var change = await db.RuleChanges.SingleAsync();
        db.Entry(change).Property(c => c.NewValue).CurrentValue = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.RuleChanges.Remove(await db.RuleChanges.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Rules.AssertChange(Assert.Single((await Rules.ChangesAsync(tenant.Client)).Items()), Rules.OverReceipt, "set", false, true, tenant.ApiKeyId);
    }

    // ---- AC-05, S4 ----

    [Fact]
    public async Task AC05_A_stored_value_whose_key_is_no_rule_is_not_listed_and_nothing_fails()
    {
        var s = await Orders.SetupAsync(app);
        var before = await Rules.ListAsync(s.Http);
        // Written directly: no operation can store a key that is no rule. Among them the keys of the first text of the spec.
        foreach (var key in new[] { "stock.negativeStock", "quantity.decimals", "nope", "Stock.NegativeStockAllowed", "partner.roleRequired" })
            Assert.Equal(1, await app.ExecuteSqlAsync(
                """INSERT INTO "RuleValues" ("TenantId", "Key", "Value", "UpdatedAt", "UpdatedBy") VALUES (@t, @key, TRUE, now(), @k)""",
                ("t", s.Tenant.Id), ("key", key), ("k", s.Tenant.ApiKeyId)));

        // Not listed, not counted, not found - and the six rules are untouched.
        var list = await Rules.ListAsync(s.Http);
        McpAssert.JsonEqual(before, list, "A stored value with an unknown key changed the list of rules");
        Assert.Equal(Rules.Keys, list.RuleKeys());
        Assert.Equal(0, (await Rules.ListAsync(s.Http, "?source=tenant")).Total());
        using (var unknown = await s.Http.GetAsync(Rules.PathOf("stock.negativeStock")))
            await HttpAssert.NotFoundAsync(unknown);
        using (var reset = await Rules.SendResetAsync(s.Http, "stock.negativeStock"))
            await HttpAssert.NotFoundAsync(reset);
        using (var set = await Rules.SendSetAsync(s.Http, "quantity.decimals", true))
            await HttpAssert.NotFoundAsync(set);
        Assert.Equal(5L, await CountAsync("RuleValues", s.Tenant.Id));

        // The operations that read rules are judged by the defaults, as before.
        await Rules.AssertDefaultBehaviourAsync(s, named: true);
        // And a real rule is set and reset next to them.
        Rules.AssertTenantValue(await Rules.SetAsync(s.Http, Rules.NegativeStock, true), Rules.NegativeStock, true, s.Tenant.ApiKeyId);
        Assert.Equal(new[] { Rules.NegativeStock }, (await Rules.ListAsync(s.Http, "?source=tenant")).RuleKeys());
        Rules.AssertAtDefault(await Rules.ResetAsync(s.Http, Rules.NegativeStock), Rules.NegativeStock);
        Assert.Equal(5L, await CountAsync("RuleValues", s.Tenant.Id));
    }

    // ---- R19: the stored balance below zero ----

    [Fact]
    public async Task R19_The_stored_balance_goes_below_zero_with_the_ledger_and_rebuild_restores_a_negative_sum()
    {
        var s = await Stock.SetupAsync(app);
        await Rules.SetAsync(s.Http, Rules.NegativeStock, true);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 5);

        Task<decimal?> StoredAsync() => app.ScalarAsync<decimal?>(
            """SELECT "Quantity" FROM "StockBalances" WHERE "TenantId" = @t AND "WarehouseId" = @w AND "ArticleId" = @a""",
            ("t", s.Tenant.Id), ("w", s.W1), ("a", s.A));

        Assert.Equal(-5m, await StoredAsync());
        await Rules.AssertBalancedAsync(s.Http, "with a negative pair");

        // A corrupted row is found by verify and set to the ledger's sum by rebuild - below zero too.
        Assert.Equal(1, await app.ExecuteSqlAsync(
            """UPDATE "StockBalances" SET "Quantity" = 1 WHERE "TenantId" = @t AND "WarehouseId" = @w AND "ArticleId" = @a""",
            ("t", s.Tenant.Id), ("w", s.W1), ("a", s.A)));
        Assert.Equal(1, (await Balance.DifferenceListAsync(s.Http)).Total());
        using (var rebuild = await s.Http.PostAsync("/api/v1/stock-balances/rebuild", null))
            Assert.Equal(1, (await HttpAssert.JsonAsync(rebuild, HttpStatusCode.OK)).GetProperty("corrected").GetInt32());
        Assert.Equal(-5m, await StoredAsync());
        await Rules.AssertBalancedAsync(s.Http, "after the rebuild");
    }

    // ---- AC-06 ----

    private sealed record MigratedOrder(Guid Id, Guid? PartnerId);

    [Fact]
    public async Task AC06_The_migration_adds_the_two_tables_empty_drops_the_balance_check_and_makes_the_order_partner_nullable()
    {
        // A database of its own, migrated to the state before this spec, filled, then migrated to the end.
        var database = "xerp_mig_" + Guid.NewGuid().ToString("N");
        await app.ExecuteSqlAsync($"""CREATE DATABASE "{database}" """);
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using var db = new XerpDbContext(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(connectionString).Options, new NoTenant());
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("PartnerOnStockDocuments");

            // A tenant with a confirmed purchase order and a confirmed sales order, a posted receipt linked to
            // the first, an unlinked posted issue, their ledger entries and the balance they sum to (10 - 4).
            var (tenant, key, uom, article, warehouse) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (sup, cus, po, so, receipt, issue) =
                (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Tenants" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ({tenant}, 'one', 'One', true, now());
                INSERT INTO "ApiKeys" ("Id", "TenantId", "Name", "ActorType", "KeyHash", "IsActive", "CreatedAt") VALUES
                    ({key}, {tenant}, 'first', 'human', {XerpFixture.Sha256Hex(database)}, true, now());
                INSERT INTO "UnitsOfMeasure" ("Id", "TenantId", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({uom}, {tenant}, 'pcs', 'Piece', true, now(), now(), {key}, {key});
                INSERT INTO "Articles" ("Id", "TenantId", "Code", "Name", "Type", "BaseUnitId", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({article}, {tenant}, 'A', 'Article', 'stock', {uom}, true, now(), now(), {key}, {key});
                INSERT INTO "Warehouses" ("Id", "TenantId", "Code", "Name", "IsActive", "IsDefault", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({warehouse}, {tenant}, 'W1', 'One', true, true, now(), now(), {key}, {key});
                INSERT INTO "Partners" ("Id", "TenantId", "Code", "Name", "IsCustomer", "IsSupplier", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({sup}, {tenant}, 'SUP', 'Supplier', false, true, true, now(), now(), {key}, {key}),
                    ({cus}, {tenant}, 'CUS', 'Customer', true, false, true, now(), now(), {key}, {key});
                INSERT INTO "PurchaseOrders" ("Id", "TenantId", "Status", "Number", "OrderDate", "SupplierId", "WarehouseId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "ConfirmedAt", "ConfirmedBy") VALUES
                    ({po}, {tenant}, 'confirmed', 'PO-000001', '2026-10-09', {sup}, {warehouse}, now(), now(), {key}, {key}, now(), {key});
                INSERT INTO "SalesOrders" ("Id", "TenantId", "Status", "Number", "OrderDate", "CustomerId", "WarehouseId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "ConfirmedAt", "ConfirmedBy") VALUES
                    ({so}, {tenant}, 'confirmed', 'SO-000001', '2026-10-09', {cus}, {warehouse}, now(), now(), {key}, {key}, now(), {key});
                INSERT INTO "StockDocuments" ("Id", "TenantId", "Type", "Status", "Number", "DocumentDate", "WarehouseId", "PurchaseOrderId", "PartnerId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "PostedAt", "PostedBy") VALUES
                    ({receipt}, {tenant}, 'receipt', 'posted', 'SR-000001', '2026-10-09', {warehouse}, {po}, {sup}, now(), now(), {key}, {key}, now(), {key}),
                    ({issue}, {tenant}, 'issue', 'posted', 'SI-000001', '2026-10-09', {warehouse}, NULL, NULL, now(), now(), {key}, {key}, now(), {key});
                INSERT INTO "StockLedgerEntries" ("Id", "TenantId", "ArticleId", "WarehouseId", "Quantity", "DocumentId", "LineNo", "DocumentDate", "PostedAt", "PostedBy") VALUES
                    ({Guid.CreateVersion7()}, {tenant}, {article}, {warehouse}, 10, {receipt}, 1, '2026-10-09', now(), {key}),
                    ({Guid.CreateVersion7()}, {tenant}, {article}, {warehouse}, -4, {issue}, 1, '2026-10-09', now(), {key});
                INSERT INTO "StockBalances" ("TenantId", "WarehouseId", "ArticleId", "Quantity") VALUES ({tenant}, {warehouse}, {article}, 6);
                """);
            // Before: the database refuses a negative balance and an order without a partner.
            var refusedBefore = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""UPDATE "StockBalances" SET "Quantity" = -1"""));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refusedBefore.SqlState);

            await migrator.MigrateAsync();

            // Both tables exist and are empty.
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RuleValues" """).SingleAsync());
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RuleChanges" """).SingleAsync());
            // StockBalance has no check constraint on Quantity (nor any other).
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_constraint WHERE contype = 'c' AND conrelid = 'public."StockBalances"'::regclass
                """).SingleAsync());
            // SupplierId and CustomerId are nullable, every existing value is kept, and their foreign keys and indexes stay.
            Assert.Equal(["YES", "YES"], await db.Database.SqlQuery<string>($"""
                SELECT is_nullable AS "Value" FROM information_schema.columns
                WHERE table_schema = 'public' AND (table_name, column_name) IN (('PurchaseOrders', 'SupplierId'), ('SalesOrders', 'CustomerId'))
                """).ToListAsync());
            Assert.Equal(sup, (await db.Database.SqlQuery<MigratedOrder>($"""SELECT "Id", "SupplierId" AS "PartnerId" FROM "PurchaseOrders" """).SingleAsync()).PartnerId);
            Assert.Equal(cus, (await db.Database.SqlQuery<MigratedOrder>($"""SELECT "Id", "CustomerId" AS "PartnerId" FROM "SalesOrders" """).SingleAsync()).PartnerId);
            Assert.Equal(2, await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_constraint
                WHERE contype = 'f' AND conname IN ('FK_PurchaseOrders_Partners_TenantId_SupplierId', 'FK_SalesOrders_Partners_TenantId_CustomerId')
                """).SingleAsync());
            Assert.Equal(2, await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_indexes
                WHERE indexname IN ('IX_PurchaseOrders_TenantId_SupplierId', 'IX_SalesOrders_TenantId_CustomerId')
                """).SingleAsync());
            // No existing row changed, and Balanced holds: every stored balance is the sum of its ledger.
            Assert.Equal(6m, await db.Database.SqlQuery<decimal>($"""SELECT "Quantity" AS "Value" FROM "StockBalances" """).SingleAsync());
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM "StockBalances" b
                WHERE b."Quantity" <> (SELECT COALESCE(sum(e."Quantity"), 0) FROM "StockLedgerEntries" e
                    WHERE e."TenantId" = b."TenantId" AND e."WarehouseId" = b."WarehouseId" AND e."ArticleId" = b."ArticleId")
                """).SingleAsync());
            Assert.Equal(sup, await db.Database.SqlQuery<Guid>($"""SELECT "PartnerId" AS "Value" FROM "StockDocuments" WHERE "Id" = {receipt}""").SingleAsync());
            // After: the database stores what the rules may now allow.
            Assert.Equal(1, await db.Database.ExecuteSqlAsync($"""UPDATE "StockBalances" SET "Quantity" = -1"""));
            Assert.Equal(1, await db.Database.ExecuteSqlAsync($"""UPDATE "PurchaseOrders" SET "SupplierId" = NULL"""));
            Assert.Equal(1, await db.Database.ExecuteSqlAsync($"""UPDATE "SalesOrders" SET "CustomerId" = NULL"""));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await app.ExecuteSqlAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        }
    }
}
