using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The real API in-process against a throwaway PostgreSQL 18 started by Testcontainers (ADR-0006).
/// One database container is shared by all tests; tests isolate themselves by creating their own tenants.
/// </summary>
public sealed class XerpFixture : IAsyncLifetime
{
    public const string AdminKey = "test-admin-key-0123456789-abcdefghijklmnop";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:18")
        .WithDatabase("xerp_test")
        .WithUsername("xerp_test")
        .WithPassword("xerp_test")
        .Build();

    private readonly List<XerpFactory> _extraFactories = [];

    public XerpFactory Factory { get; private set; } = null!;
    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new XerpFactory(ConnectionString, AdminKey);
        // Starts the host, which applies the real migrations.
        using var response = await Factory.CreateClient().GetAsync("/health");
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _extraFactories)
            await factory.DisposeAsync();
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A second API host on the same database with another admin key setting (null = not configured).</summary>
    public XerpFactory CreateHost(string? adminKey)
    {
        var factory = new XerpFactory(ConnectionString, adminKey);
        _extraFactories.Add(factory);
        return factory;
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    public HttpClient WithBearer(string token) => Bearer(Factory.CreateClient(), token);

    public HttpClient Admin() => WithBearer(AdminKey);

    public static HttpClient Bearer(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static string UniqueCode(string prefix = "t") => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Creates a new tenant through the admin endpoint (spec 001, 4.1) and returns its first key.</summary>
    public async Task<TestTenant> NewTenantAsync(string? name = null)
    {
        var code = UniqueCode();
        name ??= $"Tenant {code}";
        using var admin = Admin();
        using var response = await admin.PostAsJsonAsync("/api/v1/admin/tenants", new { code, name });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created,
            $"Creating a tenant failed: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        var key = json.RootElement.GetProperty("apiKey").GetProperty("key").GetString()!;
        return new TestTenant(
            json.RootElement.GetProperty("tenant").GetProperty("id").GetGuid(),
            code,
            name,
            json.RootElement.GetProperty("apiKey").GetProperty("id").GetGuid(),
            key,
            WithBearer(key));
    }

    public async Task<NpgsqlConnection> OpenDbAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<int> ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenDbAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenDbAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>A key string in the documented format that the server has never issued.</summary>
    public static string NewKeyString() =>
        "xerp_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Inserts a second API key for a tenant directly in the database (spec 001, AC-46).</summary>
    public async Task<(Guid Id, string Key)> InsertApiKeyAsync(Guid tenantId, string name = "second", string actorType = "agent")
    {
        var id = Guid.CreateVersion7();
        var key = NewKeyString();
        await ExecuteSqlAsync(
            """
            INSERT INTO "ApiKeys" ("Id", "TenantId", "Name", "ActorType", "KeyHash", "IsActive", "CreatedAt")
            VALUES (@id, @tenant, @name, @actorType, @hash, true, now())
            """,
            ("id", id), ("tenant", tenantId), ("name", name), ("actorType", actorType), ("hash", Sha256Hex(key)));
        return (id, key);
    }
}

public sealed record TestTenant(Guid Id, string Code, string Name, Guid ApiKeyId, string Key, HttpClient Client);

public sealed class XerpFactory(string connectionString, string? adminKey) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        // An explicit empty value keeps an admin key from the environment of the test run out of the host.
        builder.UseSetting("Xerp:AdminKey", adminKey ?? "");
    }
}

[CollectionDefinition(Name)]
public sealed class XerpCollection : ICollectionFixture<XerpFixture>
{
    public const string Name = "xerp";
}
