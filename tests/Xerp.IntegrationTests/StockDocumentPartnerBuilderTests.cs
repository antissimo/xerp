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
/// Spec 011a, the builder's own criteria - what cannot be seen or provoked through the public surface:
/// AC-02 (the migration of documents that existed before), AC-03 (the partner is resolved in Application;
/// <c>Api</c> has no rule about partners) and T1 (the foreign key as the second barrier).
/// </summary>
[Collection(XerpCollection.Name)]
public class StockDocumentPartnerBuilderTests(XerpFixture app)
{
    private sealed record NoTenant(Guid? TenantId = null, Guid? ApiKeyId = null) : ITenantContext;

    private sealed record MigratedDocument(Guid Id, Guid? PartnerId);

    // ---- AC-02 ----

    [Fact]
    public async Task AC02_The_migration_gives_every_linked_document_its_orders_partner_and_leaves_unlinked_ones_without()
    {
        // A database of its own, migrated to the state before this spec, filled, then migrated to the end.
        var database = "xerp_mig_" + Guid.NewGuid().ToString("N");
        await app.ExecuteSqlAsync($"""CREATE DATABASE "{database}" """);
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using var db = new XerpDbContext(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(connectionString).Options, new NoTenant());
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("DefaultWarehouseStockBalance");

            // Tenant "one": a purchase order of SUP and a sales order of CUS, with a posted linked receipt, the
            // document that reversed a second linked receipt (and that receipt), a posted delivery, a linked
            // draft, and unlinked documents - a receipt, an issue and a transfer. A partner nothing names.
            // Tenant "two": its own supplier, order and linked receipt.
            var (one, two) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            var (key1, key2) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            var (w1, w1b, w2) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (sup, cus, idle, sup2) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (po, so, po2) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (receipt, reversed, reversing, delivery, linkedDraft) =
                (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (manualReceipt, manualIssue, transfer, receipt2) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Tenants" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES
                    ({one}, 'one', 'One', true, now()), ({two}, 'two', 'Two', true, now());
                INSERT INTO "ApiKeys" ("Id", "TenantId", "Name", "ActorType", "KeyHash", "IsActive", "CreatedAt") VALUES
                    ({key1}, {one}, 'first', 'human', {XerpFixture.Sha256Hex(database + "1")}, true, now()),
                    ({key2}, {two}, 'first', 'human', {XerpFixture.Sha256Hex(database + "2")}, true, now());
                INSERT INTO "Warehouses" ("Id", "TenantId", "Code", "Name", "IsActive", "IsDefault", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({w1}, {one}, 'W1', 'One', true, true, now(), now(), {key1}, {key1}),
                    ({w1b}, {one}, 'W2', 'Other', true, false, now(), now(), {key1}, {key1}),
                    ({w2}, {two}, 'W1', 'Two', true, true, now(), now(), {key2}, {key2});
                INSERT INTO "Partners" ("Id", "TenantId", "Code", "Name", "IsCustomer", "IsSupplier", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({sup}, {one}, 'SUP', 'Supplier', false, true, true, now(), now(), {key1}, {key1}),
                    ({cus}, {one}, 'CUS', 'Customer', true, false, false, now(), now(), {key1}, {key1}),
                    ({idle}, {one}, 'IDLE', 'Named by nothing', true, true, true, now(), now(), {key1}, {key1}),
                    ({sup2}, {two}, 'SUP', 'Supplier of two', false, true, true, now(), now(), {key2}, {key2});
                INSERT INTO "PurchaseOrders" ("Id", "TenantId", "Status", "Number", "OrderDate", "SupplierId", "WarehouseId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "ConfirmedAt", "ConfirmedBy") VALUES
                    ({po}, {one}, 'confirmed', 'PO-000001', '2026-10-09', {sup}, {w1}, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({po2}, {two}, 'confirmed', 'PO-000001', '2026-10-09', {sup2}, {w2}, now(), now(), {key2}, {key2}, now(), {key2});
                INSERT INTO "SalesOrders" ("Id", "TenantId", "Status", "Number", "OrderDate", "CustomerId", "WarehouseId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "ConfirmedAt", "ConfirmedBy") VALUES
                    ({so}, {one}, 'confirmed', 'SO-000001', '2026-10-09', {cus}, {w1}, now(), now(), {key1}, {key1}, now(), {key1});
                INSERT INTO "StockDocuments" ("Id", "TenantId", "Type", "Status", "Number", "DocumentDate", "WarehouseId", "ToWarehouseId", "PurchaseOrderId", "SalesOrderId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "PostedAt", "PostedBy") VALUES
                    ({receipt}, {one}, 'receipt', 'posted', 'SR-000001', '2026-10-09', {w1}, NULL, {po}, NULL, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({reversed}, {one}, 'receipt', 'reversed', 'SR-000002', '2026-10-09', {w1}, NULL, {po}, NULL, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({reversing}, {one}, 'receipt', 'posted', 'SR-000003', '2026-10-09', {w1}, NULL, {po}, NULL, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({delivery}, {one}, 'issue', 'posted', 'SI-000001', '2026-10-09', {w1}, NULL, NULL, {so}, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({linkedDraft}, {one}, 'receipt', 'draft', NULL, '2026-10-09', {w1}, NULL, {po}, NULL, now(), now(), {key1}, {key1}, NULL, NULL),
                    ({manualReceipt}, {one}, 'receipt', 'posted', 'SR-000004', '2026-10-09', {w1}, NULL, NULL, NULL, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({manualIssue}, {one}, 'issue', 'draft', NULL, '2026-10-09', {w1}, NULL, NULL, NULL, now(), now(), {key1}, {key1}, NULL, NULL),
                    ({transfer}, {one}, 'transfer', 'posted', 'ST-000001', '2026-10-09', {w1}, {w1b}, NULL, NULL, now(), now(), {key1}, {key1}, now(), {key1}),
                    ({receipt2}, {two}, 'receipt', 'posted', 'SR-000001', '2026-10-09', {w2}, NULL, {po2}, NULL, now(), now(), {key2}, {key2}, now(), {key2});
                UPDATE "StockDocuments" SET "ReversalOfId" = {reversed} WHERE "Id" = {reversing};
                UPDATE "StockDocuments" SET "ReversedById" = {reversing} WHERE "Id" = {reversed};
                """);

            await migrator.MigrateAsync();

            var partners = (await db.Database.SqlQuery<MigratedDocument>($"""SELECT "Id", "PartnerId" FROM "StockDocuments" """).ToListAsync())
                .ToDictionary(d => d.Id, d => d.PartnerId);
            Assert.Equal(9, partners.Count);
            // R17: the linked ones - posted, reversed, reversing and draft - have the order's supplier / customer,
            // also a customer deactivated since (R10) ...
            Assert.Equal(sup, partners[receipt]);
            Assert.Equal(sup, partners[reversed]);
            Assert.Equal(sup, partners[reversing]);
            Assert.Equal(cus, partners[delivery]);
            Assert.Equal(sup, partners[linkedDraft]);
            // ... each from its own tenant's order ...
            Assert.Equal(sup2, partners[receipt2]);
            // ... and the unlinked ones have none.
            Assert.Null(partners[manualReceipt]);
            Assert.Null(partners[manualIssue]);
            Assert.Null(partners[transfer]);
            // R8 holds for all data: no linked document without its order's partner, no unlinked one with a partner.
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM "StockDocuments" d
                LEFT JOIN "PurchaseOrders" p ON p."TenantId" = d."TenantId" AND p."Id" = d."PurchaseOrderId"
                LEFT JOIN "SalesOrders" s ON s."TenantId" = d."TenantId" AND s."Id" = d."SalesOrderId"
                WHERE d."PartnerId" IS DISTINCT FROM COALESCE(p."SupplierId", s."CustomerId")
                """).SingleAsync());
            // R15 by the database: a partner a stock document names cannot be deleted, another one can.
            var used = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""DELETE FROM "Partners" WHERE "Id" = {idle} OR "Id" = {cus}"""));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, used.SqlState);
            Assert.Equal(1, await db.Database.ExecuteSqlAsync($"""DELETE FROM "Partners" WHERE "Id" = {idle}"""));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await app.ExecuteSqlAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        }
    }

    // ---- T1 ----

    private Task<Guid?> StoredPartnerAsync(Guid document) =>
        app.ScalarAsync<Guid?>("""SELECT "PartnerId" FROM "StockDocuments" WHERE "Id" = @id""", ("id", document));

    [Fact]
    public async Task T1_The_partner_is_stored_on_the_document_and_the_database_refuses_another_tenants_partner()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var other = await PartnerDocs.SetupAsync(app);
        var manual = (await PartnerDocs.ReceiptAsync(s, s.Sup)).Id();
        var without = (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1))).Id();
        var transfer = (await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1))).Id();

        Assert.Equal(s.Sup, await StoredPartnerAsync(manual));
        Assert.Null(await StoredPartnerAsync(without));
        Assert.Null(await StoredPartnerAsync(transfer));

        var foreign = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync("""UPDATE "StockDocuments" SET "PartnerId" = @p WHERE "Id" = @id""", ("p", other.Sup), ("id", without)));
        var unknown = await Assert.ThrowsAsync<PostgresException>(() =>
            app.ExecuteSqlAsync("""UPDATE "StockDocuments" SET "PartnerId" = @p WHERE "Id" = @id""", ("p", Guid.CreateVersion7()), ("id", without)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknown.SqlState);
        Assert.Null(await StoredPartnerAsync(without));
        PartnerDocs.AssertNoPartner(await Stock.GetAsync(s.Http, without));
    }

    // ---- AC-03 ----

    [Fact]
    public void AC03_Api_has_no_rule_about_partners_the_partner_of_a_document_is_resolved_in_Application()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Xerp.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        // What a rule about partners would have to touch: the partners themselves, their role, the checks.
        // The tool catalog only describes the rule in words, so its string literals are not code.
        string[] marks = ["PartnerChecks", "PartnerState", "IXerpDb", "IsSupplier", "IsCustomer", "PartnerRoleMissing(", "OrderMismatch("];
        var offenders = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Xerp.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadLines(f).Any(line => !line.TrimStart().StartsWith("//") && marks.Any(m => line.Contains(m, StringComparison.Ordinal))))
            .Select(f => Path.GetFileName(f)!)
            .ToArray();
        Assert.Empty(offenders);

        // And the one place that decides: the operation that saves a stock document, for HTTP and MCP alike.
        var application = Path.Combine(root.FullName, "src", "Xerp.Application");
        var deciding = Directory.EnumerateFiles(application, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("StockPartnerChecks.Role(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!)
            .ToArray();
        Assert.Equal(["StockDocumentOperations.cs"], deciding);
    }
}
