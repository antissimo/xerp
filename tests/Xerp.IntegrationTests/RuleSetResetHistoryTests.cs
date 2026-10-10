using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-30 to AC-37: set, reset and the history of changes (R5–R12, E1, E2, E4, E11). A value is the
/// JSON literal <c>true</c> or <c>false</c>; every change is attributed and kept.
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleSetResetHistoryTests(XerpFixture app)
{
    private const string Key = Rules.NegativeStock;

    private static async Task<JsonElement[]> HistoryAsync(HttpClient client, string query = "")
    {
        var list = await Rules.ChangesAsync(client, query);
        Assert.Equal(list.Total(), list.Items().Length);
        return list.Items();
    }

    // ---- AC-30 ----

    [Fact]
    public async Task AC30_Set_gives_the_tenant_its_own_value_attributed_to_the_acting_key()
    {
        var tenant = await app.NewTenantAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var rule = await Rules.SetAsync(tenant.Client, Key, true);

        Rules.AssertTenantValue(rule, Key, true, tenant.ApiKeyId);
        Rules.AssertBool(rule, "default", false);
        Assert.InRange(rule.Time("updatedAt"), before, DateTimeOffset.UtcNow.AddSeconds(5));
        McpAssert.JsonEqual(rule, await Rules.GetAsync(tenant.Client, Key), "GET differs from the answer of the Set");
        McpAssert.JsonEqual(rule, (await Rules.ListAsync(tenant.Client)).Items().Single(r => r.Str("key") == Key));
        // Only that rule changed.
        foreach (var other in (await Rules.ListAsync(tenant.Client)).Items().Where(r => r.Str("key") != Key))
        {
            Rules.AssertAtDefault(other, other.Str("key"));
            JsonBody.AssertNull(other, "updatedAt", "updatedBy");
        }
    }

    // ---- AC-31, E1 ----

    [Theory]
    [InlineData("""{ "value": "true" }""")]
    [InlineData("""{ "value": "false" }""")]
    [InlineData("""{ "value": "yes" }""")]
    [InlineData("""{ "value": "allow" }""")]
    [InlineData("""{ "value": 1 }""")]
    [InlineData("""{ "value": 0 }""")]
    [InlineData("""{ "value": null }""")]
    [InlineData("""{ "value": [] }""")]
    [InlineData("""{ "value": [true] }""")]
    [InlineData("""{ "value": {} }""")]
    [InlineData("""{ "value": "" }""")]
    [InlineData("{}")]
    public async Task AC31_A_value_that_is_not_a_JSON_boolean_is_400_with_key_value_and_changes_nothing(string body)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        // In a tenant at the default …
        using var atDefault = await Rules.SendSetRawAsync(http, Key, body);

        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(atDefault, "value"));
        Rules.AssertAtDefault(await Rules.GetAsync(http, Key), Key);
        JsonBody.AssertNull(await Rules.GetAsync(http, Key), "updatedAt", "updatedBy");
        Assert.Empty(await HistoryAsync(http));

        // … and in one that has its own value.
        var set = await Rules.SetAsync(http, Key, true);

        using var afterSet = await Rules.SendSetRawAsync(http, Key, body);

        await HttpAssert.ValidationAsync(afterSet, "value");
        McpAssert.JsonEqual(set, await Rules.GetAsync(http, Key), "A refused Set changed the rule");
        Assert.Single(await HistoryAsync(http));
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{ "value": true """)]
    [InlineData("[true]")]
    [InlineData("true")]
    public async Task AC31_R5_A_malformed_body_is_400_and_changes_nothing(string body)
    {
        var tenant = await app.NewTenantAsync();
        var set = await Rules.SetAsync(tenant.Client, Key, true);

        using var response = await Rules.SendSetRawAsync(tenant.Client, Key, body);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        McpAssert.JsonEqual(set, await Rules.GetAsync(tenant.Client, Key));
        Assert.Single(await HistoryAsync(tenant.Client));
    }

    // ---- AC-32, E2 ----

    [Theory]
    [InlineData("nope")]
    [InlineData("Stock.NegativeStockAllowed")]
    [InlineData("stock.negativeStock")]
    [InlineData("partner.roleRequired")]
    public async Task AC32_The_form_of_the_body_is_checked_before_the_key_and_the_key_before_the_value(string unknownKey)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        // The route exists: a known key with a valid body is 200.
        await Rules.SetAsync(http, Key, true);

        using var noValue = await Rules.SendSetRawAsync(http, unknownKey, "{}");
        using var unknownProperty = await Rules.SendSetRawAsync(http, unknownKey, """{ "value": true, "x": 1 }""");
        using var valid = await Rules.SendSetAsync(http, unknownKey, true);
        using var validFalse = await Rules.SendSetAsync(http, unknownKey, false);
        // R5: the rule exists (404) before the value (400).
        using var badValue = await Rules.SendSetRawAsync(http, unknownKey, """{ "value": "yes" }""");

        await HttpAssert.ValidationAsync(noValue, "value");
        await HttpAssert.ValidationAsync(unknownProperty, "x");
        Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(valid, HttpStatusCode.NotFound, "NOT_FOUND"));
        await HttpAssert.NotFoundAsync(validFalse);
        await HttpAssert.NotFoundAsync(badValue);
        Assert.Single(await HistoryAsync(http));
        Assert.Equal(new[] { Key }, (await Rules.ListAsync(http, "?source=tenant")).RuleKeys());
    }

    // ---- AC-33, AC-34 ----

    [Fact]
    public async Task AC33_Setting_the_defaults_own_value_is_a_set_like_any_other()
    {
        var tenant = await app.NewTenantAsync();

        var rule = await Rules.SetAsync(tenant.Client, Rules.PurchasePartner, true);

        Rules.AssertTenantValue(rule, Rules.PurchasePartner, true, tenant.ApiKeyId);
        var change = Assert.Single(await HistoryAsync(tenant.Client));
        Rules.AssertChange(change, Rules.PurchasePartner, "set", oldValue: true, newValue: true, tenant.ApiKeyId);
        Assert.Equal(rule.Time("updatedAt"), change.Time("changedAt"));
        Assert.Equal(new[] { Rules.PurchasePartner }, (await Rules.ListAsync(tenant.Client, "?source=tenant")).RuleKeys());
    }

    [Fact]
    public async Task AC34_Setting_the_same_value_twice_changes_nothing_the_second_time()
    {
        var tenant = await app.NewTenantAsync();
        var other = await Keys.CreateAsync(app, tenant.Client, "bot", "agent");
        var first = await Rules.SetAsync(tenant.Client, Key, true);
        await Task.Delay(20);

        // R8: neither the time nor the key of the second, changeless Set is recorded.
        var second = await Rules.SetAsync(other.Client, Key, true);

        McpAssert.JsonEqual(first, second, "The second Set of the same value changed the rule");
        Assert.Equal(first.Time("updatedAt"), second.Time("updatedAt"));
        McpAssert.JsonEqual(first, await Rules.GetAsync(tenant.Client, Key));
        Rules.AssertChange(Assert.Single(await HistoryAsync(tenant.Client)), Key, "set", false, true, tenant.ApiKeyId);
    }

    // ---- AC-35 ----

    [Fact]
    public async Task AC35_Reset_returns_the_default_and_a_second_reset_changes_nothing()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var set = await Rules.SetAsync(http, Key, true);
        await Task.Delay(20);

        var reset = await Rules.ResetAsync(http, Key);

        Rules.AssertAtDefault(reset, Key);
        Rules.AssertBool(reset, "value", false);
        Assert.True(reset.Time("updatedAt") > set.Time("updatedAt"), $"updatedAt of the reset {reset.Time("updatedAt"):O} is not later than {set.Time("updatedAt"):O}.");
        Assert.Equal(tenant.ApiKeyId, reset.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(reset, await Rules.GetAsync(http, Key), "GET differs from the answer of the reset");
        Assert.Equal(0, (await Rules.ListAsync(http, "?source=tenant")).Total());
        await Task.Delay(20);

        var again = await Rules.ResetAsync(http, Key);

        McpAssert.JsonEqual(reset, again, "A second reset changed the rule");
        Assert.Equal(2, (await HistoryAsync(http)).Length);

        using var unknown = await Rules.SendResetAsync(http, "nope");
        await HttpAssert.NotFoundAsync(unknown);
    }

    [Fact]
    public async Task AC35_R9_Reset_of_a_rule_that_was_never_set_succeeds_and_records_nothing()
    {
        var tenant = await app.NewTenantAsync();
        var before = await Rules.GetAsync(tenant.Client, Rules.SalesPartner);

        var reset = await Rules.ResetAsync(tenant.Client, Rules.SalesPartner);

        McpAssert.JsonEqual(before, reset);
        JsonBody.AssertNull(reset, "updatedAt", "updatedBy");
        Assert.Empty(await HistoryAsync(tenant.Client));
    }

    [Fact]
    public async Task AC35_R9_Reset_removes_a_tenant_value_that_equals_the_default()
    {
        // R7 then R9: the tenant's own "true" for a rule whose default is true is still its own value until reset.
        var tenant = await app.NewTenantAsync();
        await Rules.SetAsync(tenant.Client, Rules.SalesPartner, true);

        var reset = await Rules.ResetAsync(tenant.Client, Rules.SalesPartner);

        Rules.AssertAtDefault(reset, Rules.SalesPartner);
        var history = await HistoryAsync(tenant.Client);
        Assert.Equal(2, history.Length);
        Rules.AssertChange(history[0], Rules.SalesPartner, "reset", true, true, tenant.ApiKeyId);
        Rules.AssertChange(history[1], Rules.SalesPartner, "set", true, true, tenant.ApiKeyId);
    }

    // ---- AC-36 ----

    [Fact]
    public async Task AC36_The_history_shows_who_changed_what_and_when_newest_first()
    {
        var tenant = await app.NewTenantAsync();
        var k2 = await Keys.CreateAsync(app, tenant.Client, "claude", "agent");
        var set = await Rules.SetAsync(tenant.Client, Key, true);
        await Task.Delay(20);
        var reset = await Rules.ResetAsync(k2.Client, Key);
        Assert.Equal(k2.Id, reset.GetProperty("updatedBy").GetGuid());

        var list = await Rules.ChangesAsync(tenant.Client, $"?key={Key}");

        Assert.Equal(new[] { "items", "limit", "offset", "total" }, list.PropertyNames());
        Assert.Equal((2, 2), (list.Total(), list.Items().Length));
        var (newest, oldest) = (list.Items()[0], list.Items()[1]);
        Rules.AssertChange(newest, Key, "reset", oldValue: true, newValue: false, k2.Id);
        Rules.AssertChange(oldest, Key, "set", oldValue: false, newValue: true, tenant.ApiKeyId);
        Assert.Equal(reset.Time("updatedAt"), newest.Time("changedAt"));
        Assert.Equal(set.Time("updatedAt"), oldest.Time("changedAt"));
        Assert.NotEqual(newest.GetProperty("id").GetGuid(), oldest.GetProperty("id").GetGuid());

        // Without key: the changes of all rules, newest first.
        await Task.Delay(20);
        var third = await Rules.SetAsync(k2.Client, Rules.SalesPartner, false);
        var all = await HistoryAsync(tenant.Client);
        Assert.Equal(new[] { Rules.SalesPartner, Key, Key }, all.Select(c => c.Str("key")));
        Rules.AssertChange(all[0], Rules.SalesPartner, "set", oldValue: true, newValue: false, k2.Id);
        Assert.Equal(third.Time("updatedAt"), all[0].Time("changedAt"));
        McpAssert.JsonEqual(newest, all[1]);
        McpAssert.JsonEqual(oldest, all[2]);
        // The key filter is exact; a string that is no rule gives an empty list (R11).
        Assert.Equal(2, (await HistoryAsync(tenant.Client, $"?key={Key}")).Length);
        Assert.Single(await HistoryAsync(tenant.Client, $"?key={Rules.SalesPartner}"));
        Assert.Empty(await HistoryAsync(tenant.Client, "?key=nope"));
        Assert.Empty(await HistoryAsync(tenant.Client, "?key=stock"));
        Assert.Empty(await HistoryAsync(tenant.Client, $"?key={Rules.Protected}"));
        // Paging as everywhere.
        var page = await Rules.ChangesAsync(tenant.Client, "?limit=1&offset=1");
        Assert.Equal(3, page.Total());
        McpAssert.JsonEqual(newest, Assert.Single(page.Items()));
        // Both keys of the tenant read the same history.
        McpAssert.JsonEqual(await Rules.ChangesAsync(tenant.Client), await Rules.ChangesAsync(k2.Client));
    }

    [Fact]
    public async Task AC36_E4_Set_reset_and_the_same_set_again_are_three_changes()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        await Rules.SetAsync(http, Key, true);
        await Rules.SetAsync(http, Key, true);
        await Rules.ResetAsync(http, Key);
        await Rules.ResetAsync(http, Key);
        await Rules.SetAsync(http, Key, true);
        await Rules.SetAsync(http, Key, false);

        var history = await HistoryAsync(http);
        Assert.Equal(
            new[] { ("set", true, false), ("set", false, true), ("reset", true, false), ("set", false, true) },
            history.Select(c => (c.Str("action"), c.GetProperty("oldValue").GetBoolean(), c.GetProperty("newValue").GetBoolean())));
        // R10: each change starts from the value the one before it left.
        for (var i = 0; i < history.Length - 1; i++)
            Assert.True(history[i].Time("changedAt") >= history[i + 1].Time("changedAt"), "The history is not newest first.");
        Rules.AssertTenantValue(await Rules.GetAsync(http, Key), Key, false, tenant.ApiKeyId);
    }

    [Fact]
    public async Task AC36_E11_A_rule_set_by_a_key_that_is_revoked_afterwards_keeps_its_value_and_its_attribution()
    {
        var tenant = await app.NewTenantAsync();
        var bot = await Keys.CreateAsync(app, tenant.Client, "bot", "agent");
        var set = await Rules.SetAsync(bot.Client, Rules.Protected, true);

        using var revoke = await Keys.RevokeAsync(tenant.Client, bot.Id);
        Assert.True(revoke.IsSuccessStatusCode, $"Revoking the key failed: {(int)revoke.StatusCode}");

        var rule = await Rules.GetAsync(tenant.Client, Rules.Protected);
        McpAssert.JsonEqual(set, rule, "Revoking the key changed the rule");
        Rules.AssertTenantValue(rule, Rules.Protected, true, bot.Id);
        Rules.AssertChange(Assert.Single(await HistoryAsync(tenant.Client)), Rules.Protected, "set", false, true, bot.Id);
        using var refused = await Rules.SendSetAsync(bot.Client, Rules.Protected, false);
        await HttpAssert.UnauthenticatedAsync(refused);
    }

    // ---- AC-37 ----

    [Theory]
    [InlineData(Rules.OverReceipt)]
    [InlineData(Rules.PurchasePartner)]
    [InlineData(Rules.OverDelivery)]
    [InlineData(Rules.SalesPartner)]
    [InlineData(Rules.Protected)]
    [InlineData(Rules.NegativeStock)]
    public async Task AC37_Every_rule_accepts_both_values(string key)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var @default = Rules.Default(key);

        Rules.AssertTenantValue(await Rules.SetAsync(http, key, true), key, true, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.GetAsync(http, key), key, true, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.SetAsync(http, key, false), key, false, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.GetAsync(http, key), key, false, tenant.ApiKeyId);

        var history = await HistoryAsync(http);
        Assert.Equal(2, history.Length);
        Rules.AssertChange(history[0], key, "set", oldValue: true, newValue: false, tenant.ApiKeyId);
        Rules.AssertChange(history[1], key, "set", oldValue: @default, newValue: true, tenant.ApiKeyId);
        Rules.AssertAtDefault(await Rules.ResetAsync(http, key), key);
    }

    [Fact]
    public async Task AC37_R12_The_six_rules_can_be_set_and_reset_in_any_order()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        foreach (var key in Rules.Keys.Reverse())
            await Rules.SetAsync(http, key, !Rules.Default(key));

        var list = await Rules.ListAsync(http, "?source=tenant");
        Assert.Equal(Rules.Keys, list.RuleKeys());
        foreach (var rule in list.Items())
            Rules.AssertTenantValue(rule, rule.Str("key"), !Rules.Default(rule.Str("key")), tenant.ApiKeyId);

        foreach (var key in Rules.Keys)
            Rules.AssertAtDefault(await Rules.ResetAsync(http, key), key);

        Assert.Equal(0, (await Rules.ListAsync(http, "?source=tenant")).Total());
        Assert.Equal(12, (await HistoryAsync(http)).Length);
    }
}
