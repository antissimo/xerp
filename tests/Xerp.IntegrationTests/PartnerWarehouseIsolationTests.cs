using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 004, AC-10, AC-11 (the routes need a tenant key) and AC-130 to AC-138 (tenant isolation over HTTP and
/// through tools): for tenant B, the partners and warehouses of tenant A do not exist.
/// </summary>
[Collection(XerpCollection.Name)]
public class PartnerWarehouseIsolationTests(XerpFixture app)
{
    private static readonly MasterApi P = MasterApi.Partners;
    private static readonly MasterApi W = MasterApi.Warehouses;

    // ---- authentication ----

    public static TheoryData<string, string> ProtectedRequests() => new()
    {
        { "GET", "/api/v1/partners" },
        { "POST", "/api/v1/partners" },
        { "GET", "/api/v1/partners/0199c0de-0000-7000-8000-000000000001" },
        { "GET", "/api/v1/warehouses" },
        { "POST", "/api/v1/warehouses" },
        { "GET", "/api/v1/warehouses/0199c0de-0000-7000-8000-000000000001" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = HttpAssert.Raw("""{ "code": "X1", "name": "Name", "isCustomer": true }""");
        return request;
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task AC10_Without_a_credential_or_with_an_unknown_key_is_unauthenticated(string method, string path)
    {
        using var anonymous = app.Anonymous();
        using var unknown = app.WithBearer(XerpFixture.NewKeyString());

        using var noHeader = await anonymous.SendAsync(Request(method, path));
        using var unknownKey = await unknown.SendAsync(Request(method, path));

        await HttpAssert.UnauthenticatedAsync(noHeader);
        await HttpAssert.UnauthenticatedAsync(unknownKey);
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task AC11_The_admin_key_is_forbidden(string method, string path)
    {
        using var admin = app.Admin();

        using var response = await admin.SendAsync(Request(method, path));

        await HttpAssert.ForbiddenAsync(response);
    }

    // ---- isolation ----

    private sealed record Side(TestTenant Tenant, McpConnection? Connection) : IAsyncDisposable
    {
        public HttpClient Http => Tenant.Client;

        public McpConnection Mcp => Connection ?? throw new InvalidOperationException("This side has no MCP client.");

        public ValueTask DisposeAsync() => Connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>A new tenant with its key; with its own MCP client only for the criteria that use tools.</summary>
    private async Task<Side> SideAsync(bool mcp = false)
    {
        var tenant = await app.NewTenantAsync();
        return new Side(tenant, mcp ? await app.McpAsync(tenant.Key) : null);
    }

    private sealed record Owned(JsonElement Partner, JsonElement Warehouse);

    /// <summary>A's records of AC-130: partner X1 (tax id T-777, customer) and warehouse W1.</summary>
    private static async Task<Owned> SeedAsync(Side a) => new(
        await P.CreateAsync(a.Http, P.Full("X1").With("taxId", "T-777").With("isSupplier", false)),
        await W.CreateAsync(a.Http, W.Full("W1")));

    private static async Task AssertUnchangedAsync(Side a, Owned owned)
    {
        await P.AssertUnchangedAsync(a.Http, owned.Partner);
        await W.AssertUnchangedAsync(a.Http, owned.Warehouse);
        Assert.Equal(["X1"], (await P.ListAsync(a.Http)).Codes());
        Assert.Equal(["W1"], (await W.ListAsync(a.Http)).Codes());
    }

    private static void AssertEmpty(JsonElement list, string what)
    {
        Assert.True(list.GetProperty("items").GetArrayLength() == 0 && list.Total() == 0, $"{what} is not empty: {list}");
    }

    [Fact]
    public async Task AC130_Another_tenants_records_are_not_listed_counted_or_found_by_search()
    {
        await using var a = await SideAsync();
        await using var b = await SideAsync();
        await SeedAsync(a);

        foreach (var query in new[] { "", "?search=T-777", "?search=X1", "?isCustomer=true", "?isActive=true", "?search=acme" })
            AssertEmpty(await P.ListAsync(b.Http, query), $"B's GET /partners{query}");
        foreach (var query in new[] { "", "?search=W1", "?isActive=true", "?search=main" })
            AssertEmpty(await W.ListAsync(b.Http, query), $"B's GET /warehouses{query}");
        // The same queries do find the records for A, so the emptiness above is isolation and not a broken filter.
        Assert.Equal(["X1"], (await P.ListAsync(a.Http, "?search=T-777")).Codes());
        Assert.Equal(["W1"], (await W.ListAsync(a.Http, "?search=W1")).Codes());
    }

    [Fact]
    public async Task AC131_Another_tenants_records_are_not_found_by_id_or_code()
    {
        await using var a = await SideAsync();
        await using var b = await SideAsync();
        var owned = await SeedAsync(a);

        using var partnerById = await b.Http.GetAsync($"{P.Path}/{owned.Partner.Id()}");
        using var partnerByCode = await P.ByCodeAsync(b.Http, "X1");
        using var warehouseById = await b.Http.GetAsync($"{W.Path}/{owned.Warehouse.Id()}");
        using var warehouseByCode = await W.ByCodeAsync(b.Http, "W1");

        await HttpAssert.NotFoundAsync(partnerById);
        await HttpAssert.NotFoundAsync(partnerByCode);
        await HttpAssert.NotFoundAsync(warehouseById);
        await HttpAssert.NotFoundAsync(warehouseByCode);
    }

    [Fact]
    public async Task AC132_Another_tenants_records_cannot_be_replaced_or_deleted()
    {
        await using var a = await SideAsync();
        await using var b = await SideAsync();
        var owned = await SeedAsync(a);

        using var putPartner = await P.PutAsync(b.Http, owned.Partner.Id(), P.Full("HIJACK").With("name", "Taken over"));
        using var deletePartner = await P.DeleteAsync(b.Http, owned.Partner.Id());
        using var putWarehouse = await W.PutAsync(b.Http, owned.Warehouse.Id(), W.Full("HIJACK").With("name", "Taken over"));
        using var deleteWarehouse = await W.DeleteAsync(b.Http, owned.Warehouse.Id());

        await HttpAssert.NotFoundAsync(putPartner);
        await HttpAssert.NotFoundAsync(deletePartner);
        await HttpAssert.NotFoundAsync(putWarehouse);
        await HttpAssert.NotFoundAsync(deleteWarehouse);
        await AssertUnchangedAsync(a, owned);
        AssertEmpty(await P.ListAsync(b.Http), "B's partner list");
        AssertEmpty(await W.ListAsync(b.Http), "B's warehouse list");
    }

    [Fact]
    public async Task AC133_Codes_are_unique_per_tenant_not_across_tenants()
    {
        await using var a = await SideAsync();
        await using var b = await SideAsync();
        var owned = await SeedAsync(a);

        var partnerB = await P.CreateAsync(b.Http, "X1", "B's partner");
        var warehouseB = await W.CreateAsync(b.Http, "W1", "B's warehouse");

        Assert.NotEqual(owned.Partner.Id(), partnerB.Id());
        Assert.NotEqual(owned.Warehouse.Id(), warehouseB.Id());
        foreach (var (side, partnerId, warehouseId) in new[]
                 {
                     (a, owned.Partner.Id(), owned.Warehouse.Id()), (b, partnerB.Id(), warehouseB.Id()),
                 })
        {
            using var partner = await P.ByCodeAsync(side.Http, "X1");
            using var warehouse = await W.ByCodeAsync(side.Http, "W1");
            Assert.Equal(partnerId, (await HttpAssert.JsonAsync(partner, HttpStatusCode.OK)).Id());
            Assert.Equal(warehouseId, (await HttpAssert.JsonAsync(warehouse, HttpStatusCode.OK)).Id());
        }
        await AssertUnchangedAsync(a, owned);
    }

    [Fact]
    public async Task AC133_Replacing_into_another_tenants_code_is_not_a_conflict()
    {
        // T2: no CODE_TAKEN across tenants, on replace as on create.
        await using var a = await SideAsync();
        await using var b = await SideAsync();
        var owned = await SeedAsync(a);
        var partnerB = await P.CreateAsync(b.Http, "B-1");

        var renamed = await P.ReplaceAsync(b.Http, partnerB.Id(), P.Full("X1"));

        Assert.Equal("X1", renamed.Str("code"));
        await AssertUnchangedAsync(a, owned);
    }

    [Fact]
    public async Task AC134_TenantId_in_a_body_or_query_string_is_rejected_and_selects_nothing()
    {
        await using var a = await SideAsync();
        await using var b = await SideAsync();
        var owned = await SeedAsync(a);

        using var post = await P.PostAsync(b.Http, P.Minimal("Y1").With("tenantId", a.Tenant.Id.ToString()));
        using var list = await b.Http.GetAsync($"{P.Path}?tenantId={a.Tenant.Id}");
        using var postWarehouse = await W.PostAsync(b.Http, W.Minimal("Y1").With("tenantId", a.Tenant.Id.ToString()));

        await HttpAssert.ValidationAsync(post);
        var problem = await HttpAssert.ValidationAsync(list, "tenantId");
        await HttpAssert.ValidationAsync(postWarehouse);
        Assert.DoesNotContain("T-777", problem.ToString());
        Assert.DoesNotContain(owned.Partner.Id().ToString(), problem.ToString());
        AssertEmpty(await P.ListAsync(b.Http), "B's partner list");
        AssertEmpty(await W.ListAsync(b.Http), "B's warehouse list");
        await AssertUnchangedAsync(a, owned);
    }

    [Fact]
    public async Task AC135_Through_tools_another_tenants_records_do_not_exist()
    {
        await using var a = await SideAsync(mcp: true);
        await using var b = await SideAsync(mcp: true);
        var owned = await SeedAsync(a);
        var partnerId = owned.Partner.Id();
        var warehouseId = owned.Warehouse.Id();

        AssertEmpty(await b.Mcp.OkAsync("partner_list", new { }), "B's partner_list");
        AssertEmpty(await b.Mcp.OkAsync("partner_list", new { search = "T-777" }), "B's partner_list for A's tax id");
        AssertEmpty(await b.Mcp.OkAsync("partner_list", new { isCustomer = true }), "B's partner_list of customers");
        AssertEmpty(await b.Mcp.OkAsync("warehouse_list", new { }), "B's warehouse_list");
        AssertEmpty(await b.Mcp.OkAsync("warehouse_list", new { search = "W1" }), "B's warehouse_list for A's code");
        await b.Mcp.ErrorAsync("partner_get", new { id = partnerId }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("partner_get", new { code = "X1" }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("warehouse_get", new { id = warehouseId }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("warehouse_get", new { code = "W1" }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("partner_update", P.Full("HIJACK").WithId(partnerId), "NOT_FOUND");
        await b.Mcp.ErrorAsync("partner_delete", new { id = partnerId }, "NOT_FOUND");
        await b.Mcp.ErrorAsync("warehouse_update", W.Full("HIJACK").WithId(warehouseId), "NOT_FOUND");
        await b.Mcp.ErrorAsync("warehouse_delete", new { id = warehouseId }, "NOT_FOUND");

        await AssertUnchangedAsync(a, owned);
        McpAssert.JsonEqual(owned.Partner, await a.Mcp.OkAsync("partner_get", new { id = partnerId }));
        McpAssert.JsonEqual(owned.Warehouse, await a.Mcp.OkAsync("warehouse_get", new { id = warehouseId }));
    }

    [Fact]
    public async Task AC136_Through_tools_codes_are_per_tenant_and_tenantId_is_not_an_argument()
    {
        await using var a = await SideAsync(mcp: true);
        await using var b = await SideAsync(mcp: true);
        var owned = await SeedAsync(a);

        var partnerB = await b.Mcp.OkAsync("partner_create", new { code = "X1", name = "B's partner", isCustomer = true });
        var warehouseB = await b.Mcp.OkAsync("warehouse_create", new { code = "W1", name = "B's warehouse" });
        McpAssert.ValidationWithErrors(await b.Mcp.CallAsync("partner_create",
            new { code = "Y1", name = "Smuggled", isCustomer = true, tenantId = a.Tenant.Id }));

        Assert.NotEqual(owned.Partner.Id(), partnerB.Id());
        Assert.NotEqual(owned.Warehouse.Id(), warehouseB.Id());
        Assert.Equal(["X1"], (await P.ListAsync(b.Http)).Codes());
        Assert.Equal(["W1"], (await W.ListAsync(b.Http)).Codes());
        await AssertUnchangedAsync(a, owned);
    }

    [Fact]
    public async Task AC137_A_partner_created_through_a_tool_belongs_to_the_tenant_of_the_mcp_key()
    {
        await using var a = await SideAsync(mcp: true);
        await using var b = await SideAsync(mcp: true);

        var created = await b.Mcp.OkAsync("partner_create", new { code = "B-ONLY", name = "B's partner", isSupplier = true });

        Assert.Equal(["B-ONLY"], (await P.ListAsync(b.Http)).Codes());
        McpAssert.JsonEqual(created, await P.GetAsync(b.Http, created.Id()));
        AssertEmpty(await P.ListAsync(a.Http), "A's GET /partners");
        AssertEmpty(await a.Mcp.OkAsync("partner_list", new { }), "A's partner_list");
        using var getByA = await a.Http.GetAsync($"{P.Path}/{created.Id()}");
        await HttpAssert.NotFoundAsync(getByA);
    }

    [Fact]
    public async Task AC138_Interleaved_parallel_calls_of_two_tenants_never_mix_data()
    {
        await using var a = await SideAsync(mcp: true);
        await using var b = await SideAsync(mcp: true);

        // Per tenant: 10 partner_create and 10 partner_list, all 40 calls in flight together.
        async Task<string[][]> RunAsync(McpConnection mcp, string prefix)
        {
            var creates = Enumerable.Range(0, 10)
                .Select(i => mcp.OkAsync("partner_create",
                    new { code = $"{prefix}-{i}", name = $"Partner {prefix} {i}", isCustomer = true, taxId = "SHARED-1" }))
                .ToArray();
            var lists = Enumerable.Range(0, 10).Select(_ => mcp.OkAsync("partner_list", new { search = "SHARED-1" })).ToArray();
            await Task.WhenAll(creates);
            return (await Task.WhenAll(lists)).Select(list => list.Codes()).ToArray();
        }

        var runA = RunAsync(a.Mcp, "a");
        var runB = RunAsync(b.Mcp, "b");
        var listsA = await runA;
        var listsB = await runB;

        Assert.All(listsA, codes => Assert.All(codes, code => Assert.StartsWith("a-", code)));
        Assert.All(listsB, codes => Assert.All(codes, code => Assert.StartsWith("b-", code)));
        var finalA = await a.Mcp.OkAsync("partner_list", new { });
        var finalB = await b.Mcp.OkAsync("partner_list", new { });
        Assert.Equal(10, finalA.Total());
        Assert.Equal(10, finalB.Total());
        Assert.All(finalA.Codes(), code => Assert.StartsWith("a-", code));
        Assert.All(finalB.Codes(), code => Assert.StartsWith("b-", code));
    }
}
