using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-100 and AC-101 (section 8, T1–T4): rule values, the history and the value an error names are
/// the tenant's own — over HTTP and through the tools.
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleIsolationTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;

    /// <summary>Tenant X with all six rules at the value that is not the default, and tenant Y that sets nothing.</summary>
    private async Task<(OrderSetup X, OrderSetup Y)> WorldAsync()
    {
        var x = await Orders.SetupAsync(app);
        var y = await Orders.SetupAsync(app);
        foreach (var key in Rules.Keys)
            await Rules.SetAsync(x.Http, key, !Rules.Default(key));
        return (x, y);
    }

    private static void AssertSixDefaults(JsonElement list)
    {
        Assert.Equal(6, list.Total());
        Assert.Equal(Rules.Keys, list.RuleKeys());
        foreach (var rule in list.Items())
        {
            Rules.AssertAtDefault(rule, rule.Str("key"));
            JsonBody.AssertNull(rule, "updatedAt", "updatedBy");
        }
    }

    [Fact]
    public async Task AC100_Values_and_history_of_X_do_not_exist_for_Y_and_Y_is_judged_by_its_own_defaults()
    {
        var (x, y) = await WorldAsync();
        var untouched = await Rules.ListAsync(y.Http);

        AssertSixDefaults(untouched);
        Assert.Equal(0, (await Rules.ListAsync(y.Http, "?source=tenant")).Total());
        foreach (var key in Rules.Keys)
        {
            Rules.AssertAtDefault(await Rules.GetAsync(y.Http, key), key);
            Assert.Equal(0, (await Rules.ChangesAsync(y.Http, $"?key={key}")).Total());
        }
        var changes = await Rules.ChangesAsync(y.Http);
        Assert.Equal((0, 0), (changes.Total(), changes.Items().Length));

        // Y's operations are judged by Y's values, and the errors name Y's values (T2, T3).
        await Rules.AssertDefaultBehaviourAsync(y, named: true);

        // X still has its own values and its own history, untouched by anything Y did.
        var ofX = await Rules.ListAsync(x.Http, "?source=tenant");
        Assert.Equal(Rules.Keys, ofX.RuleKeys());
        foreach (var rule in ofX.Items())
            Rules.AssertTenantValue(rule, rule.Str("key"), !Rules.Default(rule.Str("key")), x.Tenant.ApiKeyId);
        var historyOfX = await Rules.ChangesAsync(x.Http);
        Assert.Equal(6, historyOfX.Total());
        Assert.All(historyOfX.Items(), c => Assert.Equal(x.Tenant.ApiKeyId, c.GetProperty("changedBy").GetGuid()));
        await Rules.AssertBalancedAsync(y.Http);
    }

    [Fact]
    public async Task AC100_T2_X_is_judged_by_its_own_values_while_Y_keeps_the_defaults()
    {
        var (x, y) = await WorldAsync();

        // In X, with every rule at the other value: negative stock, an order without a partner, over-receipt.
        await Stock.IssueAsync(x.Http, x.W1, x.A, 5);
        var order = await PO.ConfirmAsync(x.Http, (await PO.CreateAsync(x.Http, PO.NoPartner(x, (x.A, 10, null, 1m)))).Id());
        await PO.FulfilAsync(x.Http, order, 1, 30);
        Rules.AssertNoPartner(SO, await SO.CreateAsync(x.Http, SO.NoPartner(x, (x.A, 1, null, 1m))));
        Assert.Equal(25m, await Stock.QuantityAsync(x.Http, x.A, x.W1));

        // The same requests in Y.
        var (issue, refused) = await Rules.TryIssueAsync(y.Http, y.W1, y.A, 5);
        using (refused)
            await Rules.InsufficientAsync(refused, "lines[0].quantity");
        await Stock.AssertDraftAsync(y.Http, issue);
        using (var noSupplier = await PO.PostAsync(y.Http, PO.NoPartner(y, (y.A, 10, null, 1m))))
            await Rules.PartnerRequiredAsync(noSupplier, PO);
        using (var noCustomer = await SO.PostAsync(y.Http, SO.NoPartner(y, (y.A, 1, null, 1m))))
            await Rules.PartnerRequiredAsync(noCustomer, SO);
        var ofY = await PO.OrderedAsync(y, (y.A, 10, null, 1m));
        var (receipt, above) = await PO.TryFulfilAsync(y.Http, ofY, 1, 30);
        using (above)
            await Rules.ExceedsAsync(above, PO, "lines[0].quantity");
        await Stock.AssertDraftAsync(y.Http, receipt);

        // A Set in Y changes nothing in X either.
        await Rules.SetAsync(y.Http, Rules.NegativeStock, false);
        await Rules.ResetAsync(y.Http, Rules.PurchasePartner);
        Rules.AssertTenantValue(await Rules.GetAsync(x.Http, Rules.NegativeStock), Rules.NegativeStock, true, x.Tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.GetAsync(x.Http, Rules.PurchasePartner), Rules.PurchasePartner, false, x.Tenant.ApiKeyId);
        Assert.Equal(6, (await Rules.ChangesAsync(x.Http)).Total());
        Assert.Equal(1, (await Rules.ChangesAsync(y.Http)).Total());
        await Rules.AssertBalancedAsync(x.Http);
        await Rules.AssertBalancedAsync(y.Http);
    }

    [Fact]
    public async Task AC100_T2_The_history_of_Y_shows_only_Ys_changes_and_no_id_of_X()
    {
        var (x, y) = await WorldAsync();
        await Rules.SetAsync(y.Http, Rules.Protected, true);

        var ofY = await Rules.ChangesAsync(y.Http);
        var ofX = await Rules.ChangesAsync(x.Http);

        var change = Assert.Single(ofY.Items());
        Rules.AssertChange(change, Rules.Protected, "set", false, true, y.Tenant.ApiKeyId);
        Assert.Equal(6, ofX.Total());
        Assert.DoesNotContain(change.GetProperty("id").GetGuid(), ofX.Items().Select(c => c.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(x.Tenant.ApiKeyId.ToString(), ofY.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(x.Tenant.ApiKeyId.ToString(), (await Rules.ListAsync(y.Http)).GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(y.Tenant.ApiKeyId.ToString(), ofX.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- AC-101 ----

    [Fact]
    public async Task AC101_Through_the_tools_Y_sees_nothing_of_X()
    {
        var (x, y) = await WorldAsync();
        await using var mcpX = await app.McpAsync(x.Tenant.Key);
        await using var mcpY = await app.McpAsync(y.Tenant.Key);

        var rules = await mcpY.OkAsync("rule_list", new { });
        var own = await mcpY.OkAsync("rule_list", new { source = "tenant" });
        var changes = await mcpY.OkAsync("rule_change_list", new { });
        var byKey = await mcpY.OkAsync("rule_change_list", new { key = Rules.NegativeStock });

        AssertSixDefaults(rules);
        Assert.Equal(0, own.Total());
        Assert.Equal((0, 0), (changes.Total(), changes.Items().Length));
        Assert.Equal(0, byKey.Total());
        foreach (var key in Rules.Keys)
            Rules.AssertAtDefault(await mcpY.OkAsync("rule_get", new { key }), key);
        Assert.DoesNotContain(x.Tenant.ApiKeyId.ToString(), rules.GetRawText() + changes.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // T3 through a tool: Y's refusal names Y's value.
        var issue = await mcpY.OkAsync("stock_document_create", Stock.Draft("issue", y.W1, (y.A, 1)));
        var error = await mcpY.ErrorAsync("stock_document_post", new { id = issue.Id() }, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Rules.AssertNames(error, Rules.NegativeStock, false, "lines[0].quantity");
        var noSupplier = await mcpY.ErrorAsync("purchase_order_create", PO.NoPartner(y, (y.A, 1, null, 1m)), "VALIDATION_FAILED", "supplierId");
        Rules.AssertNames(noSupplier, Rules.PurchasePartner, true, "supplierId");

        // A change of Y through a tool is Y's alone; X reads its own six through its tools.
        await mcpY.OkAsync("rule_set", new { key = Rules.Protected, value = true });
        var ofX = await mcpX.OkAsync("rule_list", new { source = "tenant" });
        Assert.Equal(Rules.Keys, ofX.RuleKeys());
        foreach (var rule in ofX.Items())
            Rules.AssertTenantValue(rule, rule.Str("key"), !Rules.Default(rule.Str("key")), x.Tenant.ApiKeyId);
        Assert.Equal(6, (await mcpX.OkAsync("rule_change_list", new { })).Total());
        Assert.Equal(1, (await mcpY.OkAsync("rule_change_list", new { })).Total());
        McpAssert.JsonEqual(await Rules.ChangesAsync(y.Http), await mcpY.OkAsync("rule_change_list", new { }));
    }
}
