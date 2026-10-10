using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-91 and AC-95 to AC-97: a change of a rule touches nothing stored (R15), applies from the next
/// operation (R13) and, under parallel requests, every operation is judged entirely by the values before or
/// entirely by the values after it (R14).
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleChangeEffectTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    // ---- AC-91 ----

    [Fact]
    public async Task AC91_No_Set_and_no_reset_of_any_rule_changes_a_document_an_order_the_ledger_or_stock_on_hand()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        // Data of every kind the six rules judge: posted and draft documents, orders in every state, a
        // reservation beyond stock, a partly received purchase order.
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        await Stock.TransferAsync(http, s.W1, s.W2, s.A, 2);
        var draftIssue = await Stock.CreateAsync(http, "issue", s.W1, (s.A, 50));
        var purchase = await PO.OrderedAsync(s, (s.A, 3, s.Box, 1m), (s.B, 5, null, 2m));
        var receipt = await PO.FulfilAsync(http, purchase, 2, 2);
        var sales = await SO.OrderedAsync(s, (s.A, 20, null, 1m));
        var draftOrder = await PO.DraftAsync(s, (s.B, 1, null, 1m));
        var closed = await SO.CloseAsync(http, (await SO.OrderedAsync(s, (s.B, 1, null, 1m))).Id());
        var count = await Counts.CountAsync(http, s.W2, s.A, 1);
        string[] urls =
        [
            $"{Stock.Documents}?limit=500", $"{Stock.Documents}/{receipt.Id()}", $"{Stock.Documents}/{draftIssue.Id()}",
            $"{Stock.Documents}/{count.Id()}", $"{PO.Path}?limit=500", $"{PO.Path}/{purchase.Id()}", $"{PO.Path}/{draftOrder.Id()}",
            $"{SO.Path}?limit=500", $"{SO.Path}/{sales.Id()}", $"{SO.Path}/{closed.Id()}", $"{Stock.Ledger}?limit=500",
            $"{Stock.OnHand}?limit=500", Balance.StockPath(s.W1), Balance.StockPath(s.W2), Balance.Differences,
        ];
        var before = new Dictionary<string, JsonElement>();
        foreach (var url in urls)
            before[url] = await ReadAsync(http, url);

        async Task AssertNothingChangedAsync(string after)
        {
            foreach (var url in urls)
                McpAssert.JsonEqual(before[url], await ReadAsync(http, url), $"{after}: GET {url} reads differently");
        }

        foreach (var key in Rules.Keys)
        {
            await Rules.SetAsync(http, key, !Rules.Default(key));
            await AssertNothingChangedAsync($"After Set({key}, {!Rules.Default(key)})");
            await Rules.SetAsync(http, key, Rules.Default(key));
            await AssertNothingChangedAsync($"After Set({key}, {Rules.Default(key)})");
            await Rules.ResetAsync(http, key);
            await AssertNothingChangedAsync($"After the reset of {key}");
        }
        // All six at the other value at once.
        foreach (var key in Rules.Keys)
            await Rules.SetAsync(http, key, !Rules.Default(key));
        await AssertNothingChangedAsync("With all six rules at the other value");

        Assert.Equal(6 * 3 + 6, (await Rules.ChangesAsync(http, "?limit=1")).Total());
        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-95 ----

    [Fact]
    public async Task AC95_A_changed_rule_applies_from_the_next_operation_twenty_times_in_a_row()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;

        for (var i = 1; i <= 20; i++)
        {
            await Rules.SetAsync(http, Rules.NegativeStock, true);
            var (allowed, posted) = await Rules.TryIssueAsync(http, s.W1, s.A, 1);
            using (posted)
            {
                var body = await posted.Content.ReadAsStringAsync();
                Assert.True(posted.StatusCode == HttpStatusCode.OK, $"Iteration {i}: the issue after Set(true) was not posted: {(int)posted.StatusCode} {body}");
            }
            Assert.Equal("posted", (await Stock.GetAsync(http, allowed)).Str("status"));

            await Rules.SetAsync(http, Rules.NegativeStock, false);
            var (refusedId, refused) = await Rules.TryIssueAsync(http, s.W1, s.A, 1);
            using (refused)
            {
                var body = await refused.Content.ReadAsStringAsync();
                Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"Iteration {i}: the issue after Set(false) was not refused: {(int)refused.StatusCode} {body}");
                await Rules.InsufficientAsync(refused, "lines[0].quantity");
            }
            await Stock.AssertDraftAsync(http, refusedId);
            Assert.Equal((decimal)-i, await Stock.QuantityAsync(http, s.A, s.W1));
        }

        Assert.Equal(40, (await Rules.ChangesAsync(http, "?limit=1")).Total());
        await Rules.AssertBalancedAsync(http);
    }

    [Fact]
    public async Task AC95_R13_The_same_for_a_reset_and_for_a_rule_that_judges_a_save()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;

        for (var i = 1; i <= 10; i++)
        {
            await Rules.SetAsync(http, Rules.PurchasePartner, false);
            using (var accepted = await PO.PostAsync(http, PO.NoPartner(s, (s.A, 1, null, 1m))))
                await HttpAssert.JsonAsync(accepted, HttpStatusCode.Created);

            await Rules.ResetAsync(http, Rules.PurchasePartner);
            using (var refused = await PO.PostAsync(http, PO.NoPartner(s, (s.A, 1, null, 1m))))
                await Rules.PartnerRequiredAsync(refused, PO);
            Assert.Equal(i, (await PO.ListAsync(http, "?limit=1")).Total());
        }
    }

    // ---- AC-96 ----

    [Fact]
    public async Task AC96_Parallel_issues_and_a_Set_each_issue_is_judged_by_one_value_and_the_balance_holds()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 5);
        var drafts = new List<Guid>();
        for (var i = 0; i < 10; i++)
            drafts.Add((await Stock.CreateAsync(http, "issue", s.W1, (s.A, 1))).Id());

        var posts = drafts.Take(5).Select(id => Stock.SendPostAsync(http, id)).ToList();
        var set = Rules.SendSetAsync(http, Rules.NegativeStock, true);
        posts.AddRange(drafts.Skip(5).Select(id => Stock.SendPostAsync(http, id)));
        var responses = await Task.WhenAll(posts);
        using var setResponse = await set;

        Rules.AssertTenantValue(await HttpAssert.JsonAsync(setResponse, HttpStatusCode.OK), Rules.NegativeStock, true, s.Tenant.ApiKeyId);
        var numbers = new List<string>();
        foreach (var response in responses)
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"A parallel posting answered {(int)response.StatusCode}: {body}");
                if (response.StatusCode == HttpStatusCode.OK)
                    numbers.Add((await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Number()!);
                else
                    // Refused entirely by the value before the change: the rules member says false.
                    await Rules.InsufficientAsync(response, "lines[0].quantity");
            }

        // Stock was 5: at least five issues fit whatever the order of the requests.
        Assert.True(numbers.Count >= 5, $"Only {numbers.Count} of 10 issues were posted with stock 5.");
        Assert.Equal(5m - numbers.Count, await Stock.QuantityAsync(http, s.A, s.W1));
        Assert.Equal(Enumerable.Range(1, numbers.Count).Select(n => $"SI-{n:000000}"), numbers.Order(StringComparer.Ordinal));
        var posted = (await Stock.DocumentsAsync(http, "?type=issue&status=posted&limit=500")).Total();
        Assert.Equal(numbers.Count, posted);
        await Rules.AssertBalancedAsync(http);
        Assert.Equal(0, await Balance.DifferencesAsync(http));
        Assert.Equal(1, (await Rules.ChangesAsync(http)).Total());
    }

    // ---- AC-97 ----

    [Theory]
    [InlineData(Rules.NegativeStock)]
    [InlineData(Rules.PurchasePartner)]
    public async Task AC97_Two_parallel_Sets_of_one_rule_both_succeed_and_the_history_has_both_in_one_order(string key)
    {
        for (var round = 0; round < 5; round++)
        {
            var tenant = await app.NewTenantAsync();
            var other = await Keys.CreateAsync(app, tenant.Client, "bot", "agent");

            var toTrue = Rules.SendSetAsync(tenant.Client, key, true);
            var toFalse = Rules.SendSetAsync(other.Client, key, false);
            using var first = await toTrue;
            using var second = await toFalse;

            Rules.AssertBool(await HttpAssert.JsonAsync(first, HttpStatusCode.OK), "value", true);
            Rules.AssertBool(await HttpAssert.JsonAsync(second, HttpStatusCode.OK), "value", false);
            var rule = await Rules.GetAsync(tenant.Client, key);
            Assert.Equal("tenant", rule.Str("source"));
            var value = rule.GetProperty("value").GetBoolean();

            var history = (await Rules.ChangesAsync(tenant.Client, $"?key={key}")).Items();
            Assert.Equal(2, history.Length);
            var (newest, oldest) = (history[0], history[1]);
            // The result is that of some order of the two (ADR-0020, decision 10).
            Rules.AssertChange(newest, key, "set", oldValue: !value, newValue: value, value ? tenant.ApiKeyId : other.Id);
            Rules.AssertChange(oldest, key, "set", oldValue: Rules.Default(key), newValue: !value, value ? other.Id : tenant.ApiKeyId);
            Assert.Equal(rule.Time("updatedAt"), newest.Time("changedAt"));
            Assert.Equal(value ? tenant.ApiKeyId : other.Id, rule.GetProperty("updatedBy").GetGuid());
        }
    }
}
