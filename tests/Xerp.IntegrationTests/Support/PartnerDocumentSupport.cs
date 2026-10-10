using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The setup of spec 011a, section 10: the setup of spec 009 (standard setup, partners <c>SUP</c> — supplier
/// only — and <c>CUS</c> — customer only) plus <c>SUP2</c>, a second supplier-only partner, and <c>BOTH</c>, a
/// partner with both roles.
/// </summary>
public sealed record PartnerSetup(OrderSetup O, JsonElement Sup2Partner, JsonElement BothPartner)
{
    public TestTenant Tenant => O.Tenant;
    public HttpClient Http => O.Http;
    public StockSetup S => O.U.S;
    public Guid A => O.A;
    public Guid B => O.B;
    public Guid W1 => O.W1;
    public Guid W2 => O.W2;
    public Guid Sup => O.Sup.Id();
    public Guid Cus => O.Cus.Id();
    public Guid Sup2 => Sup2Partner.Id();
    public Guid Both => BothPartner.Id();
}

/// <summary>Shortcuts for the partner of a stock document (spec 011a).</summary>
public static class PartnerDocs
{
    /// <summary>The document date of "Receipt(P)" / "Issue(P)" (section 10).</summary>
    public const string Date = "2026-10-10";

    public static readonly MasterApi P = MasterApi.Partners;

    public static async Task<PartnerSetup> SetupAsync(XerpFixture app, TestTenant? tenant = null)
    {
        var o = await Orders.SetupAsync(app, tenant);
        return new PartnerSetup(o,
            await Orders.PartnerAsync(o.Http, "SUP2", "Second supplier", isSupplier: true, isCustomer: false),
            await Orders.PartnerAsync(o.Http, "BOTH", "Supplier and customer", isSupplier: true, isCustomer: true));
    }

    // ---- request bodies ----

    /// <summary>A create body for <c>W1</c>, dated 2026-10-10, with one line of A and the given <c>partnerId</c> value.</summary>
    public static JsonObject Body(PartnerSetup s, string type, JsonNode? partnerId, decimal quantity = 1) =>
        Stock.Draft(type, s.W1, (s.A, quantity)).With("documentDate", Date).With("partnerId", partnerId);

    /// <summary>"Receipt(P)": the create body.</summary>
    public static JsonObject Receipt(PartnerSetup s, Guid partner, decimal quantity = 1) => Body(s, "receipt", partner.ToString(), quantity);

    /// <summary>"Issue(P)": the create body.</summary>
    public static JsonObject Issue(PartnerSetup s, Guid partner, decimal quantity = 1) => Body(s, "issue", partner.ToString(), quantity);

    /// <summary>A replace body of a receipt or an issue for <c>W1</c> with one line of A and the given <c>partnerId</c> value.</summary>
    public static JsonObject Replacement(PartnerSetup s, JsonNode? partnerId, decimal quantity = 1) =>
        Stock.Replacement(s.W1, (s.A, quantity)).With("documentDate", Date).With("partnerId", partnerId);

    public static JsonObject Replacement(PartnerSetup s, Guid partner, decimal quantity = 1) => Replacement(s, partner.ToString(), quantity);

    // ---- documents ----

    /// <summary>A draft "Receipt(P)".</summary>
    public static Task<JsonElement> ReceiptAsync(PartnerSetup s, Guid partner, decimal quantity = 1) =>
        Stock.CreateAsync(s.Http, Receipt(s, partner, quantity));

    /// <summary>A draft "Issue(P)".</summary>
    public static Task<JsonElement> IssueAsync(PartnerSetup s, Guid partner, decimal quantity = 1) =>
        Stock.CreateAsync(s.Http, Issue(s, partner, quantity));

    /// <summary>A posted "Receipt(P)".</summary>
    public static async Task<JsonElement> PostedReceiptAsync(PartnerSetup s, Guid partner, decimal quantity = 1) =>
        await Stock.PostDocumentAsync(s.Http, (await ReceiptAsync(s, partner, quantity)).Id());

    /// <summary>The list summary of one document, found on the unfiltered list.</summary>
    public static async Task<JsonElement> SummaryAsync(HttpClient client, Guid id)
    {
        var all = await Balance.AllAsync(client, Stock.Documents);
        return Assert.Single(all, d => d.Id() == id);
    }

    /// <summary>The ids of the documents <c>GET /stock-documents?partnerId=…</c> returns, read to the last page, sorted.</summary>
    public static async Task<Guid[]> IdsAsync(HttpClient client, string query)
    {
        var list = await Stock.DocumentsAsync(client, query + (query.Contains("limit=") ? "" : "&limit=200"));
        Assert.Equal(list.Total(), list.Items().Length);
        return Set(list.Items());
    }

    public static Guid[] Set(params JsonElement[] documents) => documents.Select(d => d.Id()).Order().ToArray();

    // ---- assertions ----

    /// <summary><c>partner</c> is exactly <c>{ id, code, name }</c> of the partner as it is now.</summary>
    public static void AssertPartner(JsonElement documentOrSummary, JsonElement partner)
    {
        Assert.True(documentOrSummary.TryGetProperty("partner", out var actual), $"The stock document has no 'partner': {documentOrSummary}");
        Assert.True(actual.ValueKind == JsonValueKind.Object, $"'partner' is not an object: {documentOrSummary}");
        Assert.Equal(new[] { "code", "id", "name" }, actual.PropertyNames().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(partner.Id(), actual.Id());
        Assert.Equal(partner.Str("code"), actual.Str("code"));
        Assert.Equal(partner.Str("name"), actual.Str("name"));
    }

    /// <summary><c>partner</c> is present and <c>null</c>.</summary>
    public static void AssertNoPartner(JsonElement documentOrSummary)
    {
        Assert.True(documentOrSummary.TryGetProperty("partner", out var actual), $"The stock document has no 'partner': {documentOrSummary}");
        Assert.True(actual.ValueKind == JsonValueKind.Null, $"'partner' is not null: {documentOrSummary}");
    }

    /// <summary>The document read by id and its list summary both show the partner (or both show none).</summary>
    public static async Task<JsonElement> AssertPartnerEverywhereAsync(HttpClient client, Guid id, JsonElement? partner)
    {
        var read = await Stock.GetAsync(client, id);
        var summary = await SummaryAsync(client, id);
        foreach (var representation in new[] { read, summary })
        {
            if (partner is { } p)
                AssertPartner(representation, p);
            else
                AssertNoPartner(representation);
        }
        McpAssert.JsonEqual(read.GetProperty("partner"), summary.GetProperty("partner"), "Summary and document differ in 'partner'");
        return read;
    }

    /// <summary>A <c>409</c> problem with the code whose <c>errors</c> has exactly the given keys.</summary>
    public static async Task<JsonElement> ConflictExactlyAsync(HttpResponseMessage response, string code, params string[] keys)
    {
        var problem = await Stock.ConflictAsync(response, code, keys);
        Assert.Equal(keys.Order(StringComparer.Ordinal).ToArray(), McpAssert.ErrorKeys(problem));
        return problem;
    }

    /// <summary>Nothing of a refused create was stored: the tenant has exactly this many stock documents.</summary>
    public static async Task AssertDocumentCountAsync(HttpClient client, int expected) =>
        Assert.Equal(expected, (await Stock.DocumentsAsync(client)).Total());

    /// <summary>"Balanced" (011 §10) for A and B in W1, W2 and the default warehouse.</summary>
    public static async Task<Dictionary<(Guid Article, Guid Warehouse), decimal>> AssertBalancedAsync(PartnerSetup s, string when = "") =>
        await Balance.AssertBalancedAsync(s.S, when, await Balance.DefaultIdAsync(s.Http));
}
