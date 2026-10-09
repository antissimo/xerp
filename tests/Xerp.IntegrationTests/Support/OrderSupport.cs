using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The setup of spec 009, section 10: the setup of spec 007 (standard setup, units <c>box</c> and <c>pack</c>,
/// A / box = 12) plus partners <c>SUP</c> (<c>isSupplier</c> only) and <c>CUS</c> (<c>isCustomer</c> only).
/// </summary>
public sealed record OrderSetup(UnitSetup U, JsonElement Sup, JsonElement Cus)
{
    public TestTenant Tenant => U.Tenant;
    public HttpClient Http => U.Http;
    public Guid A => U.A;
    public Guid B => U.B;
    public Guid Service => U.Service;
    public Guid W1 => U.W1;
    public Guid W2 => U.W2;
    public Guid Pcs => U.Pcs;
    public Guid Box => U.Box;
    public Guid Pack => U.Pack;
}

/// <summary>What is common to both kinds of order: the setup, request bodies and the partner master.</summary>
public static class Orders
{
    public const string Date = Stock.Date;

    private static readonly MasterApi P = MasterApi.Partners;

    public static async Task<OrderSetup> SetupAsync(XerpFixture app, TestTenant? tenant = null)
    {
        var u = await Units.SetupAsync(app, tenant);
        return new OrderSetup(u,
            await PartnerAsync(u.Http, "SUP", "Bolt Works Ltd", isSupplier: true, isCustomer: false),
            await PartnerAsync(u.Http, "CUS", "Acme d.o.o.", isSupplier: false, isCustomer: true));
    }

    public static JsonObject NewPartner(string code, string name, bool isSupplier, bool isCustomer, bool isActive = true) => new()
    {
        ["code"] = code, ["name"] = name, ["isSupplier"] = isSupplier, ["isCustomer"] = isCustomer, ["isActive"] = isActive,
    };

    public static Task<JsonElement> PartnerAsync(HttpClient client, string code, string name, bool isSupplier, bool isCustomer, bool isActive = true) =>
        P.CreateAsync(client, NewPartner(code, name, isSupplier, isCustomer, isActive));

    /// <summary>The replace body (spec 004, 4.1) that keeps every value of the given partner.</summary>
    public static JsonObject PartnerBody(JsonElement partner)
    {
        var body = new JsonObject();
        foreach (var field in P.ReplaceFields)
            body[field] = JsonNode.Parse(partner.GetProperty(field).GetRawText());
        return body;
    }

    /// <summary>Replaces a partner with its current values changed by <paramref name="change"/>; asserts <c>200</c>.</summary>
    public static async Task<JsonElement> ChangePartnerAsync(HttpClient client, Guid id, Action<JsonObject> change)
    {
        var body = PartnerBody(await P.GetAsync(client, id));
        change(body);
        return await P.ReplaceAsync(client, id, body);
    }

    public static Task SetPartnerActiveAsync(HttpClient client, Guid id, bool isActive) =>
        ChangePartnerAsync(client, id, b => b["isActive"] = isActive);

    /// <summary>An order line; <paramref name="unit"/> <c>null</c> leaves <c>unitId</c> out of the JSON.</summary>
    public static JsonObject Line(Guid article, JsonNode? quantity, JsonNode? unitPrice, Guid? unit = null)
    {
        var line = new JsonObject { ["articleId"] = article.ToString(), ["quantity"] = quantity, ["unitPrice"] = unitPrice };
        return unit is null ? line : line.With("unitId", unit.Value.ToString());
    }

    public static JsonArray Lines(params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        new(lines.Select(l => (JsonNode)Line(l.Article, l.Quantity, l.Price, l.Unit)).ToArray());

    public static JsonElement[] OrderLines(this JsonElement order) => order.GetProperty("lines").EnumerateArray().ToArray();

    public static decimal Dec(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static decimal Outstanding(this JsonElement line) => line.Dec("outstandingBaseQuantity");

    public static decimal[] LineAmounts(this JsonElement order) => order.OrderLines().Select(l => l.Dec("lineAmount")).ToArray();

    /// <summary>
    /// Stock on hand as it was before orders existed: the pairs whose <c>quantity</c> is not zero. With orders a
    /// pair is also listed for what is on order, so the comparison with the ledger (005/R19) is made on these.
    /// </summary>
    public static async Task<Dictionary<(Guid Article, Guid Warehouse), decimal>> AssertQuantityEqualsLedgerAsync(HttpClient client)
    {
        var list = await Stock.OnHandAsync(client, "?limit=500");
        Assert.Equal(list.Total(), list.Items().Length);
        Assert.All(list.Items(), i => Assert.True(i.Quantity() >= 0m, $"Stock on hand is negative: {i}"));
        var stock = list.Items().Where(i => i.Quantity() != 0m)
            .ToDictionary(i => (i.GetProperty("article").Id(), i.GetProperty("warehouse").Id()), i => i.Quantity());
        var ledger = await Stock.LedgerMapAsync(client);
        Assert.True(stock.Count == ledger.Count && stock.All(p => ledger.TryGetValue(p.Key, out var sum) && sum == p.Value),
            $"Stock on hand differs from the ledger sums:\n[{string.Join(", ", stock)}]\n!=\n[{string.Join(", ", ledger)}]");
        return stock;
    }

    /// <summary>The item of one pair in stock on hand, or <c>null</c> when the pair is not listed.</summary>
    public static async Task<JsonElement?> OnHandItemAsync(HttpClient client, Guid article, Guid warehouse)
    {
        var list = await Stock.OnHandAsync(client, $"?articleId={article}&warehouseId={warehouse}");
        var items = list.Items();
        Assert.True(items.Length <= 1, $"One pair must be one item of stock on hand: {list}");
        Assert.Equal(items.Length, list.Total());
        return items.Length == 0 ? null : items[0];
    }

    /// <summary>A quantity property of the pair in stock on hand, or 0 when the pair is absent.</summary>
    public static async Task<decimal> OnHandAsync(HttpClient client, Guid article, Guid warehouse, string property)
    {
        var item = await OnHandItemAsync(client, article, warehouse);
        if (item is null)
            return 0m;
        Assert.True(item.Value.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number,
            $"The stock-on-hand item has no number '{property}': {item}");
        return value.GetDecimal();
    }
}

/// <summary>
/// One kind of order (ADR-0016): its routes, tool names and the names that differ between a purchase order
/// (spec 009) and its mirror. A test written against this class states a rule of the order mechanics once.
/// </summary>
public sealed class OrderApi
{
    /// <summary>Spec 009: purchase order, received by a <c>receipt</c> with <c>purchaseOrderId</c>.</summary>
    public static readonly OrderApi Purchase = new("purchase-orders", "purchase_order", "supplier", "expectedDate",
        "purchaseOrder", "receipt", "PO-", "SR-", "received", "receipt", "incomingQuantity", consumesStock: false);

    private OrderApi(string resource, string toolPrefix, string partner, string dueDate, string link, string documentType,
        string prefix, string documentPrefix, string done, string progress, string onHand, bool consumesStock)
    {
        Resource = resource;
        Path = $"/api/v1/{resource}";
        ToolPrefix = toolPrefix;
        Partner = partner;
        PartnerId = partner + "Id";
        Role = "is" + char.ToUpperInvariant(partner[0]) + partner[1..];
        DueDate = dueDate;
        Link = link;
        LinkId = link + "Id";
        DocumentType = documentType;
        Prefix = prefix;
        DocumentPrefix = documentPrefix;
        Done = done + "BaseQuantity";
        Progress = progress + "Status";
        OnHand = onHand;
        ConsumesStock = consumesStock;
    }

    public string Resource { get; }
    public string Path { get; }
    public string ToolPrefix { get; }
    /// <summary><c>supplier</c>: the embedded partner summary of the representation.</summary>
    public string Partner { get; }
    /// <summary><c>supplierId</c>: the request field, list filter and <c>errors</c> key.</summary>
    public string PartnerId { get; }
    /// <summary><c>isSupplier</c>: the role the partner must have.</summary>
    public string Role { get; }
    /// <summary><c>expectedDate</c>.</summary>
    public string DueDate { get; }
    /// <summary><c>purchaseOrder</c>: the link property of a stock document.</summary>
    public string Link { get; }
    /// <summary><c>purchaseOrderId</c>: the request field, list filter and <c>errors</c> key of the link.</summary>
    public string LinkId { get; }
    /// <summary><c>receipt</c>: the only stock document type that may carry the link.</summary>
    public string DocumentType { get; }
    /// <summary><c>PO-</c>.</summary>
    public string Prefix { get; }
    /// <summary><c>SR-</c>: the number series of the fulfilment documents.</summary>
    public string DocumentPrefix { get; }
    /// <summary><c>receivedBaseQuantity</c>.</summary>
    public string Done { get; }
    /// <summary><c>receiptStatus</c>: property and list filter.</summary>
    public string Progress { get; }
    /// <summary><c>incomingQuantity</c>: what confirmed orders of this kind add to stock on hand.</summary>
    public string OnHand { get; }
    /// <summary>Whether fulfilment takes goods out of stock (and therefore needs stock to be there).</summary>
    public bool ConsumesStock { get; }

    /// <summary>The stock document types that must not carry this link.</summary>
    public string[] OtherTypes => new[] { "receipt", "issue", "transfer", "count" }.Where(t => t != DocumentType).ToArray();

    public string Tool(string operation) => $"{ToolPrefix}_{operation}";

    public string Number(int n) => $"{Prefix}{n:000000}";

    public string DocumentNumber(int n) => $"{DocumentPrefix}{n:000000}";

    public override string ToString() => Resource;

    // ---- partners ----

    /// <summary>The partner of the setup that has the role this kind of order needs.</summary>
    public JsonElement PartnerOf(OrderSetup s) => Role == "isSupplier" ? s.Sup : s.Cus;

    /// <summary>The partner of the setup that lacks the role.</summary>
    public JsonElement WrongPartnerOf(OrderSetup s) => Role == "isSupplier" ? s.Cus : s.Sup;

    /// <summary>A further partner with the role (and, when asked, the other role too).</summary>
    public Task<JsonElement> NewPartnerAsync(HttpClient client, string code, string name = "Second partner", bool bothRoles = false, bool isActive = true) =>
        Orders.PartnerAsync(client, code, name, Role == "isSupplier" || bothRoles, Role == "isCustomer" || bothRoles, isActive);

    /// <summary>Changes a partner to the other role only (it loses the role this kind of order needs).</summary>
    public Task<JsonElement> RemoveRoleAsync(HttpClient client, Guid partner) =>
        Orders.ChangePartnerAsync(client, partner, b =>
        {
            b["isSupplier"] = Role != "isSupplier";
            b["isCustomer"] = Role != "isCustomer";
        });

    public Task<JsonElement> RestoreRoleAsync(HttpClient client, Guid partner) =>
        Orders.ChangePartnerAsync(client, partner, b =>
        {
            b["isSupplier"] = Role == "isSupplier";
            b["isCustomer"] = Role == "isCustomer";
        });

    // ---- request bodies ----

    /// <summary>A create body with the required properties only.</summary>
    public JsonObject Body(Guid partner, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) => new()
    {
        ["orderDate"] = Orders.Date, [PartnerId] = partner.ToString(), ["warehouseId"] = warehouse.ToString(),
        ["lines"] = Orders.Lines(lines),
    };

    /// <summary>A create body for the setup's partner with the role and <c>W1</c>.</summary>
    public JsonObject Body(OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        Body(PartnerOf(s).Id(), s.W1, lines);

    /// <summary>A replace body: all seven properties present, the three optional ones <c>null</c>.</summary>
    public JsonObject Replacement(Guid partner, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) => new()
    {
        ["orderDate"] = Orders.Date, [DueDate] = null, [PartnerId] = partner.ToString(), ["warehouseId"] = warehouse.ToString(),
        ["reference"] = null, ["note"] = null, ["lines"] = Orders.Lines(lines),
    };

    public JsonObject Replacement(OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        Replacement(PartnerOf(s).Id(), s.W1, lines);

    // ---- operations ----

    public Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Path, body);

    public async Task<JsonElement> CreateAsync(HttpClient client, JsonObject body)
    {
        using var response = await PostAsync(client, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    /// <summary>A draft order for the setup's partner and <c>W1</c>.</summary>
    public Task<JsonElement> DraftAsync(OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        CreateAsync(s.Http, Body(s, lines));

    public Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, JsonObject body) => client.PutAsJsonAsync($"{Path}/{id}", body);

    public async Task<JsonElement> ReplaceAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Path}/{id}");

    public Task<HttpResponseMessage> SendGetAsync(HttpClient client, Guid id) => client.GetAsync($"{Path}/{id}");

    public async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await SendGetAsync(client, id);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public Task<HttpResponseMessage> ByNumberAsync(HttpClient client, string number) =>
        client.GetAsync($"{Path}/by-number/{Uri.EscapeDataString(number)}");

    /// <summary><paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public Task<JsonElement> ListAsync(HttpClient client, string query = "") => Stock.ListAsync(client, Path + query);

    /// <summary><c>POST /{id}/confirm</c>, <c>/close</c> or <c>/reopen</c> without a body, the raw response.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpClient client, Guid id, string action) =>
        client.PostAsync($"{Path}/{id}/{action}", null);

    private async Task<JsonElement> ActAsync(HttpClient client, Guid id, string action)
    {
        using var response = await SendAsync(client, id, action);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public Task<JsonElement> ConfirmAsync(HttpClient client, Guid id) => ActAsync(client, id, "confirm");

    public Task<JsonElement> CloseAsync(HttpClient client, Guid id) => ActAsync(client, id, "close");

    public Task<JsonElement> ReopenAsync(HttpClient client, Guid id) => ActAsync(client, id, "reopen");

    /// <summary>"PO [lines]" (spec 009, section 10): an order for the setup's partner and <c>W1</c>, confirmed.</summary>
    public async Task<JsonElement> OrderedAsync(OrderSetup s, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        await ConfirmAsync(s.Http, (await DraftAsync(s, lines)).Id());

    /// <summary>A confirmed order for another warehouse.</summary>
    public async Task<JsonElement> OrderedAsync(OrderSetup s, Guid warehouse, params (Guid Article, decimal Quantity, Guid? Unit, decimal Price)[] lines) =>
        await ConfirmAsync(s.Http, (await CreateAsync(s.Http, Body(PartnerOf(s).Id(), warehouse, lines))).Id());

    /// <summary>Asserts that a refused operation left the order exactly as it was.</summary>
    public async Task AssertUnchangedAsync(HttpClient client, JsonElement before) =>
        McpAssert.JsonEqual(before, await GetAsync(client, before.Id()), "The order changed");

    // ---- fulfilment: the stock document linked to the order ----

    /// <summary>A line of a linked stock document: article, quantity, the order line it fulfils and optionally a unit.</summary>
    public static JsonObject DocumentLine(Guid article, JsonNode? quantity, JsonNode? orderLineNo, Guid? unit = null)
    {
        var line = Stock.Line(article, quantity).With("orderLineNo", orderLineNo);
        return unit is null ? line : line.With("unitId", unit.Value.ToString());
    }

    public static JsonArray DocumentLines(params (Guid Article, decimal Quantity, int OrderLine, Guid? Unit)[] lines) =>
        new(lines.Select(l => (JsonNode)DocumentLine(l.Article, l.Quantity, l.OrderLine, l.Unit)).ToArray());

    /// <summary>A create body of a stock document of this kind's type linked to an order.</summary>
    public JsonObject Document(Guid warehouse, Guid order, params (Guid Article, decimal Quantity, int OrderLine, Guid? Unit)[] lines) => new()
    {
        ["type"] = DocumentType, ["documentDate"] = Stock.NextDay, ["warehouseId"] = warehouse.ToString(),
        [LinkId] = order.ToString(), ["lines"] = DocumentLines(lines),
    };

    /// <summary>A replace body of a linked draft (the link itself is not part of it).</summary>
    public static JsonObject DocumentReplacement(Guid warehouse, params (Guid Article, decimal Quantity, int OrderLine, Guid? Unit)[] lines) =>
        Stock.Replacement(warehouse).With("lines", DocumentLines(lines));

    public Task<JsonElement> CreateDocumentAsync(HttpClient client, Guid warehouse, Guid order, params (Guid Article, decimal Quantity, int OrderLine, Guid? Unit)[] lines) =>
        Stock.CreateAsync(client, Document(warehouse, order, lines));

    /// <summary>
    /// A draft "(k, n) against the order": one line with the article of order line <paramref name="k"/>, in the
    /// order's warehouse.
    /// </summary>
    public Task<JsonElement> DraftDocumentAsync(HttpClient client, JsonElement order, int k, decimal n, Guid? unit = null) =>
        CreateDocumentAsync(client, order.GetProperty("warehouse").Id(), order.Id(),
            (order.OrderLines()[k - 1].GetProperty("article").Id(), n, k, unit));

    /// <summary>"Receive (k, n) against the order": the draft of <see cref="DraftDocumentAsync"/>, posted (<c>200</c>).</summary>
    public async Task<JsonElement> FulfilAsync(HttpClient client, JsonElement order, int k, decimal n, Guid? unit = null) =>
        await Stock.PostDocumentAsync(client, (await DraftDocumentAsync(client, order, k, n, unit)).Id());

    /// <summary>Creates the draft "(k, n)" and tries to post it; returns the draft's id and the raw response.</summary>
    public async Task<(Guid Id, HttpResponseMessage Response)> TryFulfilAsync(HttpClient client, JsonElement order, int k, decimal n, Guid? unit = null)
    {
        var draft = await DraftDocumentAsync(client, order, k, n, unit);
        return (draft.Id(), await Stock.SendPostAsync(client, draft.Id()));
    }

    /// <summary>
    /// Puts enough of A and B into both warehouses for the fulfilment of a test, where fulfilment takes goods
    /// out of stock. Nothing happens for a kind whose fulfilment brings goods in.
    /// </summary>
    public async Task PrepareStockAsync(OrderSetup s)
    {
        if (!ConsumesStock)
            return;
        foreach (var warehouse in new[] { s.W1, s.W2 })
            await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", warehouse, (s.A, 1000), (s.B, 1000))).Id());
    }

    // ---- progress ----

    /// <summary>"Received(k)": the fulfilled base quantity of an order line.</summary>
    public decimal DoneOf(JsonElement line) => line.Dec(Done);

    public string ProgressOf(JsonElement order) => order.Str(Progress);

    /// <summary>The (fulfilled, outstanding) base quantities of every line of the order as it is now.</summary>
    public async Task<(decimal Done, decimal Outstanding)[]> LinesAsync(HttpClient client, Guid order) =>
        (await GetAsync(client, order)).OrderLines().Select(l => (DoneOf(l), l.Outstanding())).ToArray();

    /// <summary>
    /// Asserts status, progress status and, per line, (fulfilled, outstanding) of the order as it is now, and
    /// that no line is fulfilled above its ordered quantity (R27). Returns the order.
    /// </summary>
    public async Task<JsonElement> AssertProgressAsync(HttpClient client, Guid order, string status, string progress,
        params (decimal Done, decimal Outstanding)[] lines)
    {
        var read = await GetAsync(client, order);
        var actual = read.OrderLines().Select(l => (DoneOf(l), l.Outstanding())).ToArray();
        Assert.True(read.Str("status") == status && ProgressOf(read) == progress && actual.SequenceEqual(lines),
            $"The order is {read.Str("status")} / {ProgressOf(read)} with ({Done}, outstanding) [{string.Join(", ", actual)}]; " +
            $"expected {status} / {progress} with [{string.Join(", ", lines)}]: {read}");
        foreach (var line in read.OrderLines())
            Assert.True(DoneOf(line) >= 0m && DoneOf(line) <= line.Dec("baseQuantity") && line.Outstanding() >= 0m,
                $"An order line is fulfilled outside 0 … baseQuantity: {line}");
        return read;
    }

    /// <summary>"Incoming(A, W1)": this kind's quantity of the pair in stock on hand, or 0 when the pair is absent.</summary>
    public Task<decimal> OnHandAsync(HttpClient client, Guid article, Guid warehouse) =>
        Orders.OnHandAsync(client, article, warehouse, OnHand);

    /// <summary>The documents linked to the order, as the list returns them.</summary>
    public async Task<JsonElement[]> DocumentsAsync(HttpClient client, Guid order, string more = "")
    {
        var list = await Stock.DocumentsAsync(client, $"?{LinkId}={order}&limit=500{more}");
        Assert.Equal(list.Total(), list.Items().Length);
        return list.Items();
    }

    /// <summary>Asserts the <c>{ id, number }</c> link of a stock document or summary to the order.</summary>
    public void AssertLinked(JsonElement document, JsonElement order)
    {
        Assert.True(document.TryGetProperty(Link, out var link) && link.ValueKind == JsonValueKind.Object,
            $"The stock document has no '{Link}' link: {document}");
        Assert.Equal(order.Id(), link.Id());
        Assert.Equal(order.Number(), link.Str("number"));
        Assert.Equal(new[] { "id", "number" }, link.PropertyNames());
    }
}
