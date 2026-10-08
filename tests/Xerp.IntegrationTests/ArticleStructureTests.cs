using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xerp.Domain.Catalog;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002: model and database rules for references (AC-04 … AC-06) and the error model (AC-98).</summary>
[Collection(XerpCollection.Name)]
public class ArticleStructureTests(XerpFixture app)
{
    private const string InsertArticle =
        """
        INSERT INTO "Articles" ("Id", "TenantId", "Code", "Name", "Description", "Type", "BaseUnitId", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy")
        VALUES (@id, @tenant, @code, 'Raw', NULL, 'stock', @unit, true, now(), now(), @key, @key)
        """;

    [Fact]
    public void AC04_Every_foreign_key_between_tenant_owned_entities_includes_TenantId_and_restricts_deletes()
    {
        using var scope = app.Factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<XerpDbContext>().Model;
        static bool TenantOwned(IReadOnlyEntityType type) => typeof(ITenantOwned).IsAssignableFrom(type.ClrType);

        var foreignKeys = model.GetEntityTypes()
            .SelectMany(type => type.GetForeignKeys())
            .Where(fk => TenantOwned(fk.DeclaringEntityType) && TenantOwned(fk.PrincipalEntityType))
            .ToList();

        Assert.Contains(foreignKeys, fk =>
            fk.DeclaringEntityType.ClrType == typeof(Article) &&
            fk.PrincipalEntityType.ClrType == typeof(UnitOfMeasure) &&
            fk.Properties.Any(p => p.Name == nameof(Article.BaseUnitId)));
        foreach (var fk in foreignKeys)
        {
            var name = $"{fk.DeclaringEntityType.ClrType.Name} -> {fk.PrincipalEntityType.ClrType.Name} ({string.Join(", ", fk.Properties.Select(p => p.Name))})";
            Assert.True(fk.Properties.Any(p => p.Name == nameof(ITenantOwned.TenantId)), $"{name}: dependent side lacks TenantId.");
            Assert.True(fk.PrincipalKey.Properties.Any(p => p.Name == nameof(ITenantOwned.TenantId)), $"{name}: principal key lacks TenantId.");
            // The same TenantId column on both sides, in the same position: the row and its target share a tenant.
            Assert.Equal(
                fk.Properties.ToList().FindIndex(p => p.Name == nameof(ITenantOwned.TenantId)),
                fk.PrincipalKey.Properties.ToList().FindIndex(p => p.Name == nameof(ITenantOwned.TenantId)));
            Assert.True(fk.DeleteBehavior is DeleteBehavior.Restrict or DeleteBehavior.NoAction, $"{name}: delete behaviour is {fk.DeleteBehavior}.");
        }

        // No foreign key anywhere in the model cascades or nulls (ADR-0008, decision 5).
        Assert.All(model.GetEntityTypes().SelectMany(type => type.GetForeignKeys()),
            fk => Assert.True(fk.DeleteBehavior is DeleteBehavior.Restrict or DeleteBehavior.NoAction));
    }

    [Fact]
    public async Task AC05_Database_refuses_an_article_pointing_at_a_unit_of_another_tenant()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unitA = await Art.UnitAsync(a.Client);
        var unitB = await Art.UnitAsync(b.Client);
        var id = Guid.CreateVersion7();

        var crossTenant = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ExecuteSqlAsync(InsertArticle,
            ("id", id), ("tenant", b.Id), ("code", "cross"), ("unit", unitA), ("key", b.ApiKeyId)));

        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);
        Assert.Equal(0, await app.ScalarAsync<long>("""SELECT count(*) FROM "Articles" WHERE "Id" = @id""", ("id", id)));
        // The same statement with the tenant's own unit is accepted, so it is the reference that was refused.
        Assert.Equal(1, await app.ExecuteSqlAsync(InsertArticle,
            ("id", id), ("tenant", b.Id), ("code", "own"), ("unit", unitB), ("key", b.ApiKeyId)));
    }

    [Fact]
    public async Task AC06_Database_refuses_to_delete_a_unit_that_an_article_references()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var article = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);

        var blocked = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            app.ExecuteSqlAsync("""DELETE FROM "UnitsOfMeasure" WHERE "Id" = @id""", ("id", unit)));

        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, blocked.SqlState);
        Assert.Equal(1, await app.ScalarAsync<long>("""SELECT count(*) FROM "UnitsOfMeasure" WHERE "Id" = @id""", ("id", unit)));
        Assert.Equal(1, await app.ScalarAsync<long>("""SELECT count(*) FROM "Articles" WHERE "Id" = @id""", ("id", article.Id())));
    }

    [Fact]
    public async Task T5_Database_rejects_duplicate_article_codes_on_its_own()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        await Art.CreateAsync(tenant.Client, "ART-1", "Bolt", unit);

        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ExecuteSqlAsync(InsertArticle,
            ("id", Guid.CreateVersion7()), ("tenant", tenant.Id), ("code", "art-1"), ("unit", unit), ("key", tenant.ApiKeyId)));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task AC98_Every_new_kind_of_error_is_a_problem_with_code_status_title_and_detail()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Art.UnitAsync(tenant.Client);
        var inactive = await Art.UnitAsync(tenant.Client, "old", "Obsolete", isActive: false);
        await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit);

        using var notFound = await Art.PostAsync(tenant.Client, "A2", "Nut", Guid.CreateVersion7());
        using var referenceInactive = await Art.PostAsync(tenant.Client, "A2", "Nut", inactive);
        using var inUse = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit}");
        using var validation = await Art.PostAsync(tenant.Client, "a b", "Bad", unit);

        // HttpAssert.ProblemAsync checks content type, code, status, title and detail for each of them.
        Assert.Equal("REFERENCE_NOT_FOUND", (await HttpAssert.ReferenceNotFoundAsync(notFound, "baseUnitId")).Str("title"));
        Assert.Equal("REFERENCE_INACTIVE", (await HttpAssert.ReferenceInactiveAsync(referenceInactive, "baseUnitId")).Str("title"));
        var inUseBody = await HttpAssert.ProblemAsync(inUse, HttpStatusCode.Conflict, "IN_USE");
        Assert.Equal("IN_USE", inUseBody.Str("title"));
        Assert.DoesNotContain("A1", inUseBody.GetRawText()); // S3: no data of the referrers
        Assert.DoesNotContain("Bolt", inUseBody.GetRawText());
        await HttpAssert.ValidationAsync(validation, "code");
    }
}
