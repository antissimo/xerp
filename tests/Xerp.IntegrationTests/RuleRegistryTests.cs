using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-10, AC-11 (inherited behaviour of the five routes) and AC-20 to AC-22 (the registry: the
/// literal inventory of rules with their defaults, get, filters). Rules R1–R4, E3.
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleRegistryTests(XerpFixture app)
{
    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT")
            request.Content = HttpAssert.Raw("""{ "value": true }""");
        return request;
    }

    // ---- AC-10, AC-11 ----

    [Theory]
    [InlineData("GET", Rules.Path)]
    [InlineData("GET", Rules.Path + "/" + Rules.NegativeStock)]
    [InlineData("PUT", Rules.Path + "/" + Rules.NegativeStock)]
    [InlineData("POST", Rules.Path + "/" + Rules.NegativeStock + "/reset")]
    [InlineData("GET", Rules.Changes)]
    public async Task AC10_Each_route_without_a_credential_is_401_and_with_the_admin_key_403(string method, string path)
    {
        using var anonymous = await app.Anonymous().SendAsync(Request(method, path));
        using var admin = await app.Admin().SendAsync(Request(method, path));

        await HttpAssert.UnauthenticatedAsync(anonymous);
        await HttpAssert.ForbiddenAsync(admin);
    }

    [Fact]
    public async Task AC10_S1_With_a_tenant_key_human_or_agent_every_route_answers_200()
    {
        // The other half of AC-10: the routes exist, and every tenant key may read and change rules (S1).
        var tenant = await app.NewTenantAsync();
        var agent = await Keys.CreateAsync(app, tenant.Client, "bot", "agent");

        foreach (var client in new[] { tenant.Client, agent.Client })
            foreach (var (method, path) in new[]
                     {
                         ("GET", Rules.Path), ("GET", Rules.PathOf(Rules.NegativeStock)), ("PUT", Rules.PathOf(Rules.NegativeStock)),
                         ("POST", Rules.ResetPath(Rules.NegativeStock)), ("GET", Rules.Changes),
                     })
            {
                using var response = await client.SendAsync(Request(method, path));
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    $"{method} {path}: expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
    }

    [Theory]
    [InlineData(Rules.Path + "?foo=1", "foo")]
    [InlineData(Rules.Path + "?limit=0", "limit")]
    [InlineData(Rules.Path + "?limit=501", "limit")]
    [InlineData(Rules.Path + "?offset=-1", "offset")]
    [InlineData(Rules.Path + "?source=mine", "source")]
    [InlineData(Rules.Path + "?source=Tenant", "source")]
    [InlineData(Rules.Path + "?Group=sales", "Group")]
    [InlineData(Rules.Changes + "?foo=1", "foo")]
    [InlineData(Rules.Changes + "?limit=0", "limit")]
    [InlineData(Rules.Changes + "?Key=" + Rules.NegativeStock, "Key")]
    public async Task AC11_E3_A_bad_query_parameter_is_400_with_its_key(string url, string key)
    {
        var tenant = await app.NewTenantAsync();
        // The route exists: without the bad parameter it answers 200.
        using var plain = await tenant.Client.GetAsync(url[..url.IndexOf('?')]);
        await HttpAssert.JsonAsync(plain, HttpStatusCode.OK);

        using var response = await tenant.Client.GetAsync(url);

        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(response, key));
    }

    [Fact]
    public async Task AC11_Set_with_an_unknown_property_is_400_with_that_propertys_key()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await Rules.SendSetRawAsync(tenant.Client, Rules.NegativeStock, """{ "value": true, "x": 1 }""");

        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(response, "x"));
        Rules.AssertAtDefault(await Rules.GetAsync(tenant.Client, Rules.NegativeStock), Rules.NegativeStock);
        Assert.Equal(0, (await Rules.ChangesAsync(tenant.Client)).Total());
    }

    [Theory]
    [InlineData("DELETE", Rules.Path + "/" + Rules.NegativeStock)]
    [InlineData("POST", Rules.Path)]
    [InlineData("POST", Rules.Path + "/" + Rules.NegativeStock)]
    [InlineData("GET", Rules.Path + "/" + Rules.NegativeStock + "/reset")]
    [InlineData("DELETE", Rules.Changes)]
    [InlineData("POST", Rules.Changes)]
    public async Task E3_S2_A_method_the_route_does_not_support_is_404_and_changes_nothing(string method, string path)
    {
        // 001/E11; and S2: no operation edits or deletes a rule's history.
        var tenant = await app.NewTenantAsync();
        await Rules.SetAsync(tenant.Client, Rules.NegativeStock, true);

        using var response = await tenant.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        await HttpAssert.NotFoundAsync(response);
        Rules.AssertTenantValue(await Rules.GetAsync(tenant.Client, Rules.NegativeStock), Rules.NegativeStock, true, tenant.ApiKeyId);
        Assert.Equal(1, (await Rules.ChangesAsync(tenant.Client)).Total());
    }

    // ---- AC-20 ----

    [Fact]
    public async Task AC20_A_new_tenant_lists_exactly_the_six_rules_in_order_with_their_defaults()
    {
        var tenant = await app.NewTenantAsync();

        var list = await Rules.ListAsync(tenant.Client);

        Assert.Equal(new[] { "items", "limit", "offset", "total" }, list.PropertyNames());
        Assert.Equal(6, list.Total());
        // The literal inventory (architecture §9): a spec that adds a rule adds it here, with its default.
        var inventory = new (string Key, bool Default)[]
        {
            ("purchase.overReceiptAllowed", false),
            ("purchase.partnerRequired", true),
            ("sales.overDeliveryAllowed", false),
            ("sales.partnerRequired", true),
            ("sales.reservedStockProtected", false),
            ("stock.negativeStockAllowed", false),
        };
        Assert.Equal(inventory.Select(i => i.Key), list.RuleKeys());
        Assert.Equal(inventory.Select(i => i.Key), Rules.Keys);
        foreach (var (rule, expected) in list.Items().Zip(inventory))
        {
            // Exactly the nine properties of section 4: no "type", no "allowed".
            Assert.Equal(new[] { "default", "description", "group", "key", "name", "source", "updatedAt", "updatedBy", "value" }, rule.PropertyNames());
            Assert.Equal(expected.Key, rule.Str("key"));
            Assert.Equal(expected.Key.Split('.')[0], rule.Str("group"));
            Rules.AssertBool(rule, "default", expected.Default);
            Rules.AssertBool(rule, "value", expected.Default);
            Assert.Equal(expected.Default, Rules.Default(expected.Key));
            Assert.Equal("default", rule.Str("source"));
            JsonBody.AssertNull(rule, "updatedAt", "updatedBy");
            Assert.False(string.IsNullOrWhiteSpace(rule.Str("name")), $"{expected.Key}: name is empty.");
            Assert.False(string.IsNullOrWhiteSpace(rule.Str("description")), $"{expected.Key}: description is empty.");
        }
    }

    [Fact]
    public async Task AC20_R3_Creating_a_tenant_and_reading_rules_records_no_change_and_sets_no_value()
    {
        var tenant = await app.NewTenantAsync();

        await Rules.ListAsync(tenant.Client);
        await Rules.GetAsync(tenant.Client, Rules.PurchasePartner);
        var changes = await Rules.ChangesAsync(tenant.Client);

        Assert.Equal(new[] { "items", "limit", "offset", "total" }, changes.PropertyNames());
        Assert.Equal((0, 0), (changes.Total(), changes.Items().Length));
        Assert.Equal(0, (await Rules.ListAsync(tenant.Client, "?source=tenant")).Total());
    }

    // ---- AC-21 ----

    [Fact]
    public async Task AC21_Get_returns_the_same_object_as_the_list()
    {
        var tenant = await app.NewTenantAsync();
        var list = await Rules.ListAsync(tenant.Client);
        Assert.Equal(6, list.Items().Length);

        foreach (var listed in list.Items())
            McpAssert.JsonEqual(listed, await Rules.GetAsync(tenant.Client, listed.Str("key")), "GET /rules/{key} differs from the list item");

        var reserved = await Rules.GetAsync(tenant.Client, "sales.reservedStockProtected");
        Rules.AssertAtDefault(reserved, Rules.Protected);
        Assert.Equal("sales", reserved.Str("group"));
        McpAssert.JsonEqual(list.Items()[4], reserved);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Sales.ReservedStockProtected")]
    [InlineData("SALES.RESERVEDSTOCKPROTECTED")]
    [InlineData("sales.reservedstockprotected")]
    [InlineData("sales.reservedStockProtected.x")]
    [InlineData("sales")]
    // Keys of the first text of this spec (R4) and of a later batch.
    [InlineData("sales.reservation")]
    [InlineData("stock.negativeStock")]
    [InlineData("quantity.decimals")]
    [InlineData("document.maxLines")]
    [InlineData("partner.roleRequired")]
    [InlineData("%20")]
    [InlineData("sales.reservedStockProtected%20")]
    [InlineData("0199c0de-0000-7000-8000-000000000001")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task AC21_R4_A_key_that_is_not_in_the_registry_is_404_on_get_and_on_reset(string key)
    {
        var tenant = await app.NewTenantAsync();
        // The registry answers in this tenant: the 404 below is about the key, not about a missing route.
        Assert.Equal(6, (await Rules.ListAsync(tenant.Client)).Total());

        using var get = await tenant.Client.GetAsync(Rules.PathOf(key));
        using var reset = await Rules.SendResetAsync(tenant.Client, key);

        Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(get, HttpStatusCode.NotFound, "NOT_FOUND"));
        await HttpAssert.NotFoundAsync(reset);
        Assert.Equal(0, (await Rules.ChangesAsync(tenant.Client)).Total());
    }

    // ---- AC-22 ----

    [Fact]
    public async Task AC22_Filters_group_source_and_search()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        Assert.Equal(new[] { Rules.OverDelivery, Rules.SalesPartner, Rules.Protected }, (await Rules.ListAsync(http, "?group=sales")).RuleKeys());
        Assert.Equal(new[] { Rules.OverReceipt, Rules.PurchasePartner }, (await Rules.ListAsync(http, "?group=purchase")).RuleKeys());
        Assert.Equal(new[] { Rules.NegativeStock }, (await Rules.ListAsync(http, "?group=stock")).RuleKeys());
        Assert.Equal(new[] { Rules.PurchasePartner, Rules.SalesPartner }, (await Rules.ListAsync(http, "?search=partnerRequired")).RuleKeys());
        // 001/R9: a case-insensitive substring, trimmed.
        Assert.Equal(new[] { Rules.PurchasePartner, Rules.SalesPartner }, (await Rules.ListAsync(http, "?search=%20PARTNERREQUIRED%20")).RuleKeys());
        Assert.Equal(new[] { Rules.SalesPartner }, (await Rules.ListAsync(http, "?group=sales&search=partnerRequired")).RuleKeys());
        Assert.Equal(Rules.Keys, (await Rules.ListAsync(http, "?source=default")).RuleKeys());

        var noGroup = await Rules.ListAsync(http, "?group=nope");
        Assert.Equal((0, 0), (noGroup.Total(), noGroup.Items().Length));
        // R2: group is an exact value, not a prefix and not another case.
        Assert.Equal(0, (await Rules.ListAsync(http, "?group=sale")).Total());
        Assert.Equal(0, (await Rules.ListAsync(http, "?group=Sales")).Total());
        Assert.Equal(0, (await Rules.ListAsync(http, "?source=tenant")).Total());

        var set = await Rules.SetAsync(http, Rules.Protected, true);

        var own = await Rules.ListAsync(http, "?source=tenant");
        Assert.Equal(new[] { Rules.Protected }, own.RuleKeys());
        Assert.Equal(1, own.Total());
        McpAssert.JsonEqual(set, own.Items()[0], "The list item differs from the answer of the Set");
        Assert.Equal(Rules.Keys.Where(k => k != Rules.Protected), (await Rules.ListAsync(http, "?source=default")).RuleKeys());
        Assert.Equal(0, (await Rules.ListAsync(http, "?source=tenant&group=stock")).Total());
        Assert.Equal(new[] { Rules.Protected }, (await Rules.ListAsync(http, "?source=tenant&group=sales")).RuleKeys());
        // R1: the list has all six for every tenant, set or not.
        var all = await Rules.ListAsync(http);
        Assert.Equal(6, all.Total());
        Assert.Equal(Rules.Keys, all.RuleKeys());
    }

    [Fact]
    public async Task AC22_R2_The_list_is_paged_like_every_list()
    {
        var tenant = await app.NewTenantAsync();

        var page = await Rules.ListAsync(tenant.Client, "?limit=2&offset=1");
        var beyond = await Rules.ListAsync(tenant.Client, "?offset=6");

        Assert.Equal(new[] { Rules.PurchasePartner, Rules.OverDelivery }, page.RuleKeys());
        Assert.Equal((6, 2, 1), (page.Total(), page.GetProperty("limit").GetInt32(), page.GetProperty("offset").GetInt32()));
        Assert.Equal((6, 0), (beyond.Total(), beyond.Items().Length));
        Assert.Equal(50, (await Rules.ListAsync(tenant.Client)).GetProperty("limit").GetInt32());
    }
}
