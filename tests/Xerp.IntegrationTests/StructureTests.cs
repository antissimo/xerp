using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xerp.Domain.Common;
using Xerp.Domain.Tenancy;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

[Collection(XerpCollection.Name)]
public class StructureTests(XerpFixture app)
{
    [Fact]
    public void AC06_Every_entity_except_Tenant_is_tenant_owned_and_has_a_query_filter()
    {
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XerpDbContext>();
        var entityTypes = db.Model.GetEntityTypes().ToList();

        Assert.Contains(entityTypes, t => t.ClrType == typeof(Tenant));
        Assert.Contains(entityTypes, t => t.ClrType == typeof(ApiKey));
        Assert.Contains(entityTypes, t => t.ClrType == typeof(Xerp.Domain.Inventory.UnitOfMeasure));

        foreach (var entityType in entityTypes.Where(t => t.ClrType != typeof(Tenant)))
        {
            Assert.True(typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType), $"{entityType.Name} does not implement ITenantOwned.");
            var tenantId = entityType.FindProperty("TenantId");
            Assert.True(tenantId is not null, $"{entityType.Name} has no TenantId property.");
            Assert.Equal(typeof(Guid), tenantId!.ClrType);
            Assert.False(tenantId.IsNullable);
            Assert.True(entityType.GetDeclaredQueryFilters().Count > 0, $"{entityType.Name} has no global query filter.");
        }

        var tenant = entityTypes.Single(t => t.ClrType == typeof(Tenant));
        Assert.Null(tenant.FindProperty("TenantId"));
    }

    [Fact]
    public async Task AC06_Every_table_except_Tenants_has_a_TenantId_that_references_Tenants()
    {
        // No literal list (architecture section 9): the invariant is asserted on every table that exists, and
        // the tables the specs name must be among them.
        await using var connection = await app.OpenDbAsync();
        var tables = new List<string>();
        await using (var command = new Npgsql.NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory' ORDER BY 1",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));

        string[] named =
        [
            "Tenants", "ApiKeys", "UnitsOfMeasure", // spec 001
            "Articles", // spec 002
            "Partners", "Warehouses", // spec 004
            "StockDocuments", "StockDocumentLines", "StockLedgerEntries", "DocumentCounters", // spec 005
            "ArticleUnits", // spec 007
            "StockBalances", // spec 011
        ];
        foreach (var table in named)
            Assert.Contains(table, tables);

        foreach (var table in tables.Where(t => t != "Tenants"))
        {
            var nullable = await app.ScalarAsync<string>(
                "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = 'TenantId'",
                ("t", table));
            Assert.True(nullable == "NO", $"Table {table} has no non-null TenantId column.");

            var referencesTenants = await app.ScalarAsync<long>(
                """
                SELECT count(*) FROM pg_constraint c
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
                WHERE c.contype = 'f' AND c.conrelid = ('public."' || @t || '"')::regclass
                  AND c.confrelid = 'public."Tenants"'::regclass AND array_length(c.conkey, 1) = 1 AND a.attname = 'TenantId'
                """,
                ("t", table));
            Assert.True(referencesTenants == 1, $"Table {table} has no foreign key from TenantId to Tenants.");
        }
    }

    [Theory]
    [InlineData("/articles")]
    [InlineData("/partners")]
    [InlineData("/warehouses")]
    [InlineData("/units-of-measure")]
    public async Task AC07_Old_root_routes_are_gone(string path)
    {
        var tenant = await app.NewTenantAsync();

        using var anonymous = await app.Anonymous().GetAsync(path);
        using var authenticated = await tenant.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, authenticated.StatusCode);
    }
}
