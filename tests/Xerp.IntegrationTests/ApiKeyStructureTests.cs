using System.Net;
using Npgsql;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 003, builder's tests below the public surface: the provenance columns of section 3, rows changed
/// outside the application (R6) and the single unfiltered query (T5).
/// </summary>
[Collection(XerpCollection.Name)]
public class ApiKeyStructureTests(XerpFixture app)
{
    [Theory]
    [InlineData("CreatedBy")]
    [InlineData("RevokedBy")]
    public async Task S3_Provenance_of_a_key_cannot_point_at_a_key_of_another_tenant(string column)
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteSqlAsync(
            $"""UPDATE "ApiKeys" SET "{column}" = @other WHERE "Id" = @id""", ("other", a.ApiKeyId), ("id", b.ApiKeyId)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        Assert.Equal($"FK_ApiKeys_ApiKeys_TenantId_{column}", failure.ConstraintName);
    }

    [Fact]
    public async Task S3_Created_and_revoked_keys_store_their_provenance()
    {
        var tenant = await app.NewTenantAsync();
        var key = await Keys.CreateAsync(app, tenant.Client);
        using var revoke = await Keys.RevokeAsync(tenant.Client, key.Id);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var stored = await app.ScalarAsync<long>(
            """
            SELECT count(*) FROM "ApiKeys"
            WHERE "Id" = @id AND "CreatedBy" = @actor AND "RevokedBy" = @actor AND "RevokedAt" IS NOT NULL AND NOT "IsActive"
            """, ("id", key.Id), ("actor", tenant.ApiKeyId));
        var initial = await app.ScalarAsync<long>(
            """SELECT count(*) FROM "ApiKeys" WHERE "Id" = @id AND "CreatedBy" IS NULL AND "RevokedAt" IS NULL AND "IsActive" """,
            ("id", tenant.ApiKeyId));

        Assert.Equal(1, stored);
        Assert.Equal(1, initial);
    }

    [Fact]
    public async Task R6_Revoking_a_key_deactivated_outside_the_application_succeeds_and_invents_no_revocation()
    {
        var tenant = await app.NewTenantAsync();
        var key = await Keys.CreateAsync(app, tenant.Client);
        await app.ExecuteSqlAsync("""UPDATE "ApiKeys" SET "IsActive" = false WHERE "Id" = @id""", ("id", key.Id));

        using var response = await Keys.RevokeAsync(tenant.Client, key.Id);

        var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("revokedAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("revokedBy").ValueKind);
    }

    [Fact]
    public void T5_The_authentication_lookup_is_the_only_query_that_bypasses_the_tenant_filter()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Xerp.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        var files = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadLines(f).Any(line => line.Contains("IgnoreQueryFilters(") && !line.TrimStart().StartsWith("//")))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Equal(["ApiKeyLookup.cs"], files);
    }
}
