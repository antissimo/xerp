using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002: tenant isolation of articles and of references (AC-90 … AC-97).</summary>
[Collection(XerpCollection.Name)]
public class ArticleTenantIsolationTests(XerpFixture app)
{
    private sealed record FixedTenant(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private XerpDbContext NewDbContext(TestTenant tenant) =>
        new(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(app.ConnectionString).Options,
            new FixedTenant(tenant.Id, tenant.ApiKeyId));

    private async Task<(TestTenant A, Guid UnitA, JsonElement ArticleA, TestTenant B, Guid UnitB)> TwoTenantsAsync()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unitA = await Art.UnitAsync(a.Client, "pcs", "Piece A");
        var unitB = await Art.UnitAsync(b.Client, "pcs", "Piece B");
        var articleA = await Art.CreateAsync(a.Client, "X1", "Article of A", unitA);
        return (a, unitA, articleA, b, unitB);
    }

    [Fact]
    public async Task AC90_Other_tenants_articles_are_not_listed_counted_or_found_by_any_filter()
    {
        var (a, unitA, _, b, _) = await TwoTenantsAsync();

        Assert.Equal("""{"items":[],"total":0,"limit":50,"offset":0}""", (await Art.ListAsync(b.Client)).GetRawText());
        Assert.Equal(0, (await Art.ListAsync(b.Client, "?search=X1")).Total());
        Assert.Equal(0, (await Art.ListAsync(b.Client, "?search=Article")).Total());
        Assert.Equal(0, (await Art.ListAsync(b.Client, "?type=stock")).Total());
        Assert.Equal(0, (await Art.ListAsync(b.Client, $"?baseUnitId={unitA}")).Total());
        Assert.Empty((await Art.ListAsync(b.Client, $"?baseUnitId={unitA}&limit=500")).Codes());
        Assert.Equal(1, (await Art.ListAsync(a.Client, $"?baseUnitId={unitA}")).Total());
    }

    [Fact]
    public async Task AC91_Other_tenants_article_is_not_found_by_id_or_code()
    {
        var (_, _, articleA, b, _) = await TwoTenantsAsync();

        using var byId = await b.Client.GetAsync($"{Art.Path}/{articleA.Id()}");
        using var byCode = await b.Client.GetAsync($"{Art.Path}/by-code/X1");

        await HttpAssert.NotFoundAsync(byId);
        await HttpAssert.NotFoundAsync(byCode);
    }

    [Fact]
    public async Task AC92_Other_tenants_article_cannot_be_replaced()
    {
        var (a, _, articleA, b, unitB) = await TwoTenantsAsync();

        using var put = await Art.PutAsync(b.Client, articleA.Id(), Art.ReplaceBody("hacked", "Hacked", unitB, isActive: false));

        await HttpAssert.NotFoundAsync(put);
        Assert.Equal(articleA.ToString(), (await Art.GetAsync(a.Client, articleA.Id())).ToString());
    }

    [Fact]
    public async Task AC93_Other_tenants_article_cannot_be_deleted()
    {
        var (a, _, articleA, b, _) = await TwoTenantsAsync();

        using var delete = await b.Client.DeleteAsync($"{Art.Path}/{articleA.Id()}");

        await HttpAssert.NotFoundAsync(delete);
        Assert.Equal(articleA.ToString(), (await Art.GetAsync(a.Client, articleA.Id())).ToString());
    }

    [Fact]
    public async Task AC94_Same_article_code_can_exist_in_two_tenants()
    {
        var (a, unitA, articleA, b, unitB) = await TwoTenantsAsync();

        var articleB = await Art.CreateAsync(b.Client, "X1", "Article of B", unitB);

        Assert.NotEqual(articleA.Id(), articleB.Id());
        using var byCodeA = await a.Client.GetAsync($"{Art.Path}/by-code/X1");
        using var byCodeB = await b.Client.GetAsync($"{Art.Path}/by-code/X1");
        var foundA = await HttpAssert.JsonAsync(byCodeA, HttpStatusCode.OK);
        var foundB = await HttpAssert.JsonAsync(byCodeB, HttpStatusCode.OK);
        Assert.Equal(articleA.ToString(), foundA.ToString());
        Assert.Equal(articleB.ToString(), foundB.ToString());
        Assert.Equal(unitA, foundA.BaseUnitId());
        Assert.Equal("Piece A", foundA.GetProperty("baseUnit").Str("name"));
        Assert.Equal(unitB, foundB.BaseUnitId());
        Assert.Equal("Piece B", foundB.GetProperty("baseUnit").Str("name"));
    }

    [Fact]
    public async Task AC95_Other_tenants_unit_as_reference_is_indistinguishable_from_a_random_id()
    {
        var (a, unitA, _, b, unitB) = await TwoTenantsAsync();
        var random = Guid.CreateVersion7();
        var own = await Art.CreateAsync(b.Client, "B1", "Article of B", unitB);

        using var postForeign = await Art.PostAsync(b.Client, "B2", "New", unitA);
        using var postRandom = await Art.PostAsync(b.Client, "B2", "New", random);
        using var putForeign = await Art.PutAsync(b.Client, own.Id(), Art.ReplaceBody("B1", "Changed", unitA));
        using var putRandom = await Art.PutAsync(b.Client, own.Id(), Art.ReplaceBody("B1", "Changed", random));

        var bodies = new[]
        {
            (await HttpAssert.ReferenceNotFoundAsync(postForeign, "baseUnitId")).GetRawText().Replace(unitA.ToString(), "<id>"),
            (await HttpAssert.ReferenceNotFoundAsync(postRandom, "baseUnitId")).GetRawText().Replace(random.ToString(), "<id>"),
            (await HttpAssert.ReferenceNotFoundAsync(putForeign, "baseUnitId")).GetRawText().Replace(unitA.ToString(), "<id>"),
            (await HttpAssert.ReferenceNotFoundAsync(putRandom, "baseUnitId")).GetRawText().Replace(random.ToString(), "<id>"),
        };
        Assert.Equal(bodies[1], bodies[0]);
        Assert.Equal(bodies[3], bodies[2]);
        Assert.All(bodies, body => Assert.DoesNotContain("Piece A", body));
        Assert.Equal(["B1"], (await Art.ListAsync(b.Client)).Codes());
        Assert.Equal(own.ToString(), (await Art.GetAsync(b.Client, own.Id())).ToString());
        Assert.Equal(1, (await Art.ListAsync(a.Client)).Total());
    }

    [Fact]
    public async Task AC96_Articles_of_one_tenant_never_make_another_tenants_unit_in_use()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var kgA = await Art.UnitAsync(a.Client, "kg", "Kilogram");
        var kgB = await Art.UnitAsync(b.Client, "kg", "Kilogram");
        await Art.CreateAsync(a.Client, "A1", "Sold by weight", kgA);

        using var deleteB = await b.Client.DeleteAsync($"{Uom.Path}/{kgB}");
        using var deleteAsB = await b.Client.DeleteAsync($"{Uom.Path}/{kgA}");
        using var deleteA = await a.Client.DeleteAsync($"{Uom.Path}/{kgA}");

        Assert.Equal(HttpStatusCode.NoContent, deleteB.StatusCode);
        await HttpAssert.NotFoundAsync(deleteAsB);
        await HttpAssert.InUseAsync(deleteA);
    }

    [Fact]
    public async Task AC97_DbContext_refuses_to_insert_an_article_of_another_tenant()
    {
        var (a, unitA, _, b, _) = await TwoTenantsAsync();
        var article = Article.Create("smuggled", "Smuggled", null, ArticleType.Stock, unitA, true, DateTime.UtcNow, a.ApiKeyId);

        await using var db = NewDbContext(b);
        db.Add(article);
        db.Entry(article).Property(x => x.TenantId).CurrentValue = a.Id;

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        var rows = await app.ScalarAsync<long>("""SELECT count(*) FROM "Articles" WHERE "Id" = @id""", ("id", article.Id));
        Assert.Equal(0, rows);
        Assert.Equal(1, (await Art.ListAsync(a.Client)).Total());
        Assert.Equal(0, (await Art.ListAsync(b.Client)).Total());
    }

    [Fact]
    public async Task AC97_DbContext_stamps_the_current_tenant_and_filters_articles_to_it()
    {
        var (a, _, articleA, b, unitB) = await TwoTenantsAsync();
        var article = Article.Create("stamped", "Stamped", null, ArticleType.Service, unitB, true, DateTime.UtcNow, b.ApiKeyId);

        await using (var db = NewDbContext(b))
        {
            db.Add(article);
            await db.SaveChangesAsync();
        }

        var stored = await app.ScalarAsync<Guid>("""SELECT "TenantId" FROM "Articles" WHERE "Id" = @id""", ("id", article.Id));
        Assert.Equal(b.Id, stored);
        await using var dbB = NewDbContext(b);
        Assert.Equal(["stamped"], await dbB.Set<Article>().Select(x => x.Code).ToListAsync());
        await using var dbA = NewDbContext(a);
        Assert.Equal([articleA.Id()], await dbA.Set<Article>().Select(x => x.Id).ToListAsync());
    }

    [Fact]
    public async Task T5_DbContext_cannot_store_an_article_pointing_at_another_tenants_unit()
    {
        var (a, unitA, _, b, _) = await TwoTenantsAsync();
        var article = Article.Create("cross", "Cross-tenant reference", null, ArticleType.Stock, unitA, true, DateTime.UtcNow, b.ApiKeyId);

        await using var db = NewDbContext(b);
        db.Add(article);

        // The Application check is bypassed on purpose; the database is the authority (ADR-0008, decision 6).
        await Assert.ThrowsAsync<ForeignKeyViolationException>(() => db.SaveChangesAsync());
        var rows = await app.ScalarAsync<long>("""SELECT count(*) FROM "Articles" WHERE "Id" = @id""", ("id", article.Id));
        Assert.Equal(0, rows);
        Assert.Equal(0, (await Art.ListAsync(b.Client)).Total());
        Assert.Equal(1, (await Art.ListAsync(a.Client)).Total());
    }
}
