using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-10, AC-11 and AC-20 to AC-28: a tenant is created with its default warehouse; at every moment a
/// tenant has exactly one default warehouse and it is active (R2) — it cannot be deactivated or deleted, another
/// active warehouse can be made the default, also when two calls race.
/// </summary>
[Collection(XerpCollection.Name)]
public class DefaultWarehouseTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    // Spec 004, 4.2 with spec 011, 4.1.
    private static readonly string[] Representation =
    [
        "addressLine1", "addressLine2", "city", "code", "countryCode", "createdAt", "createdBy", "id", "isActive",
        "isDefault", "name", "postalCode", "region", "updatedAt", "updatedBy",
    ];

    /// <summary>The replace body that keeps every value of the warehouse, with <c>isActive: false</c>.</summary>
    private static JsonObject Deactivated(JsonElement warehouse) => Stock.WarehouseBody(warehouse).With("isActive", false);

    /// <summary>R2: exactly one default warehouse, active; and it is the expected one.</summary>
    private static async Task<JsonElement> AssertDefaultIsAsync(HttpClient client, Guid expected)
    {
        var dw = await Balance.DefaultAsync(client);
        Assert.Equal(expected, dw.Id());
        var others = await W.ListAsync(client, "?isDefault=false&limit=500");
        Assert.All(others.Items(), w => Assert.False(w.Bool("isDefault"), $"A second default warehouse: {w}"));
        Assert.Equal((await W.ListAsync(client)).Total(), others.Total() + 1);
        return dw;
    }

    // ---- AC-10, AC-11 ----

    public static TheoryData<string, string> ProtectedRequests() => new()
    {
        { "POST", "/api/v1/warehouses/0199c0de-0000-7000-8000-000000000001/set-default" },
        { "GET", "/api/v1/warehouses/0199c0de-0000-7000-8000-000000000001/stock" },
        { "GET", Balance.Differences },
        { "POST", Balance.Rebuild },
    };

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task AC10_Without_a_credential_is_unauthenticated_and_the_admin_key_is_forbidden(string method, string path)
    {
        using var anonymous = app.Anonymous();
        using var admin = app.Admin();

        using var noCredential = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        using var adminKey = await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        await HttpAssert.UnauthenticatedAsync(noCredential);
        await HttpAssert.ForbiddenAsync(adminKey);
    }

    [Theory]
    [InlineData("/warehouses/{DW}/stock?foo=1", "foo")]
    [InlineData("/warehouses/{DW}/stock?hasStock=yes", "hasStock")]
    [InlineData("/warehouses/{DW}/stock?limit=0", "limit")]
    [InlineData("/warehouses/{DW}/stock?limit=501", "limit")]
    [InlineData("/warehouses/{DW}/stock?offset=-1", "offset")]
    [InlineData("/warehouses/{DW}/stock?isActive=maybe", "isActive")]
    [InlineData("/warehouses/{DW}/stock?warehouseId={DW}", "warehouseId")]
    [InlineData("/stock-balance-differences?warehouseId={DW}", "warehouseId")]
    [InlineData("/stock-balance-differences?limit=0", "limit")]
    [InlineData("/stock-balance-differences?tenantId={DW}", "tenantId")]
    [InlineData("/warehouses?isDefault=1", "isDefault")]
    [InlineData("/warehouses?isDefault=yes", "isDefault")]
    public async Task AC11_An_unknown_or_malformed_query_parameter_is_rejected_with_its_key(string path, string errorKey)
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultIdAsync(tenant.Client);

        using var response = await tenant.Client.GetAsync("/api/v1" + path.Replace("{DW}", dw.ToString()));

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task AC11_IsDefault_in_a_create_or_replace_body_is_an_unknown_property(string value)
    {
        // S2: the flag moves with set-default only; it is not an input of create or replace.
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultAsync(tenant.Client);
        var w1 = await W.CreateAsync(tenant.Client, "W1", "Shop");

        using var create = await W.PostAsync(tenant.Client, W.Minimal("W2").With("isDefault", JsonNode.Parse(value)));
        using var replace = await W.PutAsync(tenant.Client, w1.Id(), W.Full("W1").With("isDefault", JsonNode.Parse(value)));
        using var replaceDefault = await W.PutAsync(tenant.Client, dw.Id(), Stock.WarehouseBody(dw).With("isDefault", JsonNode.Parse(value)));

        await HttpAssert.ValidationAsync(create);
        await HttpAssert.ValidationAsync(replace);
        await HttpAssert.ValidationAsync(replaceDefault);
        Assert.Equal(["CENTRAL", "W1"], (await W.ListAsync(tenant.Client)).Codes());
        await W.AssertUnchangedAsync(tenant.Client, w1);
        await W.AssertUnchangedAsync(tenant.Client, dw);
    }

    [Fact]
    public async Task AC11_Set_default_and_rebuild_take_no_query_parameters()
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultAsync(tenant.Client);

        using var setDefault = await tenant.Client.PostAsync(Balance.SetDefaultPath(dw.Id()) + "?foo=1", null);
        using var rebuild = await tenant.Client.PostAsync(Balance.Rebuild + "?tenantId=" + tenant.Id, null);

        await HttpAssert.ValidationAsync(setDefault, "foo");
        await HttpAssert.ValidationAsync(rebuild, "tenantId");
    }

    // ---- AC-20 to AC-22 ----

    [Fact]
    public async Task AC20_A_new_tenant_has_exactly_one_warehouse_CENTRAL_which_is_its_default()
    {
        var tenant = await app.NewTenantAsync();

        var all = await W.ListAsync(tenant.Client);
        var defaults = await W.ListAsync(tenant.Client, "?isDefault=true");
        var others = await W.ListAsync(tenant.Client, "?isDefault=false");
        using var byCode = await W.ByCodeAsync(tenant.Client, "central");

        Assert.Equal(1, all.Total());
        var warehouse = Assert.Single(all.Items());
        Assert.Equal("CENTRAL", warehouse.Str("code"));
        Assert.Equal("Central warehouse", warehouse.Str("name"));
        Assert.True(warehouse.Bool("isDefault"));
        Assert.True(warehouse.Bool("isActive"));
        JsonBody.AssertNull(warehouse, MasterApi.AddressFields);
        Assert.Equal(tenant.ApiKeyId, warehouse.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, warehouse.GetProperty("updatedBy").GetGuid());
        Assert.Equal(Representation, warehouse.PropertyNames());
        Assert.Equal(1, defaults.Total());
        McpAssert.JsonEqual(warehouse, Assert.Single(defaults.Items()));
        Assert.Equal(0, others.Total());
        Assert.Empty(others.Items());
        McpAssert.JsonEqual(warehouse, await HttpAssert.JsonAsync(byCode, HttpStatusCode.OK));
        McpAssert.JsonEqual(warehouse, await W.GetAsync(tenant.Client, warehouse.Id()));
    }

    [Fact]
    public async Task AC20_Provisioning_keeps_its_response_and_every_tenant_gets_its_own_CENTRAL()
    {
        using var admin = app.Admin();
        var codes = new[] { XerpFixture.UniqueCode(), XerpFixture.UniqueCode() };
        var ids = new List<Guid>();

        foreach (var code in codes)
        {
            using var response = await admin.PostAsync("/api/v1/admin/tenants", HttpAssert.Raw($$"""{ "code": "{{code}}", "name": "Tenant" }"""));
            var body = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
            // 001 §4.1: the response is unchanged — no warehouse in it.
            Assert.Equal(new[] { "apiKey", "tenant" }, body.PropertyNames());
            using var client = app.WithBearer(body.GetProperty("apiKey").Str("key"));
            var dw = await Balance.DefaultAsync(client);
            Assert.Equal("CENTRAL", dw.Str("code"));
            Assert.Equal(body.GetProperty("apiKey").Id(), dw.GetProperty("createdBy").GetGuid());
            ids.Add(dw.Id());
        }

        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task AC21_A_created_warehouse_is_not_the_default_and_the_code_CENTRAL_is_taken()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await W.PostAsync(tenant.Client, W.Minimal("W1", "Shop"));
        var w1 = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        using var taken = await W.PostAsync(tenant.Client, W.Minimal("central", "Second central"));

        Assert.False(w1.Bool("isDefault"));
        Assert.Equal(Representation, w1.PropertyNames());
        Assert.Equal(["CENTRAL"], (await W.ListAsync(tenant.Client, "?isDefault=true")).Codes());
        var others = await W.ListAsync(tenant.Client, "?isDefault=false");
        Assert.Equal(["W1"], others.Codes());
        Assert.Equal(1, others.Total());
        await HttpAssert.CodeTakenAsync(taken);
        Assert.Equal(2, (await W.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC21_The_isDefault_filter_combines_with_the_other_filters()
    {
        var tenant = await app.NewTenantAsync();
        await W.CreateAsync(tenant.Client, W.Minimal("CENTRAL-2", "Central annex"));
        await W.CreateAsync(tenant.Client, W.Minimal("OFF", "Central closed").With("isActive", false));

        Assert.Equal(["CENTRAL", "CENTRAL-2", "OFF"], (await W.ListAsync(tenant.Client, "?search=central")).Codes());
        Assert.Equal(["CENTRAL"], (await W.ListAsync(tenant.Client, "?search=central&isDefault=true")).Codes());
        Assert.Equal(["CENTRAL-2", "OFF"], (await W.ListAsync(tenant.Client, "?search=central&isDefault=false")).Codes());
        Assert.Equal(["CENTRAL-2"], (await W.ListAsync(tenant.Client, "?isDefault=false&isActive=true")).Codes());
        var none = await W.ListAsync(tenant.Client, "?isDefault=true&isActive=false");
        Assert.Equal(0, none.Total());
        Assert.Empty(none.Items());
    }

    [Fact]
    public async Task AC22_The_default_warehouse_is_renamed_recoded_and_addressed_like_any_other_and_CENTRAL_becomes_free()
    {
        var tenant = await app.NewTenantAsync();
        var k2 = await Keys.CreateAsync(app, tenant.Client, "second", "human");
        var dw = await Balance.DefaultAsync(tenant.Client);
        var body = W.Full("HQ").With("name", "Headquarters");

        var replaced = await W.ReplaceAsync(k2.Client, dw.Id(), body);
        using var response = await W.PostAsync(tenant.Client, W.Minimal("CENTRAL", "A new central"));
        var second = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        JsonBody.AssertHasValues(body, replaced);
        Assert.True(replaced.Bool("isDefault"));
        Assert.Equal(dw.Id(), replaced.Id());
        Assert.Equal(k2.Id, replaced.GetProperty("updatedBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, replaced.GetProperty("createdBy").GetGuid());
        Assert.False(second.Bool("isDefault"));
        // R3: the code has no meaning of its own — the default is still the re-coded warehouse.
        await AssertDefaultIsAsync(tenant.Client, dw.Id());
        Assert.Equal(["CENTRAL"], (await W.ListAsync(tenant.Client, "?isDefault=false")).Codes());
    }

    // ---- AC-23, AC-24: the default cannot be deactivated or deleted ----

    [Fact]
    public async Task AC23_The_default_warehouse_cannot_be_deactivated()
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultAsync(tenant.Client);
        await W.CreateAsync(tenant.Client, "W1", "Shop");

        using var plain = await W.PutAsync(tenant.Client, dw.Id(), Deactivated(dw));
        using var changed = await W.PutAsync(tenant.Client, dw.Id(), W.Full("HQ").With("isActive", false));
        // R4 / E3: DEFAULT_WAREHOUSE is checked before CODE_TAKEN.
        using var withTakenCode = await W.PutAsync(tenant.Client, dw.Id(), Deactivated(dw).With("code", "w1"));

        await Balance.DefaultWarehouseAsync(plain, "isActive");
        await Balance.DefaultWarehouseAsync(changed, "isActive");
        await Balance.DefaultWarehouseAsync(withTakenCode, "isActive");
        await W.AssertUnchangedAsync(tenant.Client, dw);
        await AssertDefaultIsAsync(tenant.Client, dw.Id());
    }

    [Fact]
    public async Task AC23_R4_Validation_precedes_DEFAULT_WAREHOUSE_and_an_active_replace_with_a_taken_code_is_CODE_TAKEN()
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultAsync(tenant.Client);
        await W.CreateAsync(tenant.Client, "W1", "Shop");

        using var invalid = await W.PutAsync(tenant.Client, dw.Id(), Deactivated(dw).With("name", ""));
        using var missing = await W.PutAsync(tenant.Client, dw.Id(), Deactivated(dw).Without("city"));
        using var unknown = await W.PutAsync(tenant.Client, Guid.NewGuid(), Deactivated(dw));
        using var takenCode = await W.PutAsync(tenant.Client, dw.Id(), Stock.WarehouseBody(dw).With("code", "W1"));

        await HttpAssert.ValidationAsync(invalid, "name");
        await HttpAssert.ValidationAsync(missing, "city");
        await HttpAssert.NotFoundAsync(unknown);
        await HttpAssert.CodeTakenAsync(takenCode);
        await W.AssertUnchangedAsync(tenant.Client, dw);
    }

    [Fact]
    public async Task AC24_The_default_warehouse_cannot_be_deleted_used_or_not()
    {
        var s = await Stock.SetupAsync(app);
        var dw = await Balance.DefaultAsync(s.Http);

        using var unused = await W.DeleteAsync(s.Http, dw.Id());
        await Stock.ReceiveAsync(s.Http, dw.Id(), s.A, 5);
        // R5 / E4: checked before IN_USE.
        using var used = await W.DeleteAsync(s.Http, dw.Id());

        await Balance.DefaultWarehouseAsync(unused);
        await Balance.DefaultWarehouseAsync(used);
        await W.AssertUnchangedAsync(s.Http, dw);
        await AssertDefaultIsAsync(s.Http, dw.Id());
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, dw.Id()));
    }

    [Fact]
    public async Task AC24_In_a_new_tenant_the_only_warehouse_cannot_be_deleted()
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultAsync(tenant.Client);

        using var response = await W.DeleteAsync(tenant.Client, dw.Id());

        await Balance.DefaultWarehouseAsync(response);
        Assert.Equal(["CENTRAL"], (await W.ListAsync(tenant.Client)).Codes());
        await W.AssertUnchangedAsync(tenant.Client, dw);
    }

    // ---- AC-25, AC-26: set-default ----

    [Fact]
    public async Task AC25_Set_default_moves_the_flag_and_the_former_default_is_ordinary_again()
    {
        var tenant = await app.NewTenantAsync();
        var k2 = await Keys.CreateAsync(app, tenant.Client, "second", "human");
        var former = await Balance.DefaultAsync(tenant.Client);
        var w1 = await W.CreateAsync(tenant.Client, "W1", "Shop");

        using var response = await Balance.SendSetDefaultAsync(k2.Client, w1.Id());
        var result = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);

        Assert.Equal(w1.Id(), result.Id());
        Assert.True(result.Bool("isDefault"));
        Assert.Equal(k2.Id, result.GetProperty("updatedBy").GetGuid());
        Assert.Equal(Representation, result.PropertyNames());
        McpAssert.JsonEqual(result, await W.GetAsync(tenant.Client, w1.Id()));
        var defaults = await W.ListAsync(tenant.Client, "?isDefault=true");
        Assert.Equal(1, defaults.Total());
        Assert.Equal(w1.Id(), Assert.Single(defaults.Items()).Id());
        var formerNow = await W.GetAsync(tenant.Client, former.Id());
        Assert.False(formerNow.Bool("isDefault"));
        Assert.Equal(k2.Id, formerNow.GetProperty("updatedBy").GetGuid());
        // R6: both warehouses carry the updatedAt of the call; nothing else about them changed.
        Assert.Equal(result.Str("updatedAt"), formerNow.Str("updatedAt"));
        Assert.NotEqual(former.Str("updatedAt"), formerNow.Str("updatedAt"));
        foreach (var (before, after) in new[] { (former, formerNow), (w1, result) })
            foreach (var property in (string[])["code", "name", "isActive", "createdAt", "createdBy", .. MasterApi.AddressFields])
                McpAssert.JsonEqual(before.GetProperty(property), after.GetProperty(property), $"set-default changed '{property}'");

        // E5 / R7: the protection moved with the flag.
        using var deactivateNew = await W.PutAsync(tenant.Client, w1.Id(), Deactivated(result));
        using var deleteNew = await W.DeleteAsync(tenant.Client, w1.Id());
        await Balance.DefaultWarehouseAsync(deactivateNew, "isActive");
        await Balance.DefaultWarehouseAsync(deleteNew);
        var deactivated = await W.ReplaceAsync(tenant.Client, former.Id(), Deactivated(formerNow));
        Assert.False(deactivated.Bool("isActive"));
        Assert.False(deactivated.Bool("isDefault"));
        using var deleteFormer = await W.DeleteAsync(tenant.Client, former.Id());
        Assert.Equal(HttpStatusCode.NoContent, deleteFormer.StatusCode);
        Assert.Equal(["W1"], (await W.ListAsync(tenant.Client)).Codes());
        await AssertDefaultIsAsync(tenant.Client, w1.Id());
    }

    [Fact]
    public async Task AC25_The_default_can_be_moved_back_and_forth()
    {
        var tenant = await app.NewTenantAsync();
        var central = await Balance.DefaultIdAsync(tenant.Client);
        var w1 = (await W.CreateAsync(tenant.Client, "W1")).Id();
        var w2 = (await W.CreateAsync(tenant.Client, "W2")).Id();

        foreach (var target in new[] { w1, w2, central, w2, w1, central })
        {
            Assert.True((await Balance.SetDefaultAsync(tenant.Client, target)).Bool("isDefault"));
            await AssertDefaultIsAsync(tenant.Client, target);
        }

        Assert.Equal(3, (await W.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC26_Set_default_of_the_default_changes_nothing()
    {
        var tenant = await app.NewTenantAsync();
        var k2 = await Keys.CreateAsync(app, tenant.Client, "second", "human");
        var before = await Balance.DefaultAsync(tenant.Client);

        using var response = await Balance.SendSetDefaultAsync(k2.Client, before.Id());

        // R6: updatedAt and updatedBy included.
        McpAssert.JsonEqual(before, await HttpAssert.JsonAsync(response, HttpStatusCode.OK), "set-default of the default changed it");
        await W.AssertUnchangedAsync(tenant.Client, before);
    }

    [Fact]
    public async Task AC26_Set_default_of_an_inactive_warehouse_is_refused_and_of_an_unknown_one_is_not_found()
    {
        var tenant = await app.NewTenantAsync();
        var dw = await Balance.DefaultAsync(tenant.Client);
        var inactive = await W.CreateAsync(tenant.Client, W.Minimal("OFF", "Closed").With("isActive", false));

        using var refused = await Balance.SendSetDefaultAsync(tenant.Client, inactive.Id());
        using var random = await Balance.SendSetDefaultAsync(tenant.Client, Guid.NewGuid());
        using var malformed = await Balance.SendSetDefaultAsync(tenant.Client, "not-a-uuid");

        await Balance.DefaultWarehouseAsync(refused, "isActive");
        await HttpAssert.NotFoundAsync(random);
        await HttpAssert.NotFoundAsync(malformed);
        await W.AssertUnchangedAsync(tenant.Client, dw);
        await W.AssertUnchangedAsync(tenant.Client, inactive);
        await AssertDefaultIsAsync(tenant.Client, dw.Id());

        // Once it is active it can become the default.
        await Stock.SetWarehouseActiveAsync(tenant.Client, inactive.Id(), true);
        Assert.True((await Balance.SetDefaultAsync(tenant.Client, inactive.Id())).Bool("isDefault"));
        await AssertDefaultIsAsync(tenant.Client, inactive.Id());
    }

    // ---- AC-27, AC-28: races ----

    [Fact]
    public async Task AC27_Two_parallel_set_defaults_on_different_warehouses_leave_exactly_one_default()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var w1 = (await W.CreateAsync(http, "W1")).Id();
        var w2 = (await W.CreateAsync(http, "W2")).Id();

        for (var round = 1; round <= 12; round++)
        {
            var responses = await Task.WhenAll(
                Task.Run(() => Balance.SendSetDefaultAsync(http, round % 2 == 0 ? w1 : w2)),
                Task.Run(() => Balance.SendSetDefaultAsync(http, round % 2 == 0 ? w2 : w1)));

            foreach (var response in responses)
            {
                var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
                Assert.True(body.Id() == w1 || body.Id() == w2, $"Round {round}: set-default returned another warehouse: {body}");
                response.Dispose();
            }
            var defaults = await W.ListAsync(http, "?isDefault=true");
            Assert.True(defaults.Total() == 1 && defaults.Items().Length == 1, $"Round {round}: not exactly one default: {defaults}");
            var winner = defaults.Items()[0];
            Assert.True(winner.Id() == w1 || winner.Id() == w2, $"Round {round}: the default is neither target: {winner}");
            Assert.True(winner.Bool("isActive"));
            Assert.Equal(2, (await W.ListAsync(http, "?isDefault=false")).Total());
        }
    }

    [Fact]
    public async Task AC28_Set_default_racing_with_deactivation_never_leaves_an_inactive_or_missing_default()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var dw = await Balance.DefaultIdAsync(http);
        var w2 = await W.CreateAsync(http, "W2", "Second");
        var outcomes = new List<string>();

        for (var round = 1; round <= 12; round++)
        {
            var body = Deactivated(w2);
            var setFirst = round % 2 == 0;
            var setDefault = setFirst ? Task.Run(() => Balance.SendSetDefaultAsync(http, w2.Id())) : null;
            var deactivate = Task.Run(() => W.PutAsync(http, w2.Id(), body));
            setDefault ??= Task.Run(() => Balance.SendSetDefaultAsync(http, w2.Id()));
            using var set = await setDefault;
            using var put = await deactivate;

            var defaults = await W.ListAsync(http, "?isDefault=true");
            Assert.True(defaults.Total() == 1 && defaults.Items().Length == 1, $"Round {round}: not exactly one default: {defaults}");
            var current = defaults.Items()[0];
            Assert.True(current.Bool("isActive"), $"Round {round}: the default warehouse is inactive: {current}");
            var w2Now = await W.GetAsync(http, w2.Id());
            if (set.StatusCode == HttpStatusCode.OK)
            {
                // Set-default won: the deactivation met the default warehouse.
                await Balance.DefaultWarehouseAsync(put, "isActive");
                Assert.True(current.Id() == w2.Id() && w2Now.Bool("isActive") && w2Now.Bool("isDefault"),
                    $"Round {round}: (200, 409) but W2 is not the active default: {w2Now}");
                outcomes.Add("set-default");
            }
            else
            {
                // The deactivation won: set-default met an inactive warehouse.
                await Balance.DefaultWarehouseAsync(set, "isActive");
                var putText = await put.Content.ReadAsStringAsync();
                Assert.True(put.StatusCode == HttpStatusCode.OK, $"Round {round}: (409, {(int)put.StatusCode}) is not an allowed outcome: {putText}");
                Assert.True(current.Id() == dw && !w2Now.Bool("isActive") && !w2Now.Bool("isDefault"),
                    $"Round {round}: (409, 200) but W2 is {w2Now} and the default is {current}");
                outcomes.Add("deactivate");
            }

            // Between rounds: the default back on DW, W2 active again.
            await Balance.SetDefaultAsync(http, dw);
            await Stock.SetWarehouseActiveAsync(http, w2.Id(), true);
        }

        Assert.Equal(12, outcomes.Count);
    }
}
