using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-10, AC-11 (inherited behaviour, smoke) and AC-20 to AC-28 (drafts): a draft order is a freely
/// editable form with prices and amounts that orders nothing. Written once for every kind of order (ADR-0016);
/// the concrete classes are in <c>PurchaseOrderTests.cs</c>.
/// </summary>
public abstract class OrderDraftTests(XerpFixture app, OrderApi o)
{
    private static readonly string[] OrderProperties =
    [
        "id", "status", "number", "orderDate", "warehouse", "reference", "note", "lines", "totalAmount",
        "createdAt", "updatedAt", "createdBy", "updatedBy", "confirmedAt", "confirmedBy", "closedAt", "closedBy",
    ];

    private static readonly string[] LineProperties =
    [
        "lineNo", "article", "unit", "quantity", "unitPrice", "lineAmount", "factor", "baseUnit", "baseQuantity",
        "outstandingBaseQuantity",
    ];

    private async Task AssertNothingCreatedAsync(OrderSetup s)
    {
        var list = await o.ListAsync(s.Http);
        Assert.True(list.Total() == 0 && list.Items().Length == 0, $"A refused request created an order: {list}");
    }

    private static void AssertSummary(JsonElement master, JsonElement summary)
    {
        Assert.Equal(master.Id(), summary.Id());
        Assert.Equal(master.Str("code"), summary.Str("code"));
        Assert.Equal(master.Str("name"), summary.Str("name"));
        Assert.Equal(new[] { "code", "id", "name" }, summary.PropertyNames());
    }

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_Order_routes_need_a_tenant_credential()
    {
        var id = Guid.CreateVersion7();
        using var anonymous = app.Anonymous();
        using var admin = app.Admin();

        foreach (var (client, forbidden) in new[] { (anonymous, false), (admin, true) })
        {
            using var list = await client.GetAsync(o.Path);
            using var create = await o.PostAsync(client, new JsonObject());
            using var confirm = await o.SendAsync(client, id, "confirm");
            using var close = await o.SendAsync(client, id, "close");
            using var reopen = await o.SendAsync(client, id, "reopen");
            foreach (var response in new[] { list, create, confirm, close, reopen })
                if (forbidden)
                    await HttpAssert.ForbiddenAsync(response);
                else
                    await HttpAssert.UnauthenticatedAsync(response);
        }
    }

    // ---- AC-11 ----

    [Theory]
    [InlineData("status", "\"confirmed\"")]
    [InlineData("number", "\"X\"")]
    [InlineData("totalAmount", "1")]
    [InlineData("tenantId", "\"0199c0de-0000-7000-8000-000000000001\"")]
    public async Task AC11_E4_Create_with_an_unknown_property_is_rejected(string property, string json)
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s, (s.A, 10, null, 2.5m)).With(property, JsonNode.Parse(json));

        using var response = await o.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response);
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    [InlineData("lineNo", "1")]
    [InlineData("lineAmount", "25")]
    [InlineData("discount", "0")]
    public async Task AC11_E4_A_line_with_an_unknown_property_is_rejected(string property, string json)
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s, (s.A, 10, null, 2.5m));
        body["lines"]![0]![property] = JsonNode.Parse(json);

        using var response = await o.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response);
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    [InlineData("?foo=1", "foo")]
    [InlineData("?status=open", "status")]
    public async Task AC11_Unknown_query_parameter_or_filter_value_is_rejected(string query, string errorKey)
    {
        var s = await Orders.SetupAsync(app);

        using var response = await s.Http.GetAsync(o.Path + query);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("close")]
    [InlineData("reopen")]
    public async Task AC11_E18_An_action_with_a_query_parameter_is_rejected(string action)
    {
        var s = await Orders.SetupAsync(app);
        var order = await o.DraftAsync(s, (s.A, 10, null, 2.5m));
        if (action != "confirm")
            order = await o.ConfirmAsync(s.Http, order.Id());
        if (action == "reopen")
            order = await o.CloseAsync(s.Http, order.Id());

        using var response = await s.Http.PostAsync($"{o.Path}/{order.Id()}/{action}?x=1", null);

        await HttpAssert.ValidationAsync(response, "x");
        await o.AssertUnchangedAsync(s.Http, order);
    }

    // ---- AC-20, AC-21 ----

    [Fact]
    public async Task AC20_Create_returns_a_draft_with_summaries_amounts_and_get_returns_the_same()
    {
        var s = await Orders.SetupAsync(app);

        using var response = await o.PostAsync(s.Http, o.Body(s, (s.A, 10, null, 2.5m)));

        var order = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        foreach (var property in (string[])[.. OrderProperties, o.DueDate, o.Partner, o.Progress])
            Assert.True(order.TryGetProperty(property, out _), $"Property '{property}' is missing: {order}");
        Assert.False(order.TryGetProperty("tenantId", out _));
        Assert.Equal("draft", order.Str("status"));
        Assert.Equal(Orders.Date, order.Str("orderDate"));
        JsonBody.AssertNull(order, "number", o.DueDate, "reference", "note", "confirmedAt", "confirmedBy", "closedAt", "closedBy");
        AssertSummary(o.PartnerOf(s), order.GetProperty(o.Partner));
        AssertSummary(s.U.S.Warehouse1, order.GetProperty("warehouse"));
        Assert.Equal("none", o.ProgressOf(order));
        var line = Assert.Single(order.OrderLines());
        foreach (var property in (string[])[.. LineProperties, o.Done])
            Assert.True(line.TryGetProperty(property, out _), $"Line property '{property}' is missing: {line}");
        Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
        AssertSummary(s.U.S.ArticleA, line.GetProperty("article"));
        Units.AssertLine(line, "pcs", quantity: 10m, factor: 1m, baseQuantity: 10m);
        Assert.Equal((2.5m, 25m), (line.Dec("unitPrice"), line.Dec("lineAmount")));
        Assert.Equal((0m, 0m), (o.DoneOf(line), line.Outstanding()));
        Assert.Equal(25m, order.Dec("totalAmount"));
        Assert.Equal(s.Tenant.ApiKeyId, order.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, order.GetProperty("updatedBy").GetGuid());
        Assert.EndsWith($"{o.Path}/{order.Id()}", response.Headers.Location?.ToString());
        McpAssert.JsonEqual(order, await o.GetAsync(s.Http, order.Id()));
    }

    [Fact]
    public async Task AC21_A_draft_orders_nothing_and_cannot_be_fulfilled()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        var onHand = await Stock.OnHandAsync(s.Http);
        Assert.True(onHand.Total() == 0 && onHand.Items().Length == 0, $"A draft order appears in stock on hand: {onHand}");
        Assert.Equal(0m, await o.OnHandAsync(s.Http, s.A, s.W1));

        using var refused = await Stock.PostAsync(s.Http, o.Document(s.W1, draft.Id(), (s.A, 4, 1, null)));

        var problem = await Stock.ConflictAsync(refused, "ORDER_NOT_OPEN", o.LinkId);
        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(problem));
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
        await o.AssertUnchangedAsync(s.Http, draft);
    }

    // ---- AC-22 ----

    [Fact]
    public async Task AC22_Lines_are_numbered_carry_their_unit_and_amounts_are_rounded_per_line()
    {
        var s = await Orders.SetupAsync(app);

        var order = await o.DraftAsync(s, (s.A, 5, s.Box, 30m), (s.B, 2.5m, null, 0.333333m), (s.A, 1, null, 0m), (s.B, 0.5m, null, 0.01m));

        var lines = order.OrderLines();
        Assert.Equal([1, 2, 3, 4], lines.Select(l => l.GetProperty("lineNo").GetInt32()));
        Assert.Equal([s.A, s.B, s.A, s.B], lines.Select(l => l.GetProperty("article").Id()));
        Units.AssertLine(lines[0], "box", quantity: 5m, factor: 12m, baseQuantity: 60m);
        Units.AssertLine(lines[1], "pcs", quantity: 2.5m, factor: 1m, baseQuantity: 2.5m);
        Assert.Equal([30m, 0.333333m, 0m, 0.01m], lines.Select(l => l.Dec("unitPrice")));
        Assert.Equal([150m, 0.83m, 0m, 0.01m], order.LineAmounts());
        Assert.Equal(150.84m, order.Dec("totalAmount"));
        Assert.Equal("none", o.ProgressOf(order));
    }

    [Theory]
    // E2: half away from zero, to 2 decimal places.
    [InlineData("2.5", "0.333333", "0.83")]
    [InlineData("0.5", "0.01", "0.01")]
    [InlineData("0.5", "0.03", "0.02")]
    [InlineData("1", "0.004", "0")]
    [InlineData("1", "0.005", "0.01")]
    [InlineData("3", "0.115", "0.35")]
    // E1 accepted values (subject to R9).
    [InlineData("1", "0.000001", "0")]
    [InlineData("1", "999999999.999999", "1000000000")]
    [InlineData("99999.99", "100000", "9999999000")]
    [InlineData("0.000001", "999999999.999999", "1000")]
    public async Task AC22_E2_R9_The_line_amount_is_quantity_times_price_rounded_half_away_from_zero(string quantity, string price, string amount)
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s);
        body["lines"] = new JsonArray(Orders.Line(s.B, JsonNode.Parse(quantity), JsonNode.Parse(price)));

        var order = await o.CreateAsync(s.Http, body);

        Assert.Equal(decimal.Parse(amount), Assert.Single(order.OrderLines()).Dec("lineAmount"));
        Assert.Equal(decimal.Parse(amount), order.Dec("totalAmount"));
    }

    [Fact]
    public async Task AC22_R9_The_total_is_the_sum_of_the_rounded_line_amounts()
    {
        // Three lines of 0.5 x 0.01 round to 0.01 each: the total is 0.03, not round(0.015) = 0.02.
        var s = await Orders.SetupAsync(app);

        var order = await o.DraftAsync(s, (s.B, 0.5m, null, 0.01m), (s.B, 0.5m, null, 0.01m), (s.B, 0.5m, null, 0.01m));

        Assert.Equal([0.01m, 0.01m, 0.01m], order.LineAmounts());
        Assert.Equal(0.03m, order.Dec("totalAmount"));
    }

    // ---- AC-23 to AC-25 ----

    [Fact]
    public async Task AC23_Replace_changes_header_and_lines_but_not_id_or_status()
    {
        var s = await Orders.SetupAsync(app);
        var second = await o.NewPartnerAsync(s.Http, "P2", "Second partner");
        var other = await Keys.CreateAsync(app, s.Http, "clerk", "human");
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m), (s.A, 5, s.Box, 30m));
        var body = o.Replacement(second.Id(), s.W2, (s.B, 7, null, 1.1m))
            .With("orderDate", "2026-11-01").With(o.DueDate, "2026-11-15").With("reference", "Their-4711").With("note", "Line one\nline two");

        using var response = await o.PutAsync(other.Client, draft.Id(), body);

        var replaced = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(draft.Id(), replaced.Id());
        Assert.Equal("draft", replaced.Str("status"));
        JsonBody.AssertNull(replaced, "number", "confirmedAt", "confirmedBy");
        Assert.Equal(("2026-11-01", "2026-11-15"), (replaced.Str("orderDate"), replaced.Str(o.DueDate)));
        Assert.Equal(("Their-4711", "Line one\nline two"), (replaced.Str("reference"), replaced.Str("note")));
        AssertSummary(second, replaced.GetProperty(o.Partner));
        AssertSummary(s.U.S.Warehouse2, replaced.GetProperty("warehouse"));
        var line = Assert.Single(replaced.OrderLines());
        Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
        Assert.Equal(s.B, line.GetProperty("article").Id());
        Assert.Equal((7m, 1.1m, 7.7m), (line.Quantity(), line.Dec("unitPrice"), line.Dec("lineAmount")));
        Assert.Equal(7.7m, replaced.Dec("totalAmount"));
        Assert.Equal(s.Tenant.ApiKeyId, replaced.GetProperty("createdBy").GetGuid());
        Assert.Equal(other.Id, replaced.GetProperty("updatedBy").GetGuid());
        Assert.Equal(draft.GetProperty("createdAt").GetDateTimeOffset(), replaced.GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(replaced.GetProperty("updatedAt").GetDateTimeOffset() >= draft.GetProperty("updatedAt").GetDateTimeOffset());
        McpAssert.JsonEqual(replaced, await o.GetAsync(s.Http, draft.Id()));
    }

    [Fact]
    public async Task AC24_Replace_needs_all_seven_fields_and_the_optional_ones_may_be_null()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.CreateAsync(s.Http, o.Body(s, (s.A, 10, null, 2.5m))
            .With(o.DueDate, "2026-10-20").With("reference", "R-1").With("note", "Keep"));
        JsonObject Full() => o.Replacement(s, (s.B, 1, null, 1m)).With(o.DueDate, "2026-10-21").With("reference", "R-2").With("note", "New");

        foreach (var field in new[] { "orderDate", o.DueDate, o.PartnerId, "warehouseId", "reference", "note", "lines" })
        {
            using var refused = await o.PutAsync(s.Http, draft.Id(), Full().Without(field));
            await HttpAssert.ValidationAsync(refused, field);
            await o.AssertUnchangedAsync(s.Http, draft);
        }

        var cleared = await o.ReplaceAsync(s.Http, draft.Id(), Full().With(o.DueDate, null).With("reference", null).With("note", null));

        JsonBody.AssertNull(cleared, o.DueDate, "reference", "note");
        Assert.Equal(s.B, Assert.Single(cleared.OrderLines()).GetProperty("article").Id());
    }

    [Fact]
    public async Task AC24_R4_Empty_and_blank_optional_text_is_no_value()
    {
        // 005/R7 (ADR-0011): omitted, null, empty and whitespace-only all mean "no value".
        var s = await Orders.SetupAsync(app);

        var created = await o.CreateAsync(s.Http, o.Body(s, (s.A, 1, null, 1m)).With("reference", "").With("note", "   "));
        using var tooLong = await o.PostAsync(s.Http, o.Body(s, (s.A, 1, null, 1m)).With("reference", new string('x', 101)));
        using var noteTooLong = await o.PostAsync(s.Http, o.Body(s, (s.A, 1, null, 1m)).With("note", new string('x', 2001)));
        var longest = await o.CreateAsync(s.Http, o.Body(s, (s.A, 1, null, 1m))
            .With("reference", new string('x', 100)).With("note", new string('y', 2000)));

        JsonBody.AssertNull(created, "reference", "note");
        await HttpAssert.ValidationAsync(tooLong, "reference");
        await HttpAssert.ValidationAsync(noteTooLong, "note");
        Assert.Equal(100, longest.Str("reference").Length);
    }

    [Fact]
    public async Task AC25_Delete_removes_a_draft()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        using var deleted = await o.DeleteAsync(s.Http, draft.Id());
        using var get = await o.SendGetAsync(s.Http, draft.Id());
        using var again = await o.DeleteAsync(s.Http, draft.Id());

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(again);
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC25_R11_A_draft_can_be_replaced_any_number_of_times_and_lines_are_renumbered()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 1, null, 1m), (s.B, 2, null, 2m), (s.A, 3, null, 3m));

        await o.ReplaceAsync(s.Http, draft.Id(), o.Replacement(s, (s.B, 2, null, 2m)));
        var replaced = await o.ReplaceAsync(s.Http, draft.Id(), o.Replacement(s, (s.A, 3, null, 3m), (s.B, 2, null, 2m)));

        Assert.Equal([1, 2], replaced.OrderLines().Select(l => l.GetProperty("lineNo").GetInt32()));
        Assert.Equal([s.A, s.B], replaced.OrderLines().Select(l => l.GetProperty("article").Id()));
        Assert.Equal(13m, replaced.Dec("totalAmount"));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("put")]
    [InlineData("delete")]
    [InlineData("confirm")]
    [InlineData("close")]
    [InlineData("reopen")]
    public async Task AC25_An_unknown_or_malformed_order_id_is_not_found(string operation)
    {
        var s = await Orders.SetupAsync(app);

        foreach (var id in new[] { Guid.CreateVersion7().ToString(), "abc" })
        {
            var url = $"{o.Path}/{id}";
            using var response = operation switch
            {
                "get" => await s.Http.GetAsync(url),
                "put" => await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(s.Http, url, o.Replacement(s, (s.A, 1, null, 1m))),
                "delete" => await s.Http.DeleteAsync(url),
                _ => await s.Http.PostAsync($"{url}/{operation}", null),
            };
            await HttpAssert.NotFoundAsync(response);
        }
    }

    // ---- AC-26 ----

    public static TheoryData<string, string, string[]> InvalidBodies() => new()
    {
        // case, JSON patch applied to a valid two-line body ("-" removes the property), expected keys
        { "lines missing", """{ "lines": "-" }""", ["lines"] },
        { "lines empty", """{ "lines": [] }""", ["lines"] },
        { "lines not an array", """{ "lines": {} }""", ["lines"] },
        { "partner missing", """{ "$partner": "-" }""", ["$partner"] },
        { "partner malformed", """{ "$partner": "abc" }""", ["$partner"] },
        { "partner null", """{ "$partner": null }""", ["$partner"] },
        { "warehouse missing", """{ "warehouseId": "-" }""", ["warehouseId"] },
        { "warehouse malformed", """{ "warehouseId": "abc" }""", ["warehouseId"] },
        { "orderDate missing", """{ "orderDate": "-" }""", ["orderDate"] },
        { "orderDate not a date", """{ "orderDate": "2026-02-30" }""", ["orderDate"] },
        { "due date before orderDate", """{ "$due": "2026-10-08" }""", ["$due"] },
        { "due date not a date", """{ "$due": "2026-02-30" }""", ["$due"] },
        { "several fields at once", """{ "orderDate": "x", "$partner": "abc", "warehouseId": "abc" }""", ["orderDate", "$partner", "warehouseId"] },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task AC26_Header_validation(string @case, string patch, string[] keys)
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s, (s.A, 10, null, 2.5m), (s.B, 1, null, 1m));
        foreach (var (name, value) in JsonNode.Parse(patch.Replace("$partner", o.PartnerId).Replace("$due", o.DueDate))!.AsObject())
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && text == "-")
                body.Remove(name);
            else
                body[name] = value?.DeepClone();

        using var response = await o.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response, keys.Select(k => k.Replace("$partner", o.PartnerId).Replace("$due", o.DueDate)).ToArray());
        Assert.False(string.IsNullOrEmpty(@case));
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    // E1: unitPrice on the second line.
    [InlineData("unitPrice", "-")]
    [InlineData("unitPrice", "null")]
    [InlineData("unitPrice", "-0.01")]
    [InlineData("unitPrice", "\"2\"")]
    [InlineData("unitPrice", "1.0000001")]
    [InlineData("unitPrice", "1000000000")]
    [InlineData("unitPrice", "true")]
    // 005/R6: quantity.
    [InlineData("quantity", "-")]
    [InlineData("quantity", "0")]
    [InlineData("quantity", "-1")]
    [InlineData("quantity", "\"5\"")]
    [InlineData("quantity", "1.0000001")]
    [InlineData("articleId", "-")]
    [InlineData("articleId", "\"abc\"")]
    [InlineData("unitId", "\"abc\"")]
    public async Task AC26_E1_Line_validation_is_keyed_by_the_line(string field, string json)
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s, (s.A, 10, null, 2.5m), (s.B, 1, null, 1m));
        var line = body["lines"]![1]!.AsObject();
        if (json == "-")
            line.Remove(field);
        else
            line[field] = JsonNode.Parse(json);

        using var response = await o.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, $"lines[1].{field}");
        Assert.DoesNotContain(McpAssert.ErrorKeys(problem), k => k.StartsWith("lines[0]", StringComparison.Ordinal));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC26_R10_Validation_reports_all_fields_and_lines_together()
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s).With("orderDate", "2026-02-30");
        body["lines"] = new JsonArray(Orders.Line(s.A, 0, 2.5m), Orders.Line(s.B, 1, 1m), Orders.Line(s.B, 1, -1));

        using var response = await o.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "orderDate", "lines[0].quantity", "lines[2].unitPrice");
        Assert.DoesNotContain(McpAssert.ErrorKeys(problem), k => k.StartsWith("lines[1]", StringComparison.Ordinal));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC26_R5_An_order_has_1_to_200_lines()
    {
        var s = await Orders.SetupAsync(app);
        JsonObject WithLines(int count)
        {
            var body = o.Body(s);
            body["lines"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)Orders.Line(s.B, 1, 0.5m)).ToArray());
            return body;
        }

        using var tooMany = await o.PostAsync(s.Http, WithLines(201));
        await HttpAssert.ValidationAsync(tooMany, "lines");
        await AssertNothingCreatedAsync(s);

        var most = await o.CreateAsync(s.Http, WithLines(200));

        Assert.Equal(Enumerable.Range(1, 200), most.OrderLines().Select(l => l.GetProperty("lineNo").GetInt32()));
        Assert.Equal(100m, most.Dec("totalAmount"));
    }

    [Fact]
    public async Task AC26_R9_A_line_amount_above_the_maximum_is_rejected_under_quantity_and_price()
    {
        var s = await Orders.SetupAsync(app);

        using var above = await o.PostAsync(s.Http, o.Body(s, (s.A, 100000, null, 100000m)));
        using var second = await o.PostAsync(s.Http, o.Body(s, (s.B, 1, null, 1m), (s.A, 100000.01m, null, 100000m)));

        var problem = await HttpAssert.ValidationAsync(above, "lines[0].quantity", "lines[0].unitPrice");
        Assert.Equal(new[] { "lines[0].quantity", "lines[0].unitPrice" }, McpAssert.ErrorKeys(problem));
        await HttpAssert.ValidationAsync(second, "lines[1].quantity", "lines[1].unitPrice");
        await AssertNothingCreatedAsync(s);

        var below = await o.DraftAsync(s, (s.A, 99999.99m, null, 100000m));
        var exact = await o.DraftAsync(s, (s.B, 999999999.999m, null, 10m));

        Assert.Equal(9999999000m, below.Dec("totalAmount"));
        Assert.Equal(9999999999.99m, exact.Dec("totalAmount"));
    }

    [Theory]
    // 9999999999.995 is below 10000000000 and rounds to 10000000000.00, one cent above the maximum.
    [InlineData("999999999.9995", "10")]
    [InlineData("10", "999999999.9995")]
    public async Task AC26_R9_The_maximum_is_compared_with_the_rounded_line_amount(string quantity, string price)
    {
        var s = await Orders.SetupAsync(app);
        var body = o.Body(s);
        body["lines"] = new JsonArray(Orders.Line(s.B, JsonNode.Parse(quantity), JsonNode.Parse(price)));

        using var response = await o.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "lines[0].quantity", "lines[0].unitPrice");
        Assert.Equal(new[] { "lines[0].quantity", "lines[0].unitPrice" }, McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC26_Accepted_a_price_of_zero_and_a_due_date_equal_to_the_order_date()
    {
        var s = await Orders.SetupAsync(app);

        var free = await o.DraftAsync(s, (s.A, 3, null, 0m));
        var sameDay = await o.CreateAsync(s.Http, o.Body(s, (s.A, 1, null, 1m)).With(o.DueDate, Orders.Date));
        var explicitNull = await o.CreateAsync(s.Http, o.Body(s, (s.A, 1, null, 1m)).With(o.DueDate, null).With("reference", null).With("note", null));
        var nullUnit = await o.CreateAsync(s.Http, o.Body(s).With("lines", new JsonArray(Orders.Line(s.A, 1, 1).With("unitId", null))));

        Assert.Equal((0m, 0m), (free.OrderLines()[0].Dec("unitPrice"), free.Dec("totalAmount")));
        Assert.Equal(Orders.Date, sameDay.Str(o.DueDate));
        JsonBody.AssertNull(explicitNull, o.DueDate, "reference", "note");
        Assert.Equal("pcs", nullUnit.OrderLines()[0].GetProperty("unit").Str("code"));
    }

    // ---- AC-27 ----

    [Fact]
    public async Task AC27_The_partner_must_exist_be_active_and_have_the_role()
    {
        var s = await Orders.SetupAsync(app);
        var inactive = await o.NewPartnerAsync(s.Http, "OLD", "Retired partner", isActive: false);
        var both = await o.NewPartnerAsync(s.Http, "BOTH", "Trader", bothRoles: true);

        using var unknown = await o.PostAsync(s.Http, o.Body(Guid.CreateVersion7(), s.W1, (s.A, 1, null, 1m)));
        using var retired = await o.PostAsync(s.Http, o.Body(inactive.Id(), s.W1, (s.A, 1, null, 1m)));
        using var wrongRole = await o.PostAsync(s.Http, o.Body(o.WrongPartnerOf(s).Id(), s.W1, (s.A, 1, null, 1m)));
        // A warehouse's id is not a partner.
        using var notAPartner = await o.PostAsync(s.Http, o.Body(s.W1, s.W1, (s.A, 1, null, 1m)));

        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(unknown, o.PartnerId)));
        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceInactiveAsync(retired, o.PartnerId)));
        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await Stock.ConflictAsync(wrongRole, "PARTNER_ROLE_MISSING", o.PartnerId)));
        await HttpAssert.ReferenceNotFoundAsync(notAPartner, o.PartnerId);
        await AssertNothingCreatedAsync(s);

        // E6: a partner with both roles is accepted.
        var accepted = await o.CreateAsync(s.Http, o.Body(both.Id(), s.W1, (s.A, 1, null, 1m)));
        Assert.Equal(both.Id(), accepted.GetProperty(o.Partner).Id());
    }

    [Fact]
    public async Task AC27_The_warehouse_must_exist_and_be_active()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);

        using var unknown = await o.PostAsync(s.Http, o.Body(o.PartnerOf(s).Id(), Guid.CreateVersion7(), (s.A, 1, null, 1m)));
        using var inactive = await o.PostAsync(s.Http, o.Body(o.PartnerOf(s).Id(), s.W2, (s.A, 1, null, 1m)));

        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(unknown, "warehouseId")));
        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceInactiveAsync(inactive, "warehouseId")));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC27_Line_references_only_stock_articles_in_one_of_their_units()
    {
        var s = await Orders.SetupAsync(app);
        var retired = await Art.CreateAsync(s.Http, "OLD", "Retired article", s.Pcs);
        await Stock.SetArticleActiveAsync(s.Http, retired.Id(), false);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        JsonObject Second(JsonObject line) => o.Body(s, (s.A, 1, null, 1m)).With("lines", new JsonArray(Orders.Line(s.A, 1, 1), line));

        using var unknownArticle = await o.PostAsync(s.Http, Second(Orders.Line(Guid.CreateVersion7(), 1, 1)));
        using var inactiveArticle = await o.PostAsync(s.Http, Second(Orders.Line(retired.Id(), 1, 1)));
        using var service = await o.PostAsync(s.Http, Second(Orders.Line(s.Service, 1, 1)));
        using var unknownUnit = await o.PostAsync(s.Http, Second(Orders.Line(s.B, 1, 1, Guid.CreateVersion7())));
        using var foreignUnit = await o.PostAsync(s.Http, Second(Orders.Line(s.B, 1, 1, s.Box)));
        using var notConvertible = await o.PostAsync(s.Http, Second(Orders.Line(s.A, 0.000001m, 1, s.Pack)));

        Assert.Equal(new[] { "lines[1].articleId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(unknownArticle, "lines[1].articleId")));
        await HttpAssert.ReferenceInactiveAsync(inactiveArticle, "lines[1].articleId");
        Assert.Equal(new[] { "lines[1].articleId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(service, "ARTICLE_NOT_STOCKED", "lines[1].articleId")));
        await HttpAssert.ReferenceNotFoundAsync(unknownUnit, "lines[1].unitId");
        Assert.Equal(new[] { "lines[1].unitId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(foreignUnit, "UNIT_NOT_ON_ARTICLE", "lines[1].unitId")));
        await Stock.ConflictAsync(notConvertible, "QUANTITY_NOT_CONVERTIBLE", "lines[1].quantity");
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC27_Replace_checks_references_as_create_does_and_leaves_the_draft_unchanged()
    {
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));

        using var unknown = await o.PutAsync(s.Http, draft.Id(), o.Replacement(Guid.CreateVersion7(), s.W1, (s.A, 1, null, 1m)));
        using var wrongRole = await o.PutAsync(s.Http, draft.Id(), o.Replacement(o.WrongPartnerOf(s).Id(), s.W1, (s.A, 1, null, 1m)));
        using var warehouse = await o.PutAsync(s.Http, draft.Id(), o.Replacement(o.PartnerOf(s).Id(), Guid.CreateVersion7(), (s.A, 1, null, 1m)));
        using var service = await o.PutAsync(s.Http, draft.Id(), o.Replacement(s, (s.Service, 1, null, 1m)));
        using var unit = await o.PutAsync(s.Http, draft.Id(), o.Replacement(s, (s.B, 1, s.Box, 1m)));

        await HttpAssert.ReferenceNotFoundAsync(unknown, o.PartnerId);
        await Stock.ConflictAsync(wrongRole, "PARTNER_ROLE_MISSING", o.PartnerId);
        await HttpAssert.ReferenceNotFoundAsync(warehouse, "warehouseId");
        await Stock.ConflictAsync(service, "ARTICLE_NOT_STOCKED", "lines[0].articleId");
        await Stock.ConflictAsync(unit, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await o.AssertUnchangedAsync(s.Http, draft);
    }

    [Fact]
    public async Task AC27_E7_A_draft_whose_partner_lost_the_role_can_be_read_but_not_saved_with_it()
    {
        var s = await Orders.SetupAsync(app);
        var partner = o.PartnerOf(s).Id();
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));
        await o.RemoveRoleAsync(s.Http, partner);

        var read = await o.GetAsync(s.Http, draft.Id());
        using var put = await o.PutAsync(s.Http, draft.Id(), o.Replacement(s, (s.A, 10, null, 2.5m)));

        Assert.Equal(partner, read.GetProperty(o.Partner).Id());
        await Stock.ConflictAsync(put, "PARTNER_ROLE_MISSING", o.PartnerId);
        await o.AssertUnchangedAsync(s.Http, read);
    }

    [Fact]
    public async Task AC27_ADR0008_A_draft_keeps_a_deactivated_partner_and_warehouse_on_replace()
    {
        // 005/R5, R8: "inactive and newly assigned" is refused; a reference the draft already has stays valid.
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 10, null, 2.5m));
        await Orders.SetPartnerActiveAsync(s.Http, o.PartnerOf(s).Id(), false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);

        var replaced = await o.ReplaceAsync(s.Http, draft.Id(), o.Replacement(s, (s.A, 11, null, 2.5m)).With("note", "kept"));
        using var newlyAssigned = await o.PostAsync(s.Http, o.Body(s, (s.B, 1, null, 1m)));

        Assert.Equal(11m, replaced.OrderLines()[0].Quantity());
        await HttpAssert.ReferenceInactiveAsync(newlyAssigned, o.PartnerId);
    }

    // ---- AC-28 ----

    [Fact]
    public async Task AC28_E5_Each_stage_of_checks_answers_alone()
    {
        var s = await Orders.SetupAsync(app);
        var inactiveWrongRole = await Orders.PartnerAsync(s.Http, "X1", "Inactive, wrong role",
            isSupplier: o.Role != "isSupplier", isCustomer: o.Role != "isCustomer", isActive: false);
        var random = Guid.CreateVersion7();

        // Unknown partner, unknown warehouse and a service article: the partner stage answers.
        using var partnerFirst = await o.PostAsync(s.Http, o.Body(random, random, (s.Service, 1, null, 1m)));
        // An invalid price is decided before any reference.
        using var validationFirst = await o.PostAsync(s.Http, o.Body(random, s.W1, (s.A, 1, null, -1m)));
        // Within the partner stage: not found, then inactive, then the role.
        using var inactiveBeforeRole = await o.PostAsync(s.Http, o.Body(inactiveWrongRole.Id(), s.W1, (s.A, 1, null, 1m)));
        // The role is part of the partner stage, before the warehouse.
        using var roleBeforeWarehouse = await o.PostAsync(s.Http, o.Body(o.WrongPartnerOf(s).Id(), random, (s.Service, 1, null, 1m)));
        // The warehouse before the lines.
        using var warehouseBeforeLines = await o.PostAsync(s.Http, o.Body(o.PartnerOf(s).Id(), random, (s.Service, 1, null, 1m)));

        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(partnerFirst, o.PartnerId)));
        await HttpAssert.ValidationAsync(validationFirst, "lines[0].unitPrice");
        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceInactiveAsync(inactiveBeforeRole, o.PartnerId)));
        Assert.Equal(new[] { o.PartnerId }, McpAssert.ErrorKeys(await Stock.ConflictAsync(roleBeforeWarehouse, "PARTNER_ROLE_MISSING", o.PartnerId)));
        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(warehouseBeforeLines, "warehouseId")));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC28_R10_On_replace_validation_then_existence_then_state_then_references()
    {
        var s = await Orders.SetupAsync(app);
        var confirmed = await o.OrderedAsync(s, (s.A, 10, null, 2.5m));
        var random = Guid.CreateVersion7();

        using var invalidOnUnknown = await o.PutAsync(s.Http, random, o.Replacement(random, s.W1, (s.A, 1, null, -1m)));
        using var unknownOrder = await o.PutAsync(s.Http, random, o.Replacement(random, s.W1, (s.A, 1, null, 1m)));
        using var invalidOnConfirmed = await o.PutAsync(s.Http, confirmed.Id(), o.Replacement(s, (s.A, 1, null, -1m)));
        using var stateBeforeReferences = await o.PutAsync(s.Http, confirmed.Id(), o.Replacement(random, s.W1, (s.A, 1, null, 1m)));

        await HttpAssert.ValidationAsync(invalidOnUnknown, "lines[0].unitPrice");
        await HttpAssert.NotFoundAsync(unknownOrder);
        await HttpAssert.ValidationAsync(invalidOnConfirmed, "lines[0].unitPrice");
        await Stock.ConflictAsync(stateBeforeReferences, "INVALID_STATE");
        await o.AssertUnchangedAsync(s.Http, confirmed);
    }
}
