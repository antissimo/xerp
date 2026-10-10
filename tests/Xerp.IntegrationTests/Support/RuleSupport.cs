using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The conventions of spec 012, section 10: "Set(key, v)", "Issue n of A from W1", "refused by (CODE, key, v)"
/// and a "Balanced" that also holds for pairs below zero (R19) — all through HTTP. A rule is a boolean switch.
/// </summary>
public static class Rules
{
    public const string Path = "/api/v1/rules";
    public const string Changes = "/api/v1/rule-changes";

    public const string OverReceipt = "purchase.overReceiptAllowed";
    public const string PurchasePartner = "purchase.partnerRequired";
    public const string OverDelivery = "sales.overDeliveryAllowed";
    public const string SalesPartner = "sales.partnerRequired";
    public const string Protected = "sales.reservedStockProtected";
    public const string NegativeStock = "stock.negativeStockAllowed";

    /// <summary>The six keys of the registry in the order of the list (R2: by key, ordinal).</summary>
    public static readonly string[] Keys = [OverReceipt, PurchasePartner, OverDelivery, SalesPartner, Protected, NegativeStock];

    /// <summary>The default of a rule (section 6.1): only the two partner rules are <c>true</c>.</summary>
    public static bool Default(string key) => key is PurchasePartner or SalesPartner;

    /// <summary>The nine properties of a rule (section 4), sorted. There is no <c>type</c> and no <c>allowed</c>.</summary>
    public static readonly string[] Representation =
        ["default", "description", "group", "key", "name", "source", "updatedAt", "updatedBy", "value"];

    /// <summary>The properties of a change (section 4), sorted.</summary>
    public static readonly string[] ChangeRepresentation = ["action", "changedAt", "changedBy", "id", "key", "newValue", "oldValue"];

    /// <summary><c>purchase.partnerRequired</c> / <c>sales.partnerRequired</c>.</summary>
    public static string PartnerRule(OrderApi o) => o == OrderApi.Purchase ? PurchasePartner : SalesPartner;

    /// <summary><c>purchase.overReceiptAllowed</c> / <c>sales.overDeliveryAllowed</c>.</summary>
    public static string OverRule(OrderApi o) => o == OrderApi.Purchase ? OverReceipt : OverDelivery;

    public static string PathOf(string key) => $"{Path}/{key}";

    public static string ResetPath(string key) => $"{Path}/{key}/reset";

    // ---- operations ----

    /// <summary><c>PUT /rules/{key}</c> with <c>{ "value": v }</c>, the raw response.</summary>
    public static Task<HttpResponseMessage> SendSetAsync(HttpClient client, string key, bool value) =>
        client.PutAsJsonAsync(PathOf(key), new JsonObject { ["value"] = value });

    /// <summary><c>PUT /rules/{key}</c> with a body given as JSON text, the raw response.</summary>
    public static Task<HttpResponseMessage> SendSetRawAsync(HttpClient client, string key, string json) =>
        client.PutAsync(PathOf(key), HttpAssert.Raw(json));

    /// <summary>"Set(key, v)": <c>PUT /rules/{key}</c> answered <c>200</c>; returns the rule.</summary>
    public static async Task<JsonElement> SetAsync(HttpClient client, string key, bool value)
    {
        using var response = await SendSetAsync(client, key, value);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<HttpResponseMessage> SendResetAsync(HttpClient client, string key) => client.PostAsync(ResetPath(key), null);

    /// <summary><c>POST /rules/{key}/reset</c> answered <c>200</c>; returns the rule.</summary>
    public static async Task<JsonElement> ResetAsync(HttpClient client, string key)
    {
        using var response = await SendResetAsync(client, key);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> GetAsync(HttpClient client, string key)
    {
        using var response = await client.GetAsync(PathOf(key));
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><c>GET /rules</c>; <paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public static Task<JsonElement> ListAsync(HttpClient client, string query = "") => Stock.ListAsync(client, Path + query);

    /// <summary><c>GET /rule-changes</c>; <paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public static Task<JsonElement> ChangesAsync(HttpClient client, string query = "") => Stock.ListAsync(client, Changes + query);

    public static string[] RuleKeys(this JsonElement list) => list.Items().Select(i => i.Str("key")).ToArray();

    public static DateTimeOffset Time(this JsonElement element, string property) => element.GetProperty(property).GetDateTimeOffset();

    // ---- values ----

    /// <summary>The property is the JSON literal <c>true</c> or <c>false</c> — not a string, a number or null.</summary>
    public static void AssertBool(JsonElement holder, string property, bool expected)
    {
        Assert.True(holder.TryGetProperty(property, out var actual), $"'{property}' is missing: {holder}");
        Assert.True(actual.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False),
            $"'{property}' is {actual.GetRawText()}, expected the JSON boolean {(expected ? "true" : "false")}: {holder}");
    }

    /// <summary>A rule with the tenant's own value (R5), last changed by the given key.</summary>
    public static void AssertTenantValue(JsonElement rule, string key, bool value, Guid by)
    {
        Assert.Equal(Representation, rule.PropertyNames());
        Assert.Equal(key, rule.Str("key"));
        AssertBool(rule, "value", value);
        AssertBool(rule, "default", Default(key));
        Assert.Equal("tenant", rule.Str("source"));
        Assert.Equal(by, rule.GetProperty("updatedBy").GetGuid());
        Assert.Equal(JsonValueKind.String, rule.GetProperty("updatedAt").ValueKind);
    }

    /// <summary>A rule at its default (R3, R9): <c>value == default</c>, <c>source == "default"</c>.</summary>
    public static void AssertAtDefault(JsonElement rule, string key)
    {
        Assert.Equal(Representation, rule.PropertyNames());
        Assert.Equal(key, rule.Str("key"));
        AssertBool(rule, "default", Default(key));
        AssertBool(rule, "value", Default(key));
        Assert.Equal("default", rule.Str("source"));
    }

    /// <summary>One item of the history (R10): the action, the effective values before and after, and who.</summary>
    public static void AssertChange(JsonElement change, string key, string action, bool oldValue, bool newValue, Guid by)
    {
        Assert.Equal(ChangeRepresentation, change.PropertyNames());
        Assert.Equal((key, action), (change.Str("key"), change.Str("action")));
        AssertBool(change, "oldValue", oldValue);
        AssertBool(change, "newValue", newValue);
        Assert.Equal(by, change.GetProperty("changedBy").GetGuid());
        Assert.Equal(7, change.GetProperty("id").GetGuid().Version);
        Assert.Equal(JsonValueKind.String, change.GetProperty("changedAt").ValueKind);
    }

    // ---- "refused by" ----

    /// <summary>The keys of <c>errors</c> in the order of the document.</summary>
    public static string[] ErrorKeysInOrder(JsonElement error) =>
        error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            ? errors.EnumerateObject().Select(p => p.Name).ToArray()
            : [];

    /// <summary>
    /// "Refused by (CODE, key, v)" for a problem document or a tool error whose code is already asserted: a
    /// non-empty <c>errors</c>, and <c>rules</c> with exactly one element whose <c>key</c> and <c>value</c> are
    /// those and whose <c>fields</c> equal the keys of <c>errors</c>, in their order (section 4, R38). When
    /// <paramref name="exactErrorKeys"/> are given, <c>errors</c> has exactly those keys.
    /// </summary>
    public static JsonElement AssertNames(JsonElement error, string key, bool value, params string[] exactErrorKeys)
    {
        var errorKeys = ErrorKeysInOrder(error);
        Assert.True(errorKeys.Length > 0, $"A refusal by rule '{key}' must have a non-empty 'errors': {error}");
        if (exactErrorKeys.Length > 0)
            Assert.Equal(exactErrorKeys.Order(StringComparer.Ordinal), errorKeys.Order(StringComparer.Ordinal));
        var fields = Fields(AssertRule(error, key, value));
        Assert.True(fields.SequenceEqual(errorKeys),
            $"rules[0].fields is [{string.Join(", ", fields)}], the keys of errors are [{string.Join(", ", errorKeys)}]: {error}");
        return error;
    }

    /// <summary><c>rules</c> has exactly one element, <c>{ key, value, fields }</c>, with that key and value; returns it.</summary>
    public static JsonElement AssertRule(JsonElement error, string key, bool value)
    {
        Assert.True(error.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array,
            $"The refusal does not name a rule (no 'rules' array); expected '{key}': {error}");
        Assert.True(rules.GetArrayLength() == 1, $"'rules' must have exactly one element, '{key}': {rules}");
        var rule = rules[0];
        Assert.Equal(new[] { "fields", "key", "value" }, rule.PropertyNames());
        Assert.Equal(key, rule.Str("key"));
        AssertBool(rule, "value", value);
        Assert.Equal(JsonValueKind.Array, rule.GetProperty("fields").ValueKind);
        return rule;
    }

    public static string[] Fields(JsonElement rule) => rule.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray();

    /// <summary>A refusal no configurable rule caused has no <c>rules</c> member — not an empty array (R39).</summary>
    public static JsonElement AssertNamesNoRule(JsonElement error)
    {
        Assert.False(error.TryGetProperty("rules", out var rules), $"No rule refused, yet the error has 'rules' {rules}: {error}");
        return error;
    }

    /// <summary>The response is "refused by (CODE, key, v)" with the given status; returns the problem.</summary>
    public static async Task<JsonElement> RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code,
        string key, bool value, params string[] exactErrorKeys) =>
        AssertNames(await HttpAssert.ProblemAsync(response, status, code), key, value, exactErrorKeys);

    /// <summary>A <c>409</c> "refused by (CODE, key, v)".</summary>
    public static Task<JsonElement> ConflictAsync(HttpResponseMessage response, string code, string key, bool value, params string[] exactErrorKeys) =>
        RefusedAsync(response, HttpStatusCode.Conflict, code, key, value, exactErrorKeys);

    /// <summary><c>409 INSUFFICIENT_STOCK</c> refused by (<c>stock.negativeStockAllowed</c>, <c>false</c>).</summary>
    public static Task<JsonElement> InsufficientAsync(HttpResponseMessage response, params string[] exactErrorKeys) =>
        ConflictAsync(response, "INSUFFICIENT_STOCK", NegativeStock, false, exactErrorKeys);

    /// <summary><c>409 STOCK_RESERVED</c> refused by (<c>sales.reservedStockProtected</c>, <c>true</c>).</summary>
    public static Task<JsonElement> ReservedAsync(HttpResponseMessage response, params string[] exactErrorKeys) =>
        ConflictAsync(response, "STOCK_RESERVED", Protected, true, exactErrorKeys);

    /// <summary><c>409 QUANTITY_EXCEEDS_ORDER</c> refused by the kind's over-fulfilment rule at <c>false</c>.</summary>
    public static Task<JsonElement> ExceedsAsync(HttpResponseMessage response, OrderApi o, params string[] exactErrorKeys) =>
        ConflictAsync(response, "QUANTITY_EXCEEDS_ORDER", OverRule(o), false, exactErrorKeys);

    /// <summary><c>400 VALIDATION_FAILED</c> with exactly the partner key, refused by the kind's partner rule at <c>true</c>.</summary>
    public static Task<JsonElement> PartnerRequiredAsync(HttpResponseMessage response, OrderApi o) =>
        RefusedAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED", PartnerRule(o), true, o.PartnerId);

    // ---- movements ----

    /// <summary>A draft unlinked issue with one line and the raw response of posting it.</summary>
    public static async Task<(Guid Id, HttpResponseMessage Response)> TryIssueAsync(HttpClient client, Guid warehouse, Guid article, decimal quantity)
    {
        var draft = await Stock.CreateAsync(client, "issue", warehouse, (article, quantity));
        return (draft.Id(), await Stock.SendPostAsync(client, draft.Id()));
    }

    /// <summary>"Available(A, W)": <c>availableQuantity</c> of the pair in stock on hand, or 0 when it is absent.</summary>
    public static Task<decimal> AvailableAsync(HttpClient client, Guid article, Guid warehouse) =>
        Orders.OnHandAsync(client, article, warehouse, "availableQuantity");

    // ---- orders without a partner ----

    /// <summary>A create body of an order for <c>W1</c> without the partner property (R32: the same as <c>null</c>).</summary>
    public static JsonObject NoPartner(this OrderApi o, OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        o.Body(s, lines).Without(o.PartnerId);

    /// <summary>A replace body of an order for <c>W1</c> with the partner property <c>null</c>.</summary>
    public static JsonObject NoPartnerReplacement(this OrderApi o, OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        o.Replacement(s, lines).With(o.PartnerId, null);

    /// <summary>The order's partner property is present and <c>null</c> (section 4, "Changes to existing operations").</summary>
    public static void AssertNoPartner(OrderApi o, JsonElement order) => JsonBody.AssertNull(order, o.Partner);

    // ---- Balanced, negative pairs included ----

    /// <summary>
    /// "Balanced" as R19 states it for either value of <c>stock.negativeStockAllowed</c>: for every pair, stock on
    /// hand == sum of the ledger == the quantity on the warehouse's stock list — below zero included — and verify
    /// (<c>GET /stock-balance-differences</c>) is empty. Returns the ledger sum per pair.
    /// </summary>
    public static async Task<Dictionary<(Guid Article, Guid Warehouse), decimal>> AssertBalancedAsync(HttpClient client, string when = "")
    {
        var ledger = Balance.Sums(await Balance.LedgerAsync(client));
        var onHand = (await Balance.AllAsync(client, Stock.OnHand))
            .ToDictionary(i => (i.GetProperty("article").Id(), i.GetProperty("warehouse").Id()), i => i.Quantity());
        foreach (var (pair, quantity) in onHand)
            Assert.True(quantity == ledger.GetValueOrDefault(pair),
                $"{when}: stock on hand {quantity} of {pair} is not the ledger sum {ledger.GetValueOrDefault(pair)}.");
        foreach (var (pair, sum) in ledger.Where(p => p.Value != 0m))
            Assert.True(onHand.TryGetValue(pair, out var quantity) && quantity == sum,
                $"{when}: the ledger sums to {sum} for {pair}, stock on hand does not list that quantity.");
        var warehouses = await Balance.AllAsync(client, MasterApi.Warehouses.Path);
        foreach (var warehouse in warehouses.Select(w => w.Id()))
            foreach (var item in await Balance.StockListAsync(client, warehouse))
            {
                var pair = (item.GetProperty("article").Id(), warehouse);
                Assert.True(item.Quantity() == ledger.GetValueOrDefault(pair),
                    $"{when}: the stock list shows {item.Quantity()} for {pair}, the ledger sums to {ledger.GetValueOrDefault(pair)}.");
            }
        var differences = await Balance.DifferenceListAsync(client);
        Assert.True(differences.Total() == 0 && differences.Items().Length == 0, $"{when}: verify reports differences: {differences}");
        return ledger;
    }

    // ---- the defaults (AC-40, AC-42) ----

    /// <summary>
    /// AC-40: in this tenant the six rules behave as specs 001–011a say — an issue above stock, a receipt and a
    /// delivery above their order and an order without a partner are refused, and an unlinked issue of reserved
    /// goods posts. With <paramref name="named"/> (AC-42) each of the five refusals is "refused by" its rule at
    /// its default. The setup must be fresh: no stock of B, no order.
    /// </summary>
    public static async Task AssertDefaultBehaviourAsync(OrderSetup s, bool named)
    {
        var http = s.Http;
        var (purchase, sales) = (OrderApi.Purchase, OrderApi.Sales);

        async Task RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code, string key, bool value, string errorKey)
        {
            using (response)
            {
                var problem = await HttpAssert.ProblemAsync(response, status, code);
                Assert.Equal(new[] { errorKey }, McpAssert.ErrorKeys(problem));
                if (named)
                    AssertNames(problem, key, value, errorKey);
            }
        }

        // An issue above stock.
        await Stock.ReceiveAsync(http, s.W2, s.B, 4);
        var (issue, aboveStock) = await TryIssueAsync(http, s.W2, s.B, 5);
        await RefusedAsync(aboveStock, HttpStatusCode.Conflict, "INSUFFICIENT_STOCK", NegativeStock, false, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, issue);
        Assert.Equal(4m, await Stock.QuantityAsync(http, s.B, s.W2));

        // A receipt above its order.
        var po = await purchase.OrderedAsync(s, (s.A, 10, null, 1m));
        var (receipt, aboveOrder) = await purchase.TryFulfilAsync(http, po, 1, 11);
        await RefusedAsync(aboveOrder, HttpStatusCode.Conflict, "QUANTITY_EXCEEDS_ORDER", OverReceipt, false, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, receipt);
        await purchase.FulfilAsync(http, po, 1, 10);

        // A delivery above its order (stock is there: 100 of A in W1).
        await Stock.ReceiveAsync(http, s.W1, s.A, 90);
        var so = await sales.OrderedAsync(s, (s.A, 10, null, 1m));
        var (delivery, aboveSales) = await sales.TryFulfilAsync(http, so, 1, 11);
        await RefusedAsync(aboveSales, HttpStatusCode.Conflict, "QUANTITY_EXCEEDS_ORDER", OverDelivery, false, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, delivery);

        // An unlinked issue of goods a confirmed order reserves: posted (010/R17).
        Assert.Equal(90m, await AvailableAsync(http, s.A, s.W1));
        await Stock.IssueAsync(http, s.W1, s.A, 95);
        Assert.Equal((5m, -5m), (await Stock.QuantityAsync(http, s.A, s.W1), await AvailableAsync(http, s.A, s.W1)));

        // An order without a partner.
        foreach (var o in new[] { purchase, sales })
        {
            var before = (await o.ListAsync(http)).Total();
            await RefusedAsync(await o.PostAsync(http, o.NoPartner(s, (s.A, 1, null, 1m))),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED", PartnerRule(o), true, o.PartnerId);
            Assert.Equal(before, (await o.ListAsync(http)).Total());
        }

        await AssertBalancedAsync(http, "after the default behaviour");
    }
}
