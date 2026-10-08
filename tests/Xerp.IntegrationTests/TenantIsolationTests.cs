using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Infrastructure.Persistence;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

[Collection(XerpCollection.Name)]
public class TenantIsolationTests(XerpFixture app)
{
    private sealed record FixedTenant(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private XerpDbContext NewDbContext(Guid? tenantId, Guid? apiKeyId = null) =>
        new(new DbContextOptionsBuilder<XerpDbContext>().UseNpgsql(app.ConnectionString).Options,
            new FixedTenant(tenantId, apiKeyId));

    [Fact]
    public async Task AC60_Other_tenants_units_are_not_listed_or_counted()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        await Uom.CreateAsync(a.Client, "kg", "Kilogram");

        var list = await Uom.ListAsync(b.Client);

        Assert.Empty(list.Codes());
        Assert.Equal(0, list.Total());
        Assert.Equal(1, (await Uom.ListAsync(a.Client)).Total());
    }

    [Fact]
    public async Task AC61_Other_tenants_unit_is_not_found_by_id_or_code()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(a.Client, "kg", "Kilogram");

        using var byId = await b.Client.GetAsync($"{Uom.Path}/{unit.Id()}");
        using var byCode = await b.Client.GetAsync($"{Uom.Path}/by-code/kg");

        await HttpAssert.NotFoundAsync(byId);
        await HttpAssert.NotFoundAsync(byCode);
    }

    [Fact]
    public async Task AC62_Other_tenants_unit_cannot_be_replaced()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(a.Client, "kg", "Kilogram");

        using var put = await b.Client.PutAsJsonAsync($"{Uom.Path}/{unit.Id()}", new { code = "hacked", name = "Hacked", isActive = false });

        await HttpAssert.NotFoundAsync(put);
        Assert.Equal(unit.ToString(), (await Uom.GetAsync(a.Client, unit.Id())).ToString());
    }

    [Fact]
    public async Task AC63_Other_tenants_unit_cannot_be_deleted()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(a.Client, "kg", "Kilogram");

        using var delete = await b.Client.DeleteAsync($"{Uom.Path}/{unit.Id()}");

        await HttpAssert.NotFoundAsync(delete);
        Assert.Equal(unit.ToString(), (await Uom.GetAsync(a.Client, unit.Id())).ToString());
    }

    [Fact]
    public async Task AC64_Same_code_can_exist_in_two_tenants()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unitA = await Uom.CreateAsync(a.Client, "kg", "Kilogram A");

        var unitB = await Uom.CreateAsync(b.Client, "kg", "Kilogram B");

        Assert.NotEqual(unitA.Id(), unitB.Id());
        using var byCodeA = await a.Client.GetAsync($"{Uom.Path}/by-code/kg");
        using var byCodeB = await b.Client.GetAsync($"{Uom.Path}/by-code/kg");
        Assert.Equal(unitA.ToString(), (await HttpAssert.JsonAsync(byCodeA, HttpStatusCode.OK)).ToString());
        Assert.Equal(unitB.ToString(), (await HttpAssert.JsonAsync(byCodeB, HttpStatusCode.OK)).ToString());

        // A's unit does not block B from renaming one of its own units either.
        await Uom.CreateAsync(a.Client, "only-a", "Only in A");
        var other = await Uom.CreateAsync(b.Client, "tmp", "Temporary");
        using var rename = await b.Client.PutAsJsonAsync($"{Uom.Path}/{other.Id()}", new { code = "ONLY-A", name = "Mine", isActive = true });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [Fact]
    public async Task AC65_Search_and_paging_never_cross_tenants()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        foreach (var code in new[] { "ka", "kb", "kc", "kd" })
            await Uom.CreateAsync(a.Client, code, $"A {code}");
        await Uom.CreateAsync(b.Client, "kz", "B kz");
        await Uom.CreateAsync(b.Client, "m", "B metre");

        var search = await Uom.ListAsync(b.Client, "?search=k");
        var pageOne = await Uom.ListAsync(b.Client, "?limit=1");
        var pageTwo = await Uom.ListAsync(b.Client, "?limit=1&offset=1");
        var pageThree = await Uom.ListAsync(b.Client, "?limit=1&offset=2");
        var searchA = await Uom.ListAsync(b.Client, "?search=A%20k");

        Assert.Equal(["kz"], search.Codes());
        Assert.Equal(1, search.Total());
        Assert.Equal(["kz"], pageOne.Codes());
        Assert.Equal(2, pageOne.Total());
        Assert.Equal(["m"], pageTwo.Codes());
        Assert.Empty(pageThree.Codes());
        Assert.Equal(2, pageThree.Total());
        Assert.Equal(0, searchA.Total());
    }

    [Fact]
    public async Task AC66_Whoami_returns_each_keys_own_tenant()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();

        using var responseA = await a.Client.GetAsync("/api/v1/whoami");
        using var responseB = await b.Client.GetAsync("/api/v1/whoami");

        var meA = await HttpAssert.JsonAsync(responseA, HttpStatusCode.OK);
        var meB = await HttpAssert.JsonAsync(responseB, HttpStatusCode.OK);
        Assert.Equal(a.Id, meA.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(a.Code, meA.GetProperty("tenant").Str("code"));
        Assert.Equal(a.ApiKeyId, meA.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        Assert.Equal(b.Id, meB.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(b.Code, meB.GetProperty("tenant").Str("code"));
        Assert.Equal(b.ApiKeyId, meB.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
    }

    [Fact]
    public async Task T1_No_query_parameter_or_header_selects_a_tenant()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        await Uom.CreateAsync(a.Client, "kg", "Kilogram");
        using var client = app.WithBearer(b.Key);
        client.DefaultRequestHeaders.Add("X-Tenant", a.Id.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", a.Id.ToString());

        // Headers are ignored; a tenant in the query string is an unknown parameter (R15).
        var list = await Uom.ListAsync(client);
        using var whoami = await client.GetAsync("/api/v1/whoami");
        Assert.Equal(0, list.Total());
        Assert.Equal(b.Id, (await HttpAssert.JsonAsync(whoami, HttpStatusCode.OK)).GetProperty("tenant").GetProperty("id").GetGuid());

        foreach (var name in new[] { "tenantId", "tenant" })
        {
            using var listWithQuery = await client.GetAsync($"{Uom.Path}?{name}={a.Id}");
            using var whoamiWithQuery = await client.GetAsync($"/api/v1/whoami?{name}={a.Id}");
            await HttpAssert.ValidationAsync(listWithQuery, name);
            await HttpAssert.ValidationAsync(whoamiWithQuery, name);
        }
    }

    [Fact]
    public async Task AC67_DbContext_refuses_to_insert_a_row_of_another_tenant()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var unit = UnitOfMeasure.Create("smuggled", "Smuggled", true, DateTime.UtcNow, b.ApiKeyId);

        await using var db = NewDbContext(b.Id, b.ApiKeyId);
        db.UnitsOfMeasure.Add(unit);
        db.Entry(unit).Property(u => u.TenantId).CurrentValue = a.Id;

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        var rows = await app.ScalarAsync<long>("""SELECT count(*) FROM "UnitsOfMeasure" WHERE "Id" = @id""", ("id", unit.Id));
        Assert.Equal(0, rows);
        Assert.Equal(0, (await Uom.ListAsync(a.Client)).Total());
        Assert.Equal(0, (await Uom.ListAsync(b.Client)).Total());
    }

    [Fact]
    public async Task AC67_DbContext_stamps_the_current_tenant_on_insert()
    {
        var b = await app.NewTenantAsync();
        var unit = UnitOfMeasure.Create("stamped", "Stamped", true, DateTime.UtcNow, b.ApiKeyId);

        await using (var db = NewDbContext(b.Id, b.ApiKeyId))
        {
            db.UnitsOfMeasure.Add(unit);
            await db.SaveChangesAsync();
        }

        var stored = await app.ScalarAsync<Guid>("""SELECT "TenantId" FROM "UnitsOfMeasure" WHERE "Id" = @id""", ("id", unit.Id));
        Assert.Equal(b.Id, stored);
        Assert.Equal(["stamped"], (await Uom.ListAsync(b.Client)).Codes());
    }

    [Fact]
    public async Task AC67_DbContext_refuses_to_update_or_delete_a_row_of_another_tenant()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        var created = await Uom.CreateAsync(a.Client, "kg", "Kilogram");

        // The row is loaded legitimately as tenant A, then attached to a context whose tenant is B.
        UnitOfMeasure unit;
        await using (var dbA = NewDbContext(a.Id, a.ApiKeyId))
            unit = await dbA.UnitsOfMeasure.AsNoTracking().SingleAsync(u => u.Id == created.Id());

        await using (var dbB = NewDbContext(b.Id, b.ApiKeyId))
        {
            dbB.Attach(unit);
            unit.Replace("stolen", "Stolen", false, DateTime.UtcNow, b.ApiKeyId);
            await Assert.ThrowsAnyAsync<Exception>(() => dbB.SaveChangesAsync());
        }

        await using (var dbB = NewDbContext(b.Id, b.ApiKeyId))
        {
            var again = await NewDbContextRow(a, created.Id());
            dbB.Remove(again);
            await Assert.ThrowsAnyAsync<Exception>(() => dbB.SaveChangesAsync());
        }

        Assert.Equal(created.ToString(), (await Uom.GetAsync(a.Client, created.Id())).ToString());
    }

    private async Task<UnitOfMeasure> NewDbContextRow(TestTenant tenant, Guid id)
    {
        await using var db = NewDbContext(tenant.Id, tenant.ApiKeyId);
        return await db.UnitsOfMeasure.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    [Fact]
    public async Task AC67_DbContext_without_a_tenant_sees_nothing_and_writes_nothing()
    {
        var a = await app.NewTenantAsync();
        await Uom.CreateAsync(a.Client, "kg", "Kilogram");

        await using var db = NewDbContext(tenantId: null);

        Assert.Empty(await db.UnitsOfMeasure.ToListAsync());
        Assert.Empty(await db.ApiKeys.ToListAsync());
        var unit = UnitOfMeasure.Create("orphan", "Orphan", true, DateTime.UtcNow, a.ApiKeyId);
        db.UnitsOfMeasure.Add(unit);
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        var rows = await app.ScalarAsync<long>("""SELECT count(*) FROM "UnitsOfMeasure" WHERE "Id" = @id""", ("id", unit.Id));
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task T2_DbContext_filters_every_tenant_owned_set_to_the_current_tenant()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        await Uom.CreateAsync(a.Client, "kg", "Kilogram");
        await Uom.CreateAsync(b.Client, "m", "Metre");

        await using var db = NewDbContext(b.Id, b.ApiKeyId);

        Assert.Equal(["m"], await db.UnitsOfMeasure.Select(u => u.Code).ToListAsync());
        Assert.Equal([b.ApiKeyId], await db.ApiKeys.Select(k => k.Id).ToListAsync());
    }

    [Fact]
    public async Task T2_Database_rejects_duplicate_codes_and_dangling_tenants_on_its_own()
    {
        var a = await app.NewTenantAsync();
        await Uom.CreateAsync(a.Client, "kg", "Kilogram");
        const string insert =
            """
            INSERT INTO "UnitsOfMeasure" ("Id", "TenantId", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy")
            VALUES (@id, @tenant, @code, 'Raw', true, now(), now(), @key, @key)
            """;

        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ExecuteSqlAsync(insert,
            ("id", Guid.CreateVersion7()), ("tenant", a.Id), ("code", "KG"), ("key", a.ApiKeyId)));
        var dangling = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ExecuteSqlAsync(insert,
            ("id", Guid.CreateVersion7()), ("tenant", Guid.CreateVersion7()), ("code", "x"), ("key", a.ApiKeyId)));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, dangling.SqlState);
    }
}
