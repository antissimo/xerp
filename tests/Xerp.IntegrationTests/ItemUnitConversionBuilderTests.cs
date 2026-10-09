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
/// Spec 007, the builder's own tests: AC-03 (what the migration does to lines that existed before), and the
/// cases of the spec as it is after its consistency pass that the acceptance tests, written before it, do not
/// have - the base unit is never checked for being active on a line (R12, AC-42, E5), and the addressing
/// arguments of the four tools follow 003/R15 under their own names (section 5).
/// </summary>
[Collection(XerpCollection.Name)]
public class ItemUnitConversionBuilderTests(XerpFixture app)
{
    private sealed record NoTenant(Guid? TenantId = null, Guid? ApiKeyId = null) : ITenantContext;

    // ---- AC-03 ----

    [Fact]
    public async Task AC03_The_migration_gives_existing_lines_their_base_unit_and_posted_lines_factor_one()
    {
        // A database of its own, migrated to the state before this spec, filled, then migrated to the end.
        var database = "xerp_mig_" + Guid.NewGuid().ToString("N");
        await app.ExecuteSqlAsync($"""CREATE DATABASE "{database}" """);
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using var db = new XerpDbContext(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(connectionString).Options, new NoTenant());
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("StockTransfersReversal");

            var (tenant, key, pcs, kg, a, b, w) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (draft, posted, reversed, reversing) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Tenants" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ({tenant}, 'mig', 'Migrated', true, now());
                INSERT INTO "ApiKeys" ("Id", "TenantId", "Name", "ActorType", "KeyHash", "IsActive", "CreatedAt")
                    VALUES ({key}, {tenant}, 'first', 'human', {XerpFixture.Sha256Hex(database)}, true, now());
                INSERT INTO "UnitsOfMeasure" ("Id", "TenantId", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({pcs}, {tenant}, 'pcs', 'Piece', true, now(), now(), {key}, {key}),
                    ({kg}, {tenant}, 'kg', 'Kilogram', false, now(), now(), {key}, {key});
                INSERT INTO "Articles" ("Id", "TenantId", "Code", "Name", "Type", "BaseUnitId", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy") VALUES
                    ({a}, {tenant}, 'A', 'Article A', 'stock', {pcs}, true, now(), now(), {key}, {key}),
                    ({b}, {tenant}, 'B', 'Article B', 'stock', {kg}, true, now(), now(), {key}, {key});
                INSERT INTO "Warehouses" ("Id", "TenantId", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy")
                    VALUES ({w}, {tenant}, 'W1', 'Main', true, now(), now(), {key}, {key});
                INSERT INTO "StockDocuments" ("Id", "TenantId", "Type", "Status", "Number", "DocumentDate", "WarehouseId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "PostedAt", "PostedBy") VALUES
                    ({draft}, {tenant}, 'receipt', 'draft', NULL, '2026-10-09', {w}, now(), now(), {key}, {key}, NULL, NULL),
                    ({posted}, {tenant}, 'receipt', 'posted', 'SR-000001', '2026-10-09', {w}, now(), now(), {key}, {key}, now(), {key}),
                    ({reversed}, {tenant}, 'receipt', 'posted', 'SR-000002', '2026-10-09', {w}, now(), now(), {key}, {key}, now(), {key});
                INSERT INTO "StockDocuments" ("Id", "TenantId", "Type", "Status", "Number", "DocumentDate", "WarehouseId", "ReversalOfId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "PostedAt", "PostedBy")
                    VALUES ({reversing}, {tenant}, 'receipt', 'posted', 'SR-000003', '2026-10-09', {w}, {reversed}, now(), now(), {key}, {key}, now(), {key});
                UPDATE "StockDocuments" SET "Status" = 'reversed', "ReversedById" = {reversing} WHERE "Id" = {reversed};
                INSERT INTO "StockDocumentLines" ("Id", "TenantId", "DocumentId", "LineNo", "ArticleId", "Quantity") VALUES
                    (gen_random_uuid(), {tenant}, {draft}, 1, {a}, 5),
                    (gen_random_uuid(), {tenant}, {draft}, 2, {b}, 0.000001),
                    (gen_random_uuid(), {tenant}, {posted}, 1, {a}, 100),
                    (gen_random_uuid(), {tenant}, {posted}, 2, {b}, 2.5),
                    (gen_random_uuid(), {tenant}, {reversed}, 1, {b}, 999999999.999999),
                    (gen_random_uuid(), {tenant}, {reversing}, 1, {b}, 999999999.999999);
                """);

            await migrator.MigrateAsync();

            var lines = await db.Database.SqlQuery<MigratedLine>($"""
                SELECT d."Status" AS "Status", d."Number" AS "Number", l."LineNo" AS "LineNo", l."UnitId" AS "UnitId", l."Quantity" AS "Quantity",
                       l."Factor" AS "Factor", l."BaseQuantity" AS "BaseQuantity"
                FROM "StockDocumentLines" l JOIN "StockDocuments" d ON d."Id" = l."DocumentId"
                ORDER BY d."Number" NULLS FIRST, l."LineNo"
                """).ToListAsync();

            Assert.Equal(
                [
                    // A line that existed before has its article's base unit; a draft line has nothing else.
                    new MigratedLine("draft", null, 1, pcs, 5m, null, null),
                    new MigratedLine("draft", null, 2, kg, 0.000001m, null, null),
                    // A posted line has factor 1 and a base quantity equal to its quantity - reversed or reversing too.
                    new MigratedLine("posted", "SR-000001", 1, pcs, 100m, 1m, 100m),
                    new MigratedLine("posted", "SR-000001", 2, kg, 2.5m, 1m, 2.5m),
                    new MigratedLine("reversed", "SR-000002", 1, kg, 999999999.999999m, 1m, 999999999.999999m),
                    new MigratedLine("posted", "SR-000003", 1, kg, 999999999.999999m, 1m, 999999999.999999m),
                ],
                lines);
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "ArticleUnits" """).SingleAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await app.ExecuteSqlAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        }
    }

    private sealed record MigratedLine(string Status, string? Number, int LineNo, Guid UnitId, decimal Quantity, decimal? Factor, decimal? BaseQuantity);

    // ---- R12, AC-42, E5: the base unit is never checked for being active on a line ----

    [Fact]
    public async Task AC42_R12_A_line_in_the_articles_base_unit_is_accepted_after_that_unit_was_deactivated()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetUnitActiveAsync(s.Http, s.S.Unit, false);

        var omitted = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 3, null));
        var given = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 4, s.Pcs));
        // A draft that had no line in the base unit takes one, also by replace.
        var inBoxes = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1, s.Box));
        var replaced = await Stock.ReplaceAsync(s.Http, inBoxes.Id(), Units.Replacement(s.W1, (s.A, 1, s.Box), (s.A, 5, s.Pcs)));

        Units.AssertLine(omitted.DocumentLines()[0], "pcs", 3m, 1m, 3m);
        Units.AssertLine(given.DocumentLines()[0], "pcs", 4m, 1m, 4m);
        Units.AssertLine(replaced.DocumentLines()[1], "pcs", 5m, 1m, 5m);
        // E5: the two behave exactly alike, and both post (R18: units are not re-checked).
        await Stock.PostDocumentAsync(s.Http, omitted.Id());
        await Stock.PostDocumentAsync(s.Http, given.Id());
        Assert.Equal(7m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC42_R12_An_inactive_unit_that_is_another_articles_base_unit_is_still_refused()
    {
        // The exemption is for the line's own article: "REFERENCE_INACTIVE concerns alternative units only".
        var s = await Units.SetupAsync(app);
        var kg = await Uom.CreateAsync(s.Http, "kg", "Kilogram");
        var c = await Art.CreateAsync(s.Http, "C", "Article C", kg.Id());
        await Units.SetAsync(s.Http, s.A, kg.Id(), 2);
        await Units.SetUnitActiveAsync(s.Http, kg, false);

        using var asAlternative = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 1, kg.Id())));
        var asBase = await Units.CreateAsync(s.Http, "receipt", s.W1, (c.Id(), 1, kg.Id()));

        await Stock.ConflictAsync(asAlternative, "REFERENCE_INACTIVE", "lines[0].unitId");
        Units.AssertLine(asBase.DocumentLines()[0], "kg", 1m, 1m, 1m, "kg");
    }

    // ---- R17, E8 (007-q, T-Q7): what a draft shows when a line no longer converts ----

    [Fact]
    public async Task E8_A_draft_line_that_no_longer_converts_is_shown_with_the_current_factor_and_what_it_converts_to()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 0.000002m, s.Pack));

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.2m);

        var read = await Stock.GetAsync(s.Http, draft.Id());
        Units.AssertLine(read.DocumentLines()[0], "pack", 0.000002m, 0.2m, 0m);
        Assert.Equal(draft.Str("updatedAt"), read.Str("updatedAt"));
    }

    // ---- section 5: addressing arguments of the four tools (003/R15 under their own names) ----

    [Fact]
    public async Task S5_Addressing_arguments_missing_are_validation_errors_and_malformed_ones_address_no_record()
    {
        var s = await Units.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, s.Http, "agent", "agent");
        await using var mcp = await app.McpAsync(agent.Key);
        var (a, box) = (s.A, s.Box);

        // Missing, null, empty or not a JSON string: VALIDATION_FAILED under the argument's own name.
        await mcp.ErrorAsync("article_unit_set", new { articleId = a, factor = 6 }, "VALIDATION_FAILED", "unitId");
        await mcp.ErrorAsync("article_unit_set", new { unitId = box, factor = 6 }, "VALIDATION_FAILED", "articleId");
        await mcp.ErrorAsync("article_unit_set", new { factor = 6 }, "VALIDATION_FAILED", "articleId", "unitId");
        await mcp.ErrorAsync("article_unit_set", new { articleId = a, unitId = box }, "VALIDATION_FAILED", "factor");
        await mcp.ErrorAsync("article_unit_set", new { articleId = a, unitId = 12, factor = 6 }, "VALIDATION_FAILED", "unitId");
        await mcp.ErrorAsync("article_unit_get", new { articleId = a }, "VALIDATION_FAILED", "unitId");
        await mcp.ErrorAsync("article_unit_get", new { articleId = "", unitId = box }, "VALIDATION_FAILED", "articleId");
        await mcp.ErrorAsync("article_unit_get", new { articleId = a, unitId = (string?)null }, "VALIDATION_FAILED", "unitId");
        await mcp.ErrorAsync("article_unit_delete", new { unitId = box }, "VALIDATION_FAILED", "articleId");
        await mcp.ErrorAsync("article_unit_list", new { }, "VALIDATION_FAILED", "articleId");
        await mcp.ErrorAsync("article_unit_list", new { articleId = a, limit = 0 }, "VALIDATION_FAILED", "limit");
        await mcp.ErrorAsync("article_unit_list", new { articleId = a, unitId = box }, "VALIDATION_FAILED", "unitId"); // not an argument of list

        // A string that is not a UUID: NOT_FOUND, as the malformed path segment over HTTP (001/E6, AC-24).
        using var http = await s.Http.PutAsync($"{Units.Path(a)}/abc", HttpAssert.Raw("""{ "factor": 6 }"""));
        await HttpAssert.NotFoundAsync(http);
        await mcp.ErrorAsync("article_unit_set", new { articleId = a, unitId = "abc", factor = 6 }, "NOT_FOUND");
        await mcp.ErrorAsync("article_unit_set", new { articleId = "abc", unitId = box, factor = 6 }, "NOT_FOUND");
        await mcp.ErrorAsync("article_unit_get", new { articleId = a, unitId = "abc" }, "NOT_FOUND");
        await mcp.ErrorAsync("article_unit_delete", new { articleId = "abc", unitId = box }, "NOT_FOUND");
        await mcp.ErrorAsync("article_unit_list", new { articleId = "abc" }, "NOT_FOUND");

        // Well-formed ids that name nothing.
        await mcp.ErrorAsync("article_unit_list", new { articleId = Guid.NewGuid() }, "NOT_FOUND");
        await mcp.ErrorAsync("article_unit_get", new { articleId = a, unitId = s.Pack }, "NOT_FOUND");
        await mcp.ErrorAsync("article_unit_delete", new { articleId = a, unitId = s.Pack }, "NOT_FOUND");

        Assert.Equal(new[] { "box" }, (await Units.ListAsync(s.Http, a)).UnitCodes());
        Assert.Equal(12m, (await Units.GetAsync(s.Http, a, box)).Factor());
    }

    // ---- R9, R11: frozen base unit and delete of an article with conversions, through the same lock ----

    [Fact]
    public async Task R11_Deleting_an_article_while_a_conversion_is_set_for_it_never_fails_and_leaves_no_orphan()
    {
        for (var round = 0; round < 3; round++)
        {
            var s = await Units.SetupAsync(app);
            var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
            await Units.SetAsync(s.Http, article.Id(), s.Box, 12);

            var delete = Task.Run(() => s.Http.DeleteAsync($"{Art.Path}/{article.Id()}"));
            var set = Task.Run(() => Units.PutAsync(s.Http, article.Id(), s.Pack, 6));
            using var deleted = await delete;
            using var setting = await set;

            // The delete always succeeds: conversions never block it. The Set either came first (and its
            // conversion went with the article) or found no article.
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.True(setting.StatusCode is HttpStatusCode.Created or HttpStatusCode.NotFound, $"Set answered {(int)setting.StatusCode}.");
            Assert.Equal(0L, await app.ScalarAsync<long>(
                """SELECT count(*) FROM "ArticleUnits" WHERE "ArticleId" = @id""", ("id", article.Id())));
            using var deletePack = await Units.DeleteUnitAsync(s.Http, s.Pack);
            Assert.Equal(HttpStatusCode.NoContent, deletePack.StatusCode);
        }
    }
}
