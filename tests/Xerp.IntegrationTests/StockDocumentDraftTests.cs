using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-11 (strict input, one smoke test), AC-20 to AC-28 (drafts) and AC-74 (unknown documents):
/// a draft is a freely editable form that has no effect on stock.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockDocumentDraftTests(XerpFixture app)
{
    private static readonly string[] DocumentProperties =
    [
        "id", "type", "status", "number", "documentDate", "warehouse", "reference", "note", "lines",
        "createdAt", "updatedAt", "createdBy", "updatedBy", "postedAt", "postedBy",
    ];

    private static async Task AssertNothingCreatedAsync(StockSetup s)
    {
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
    }

    private static void AssertSummary(JsonElement master, JsonElement summary)
    {
        Assert.Equal(master.Id(), summary.Id());
        Assert.Equal(master.Str("code"), summary.Str("code"));
        Assert.Equal(master.Str("name"), summary.Str("name"));
    }

    // ---- AC-11 ----

    [Theory]
    [InlineData("status", "\"posted\"")]
    [InlineData("number", "\"X\"")]
    [InlineData("tenantId", "\"0199c0de-0000-7000-8000-000000000001\"")]
    public async Task AC11_Create_with_an_unknown_property_is_rejected(string property, string json)
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 100)).With(property, JsonNode.Parse(json));

        using var response = await Stock.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response);
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    [InlineData(Stock.OnHand + "?foo=1", "foo")]
    [InlineData(Stock.Documents + "?status=open", "status")]
    public async Task AC11_Unknown_query_parameter_or_filter_value_is_rejected(string url, string errorKey)
    {
        var s = await Stock.SetupAsync(app);

        using var response = await s.Http.GetAsync(url);

        await HttpAssert.ValidationAsync(response, errorKey);
    }

    // ---- AC-20 to AC-22: create ----

    [Fact]
    public async Task AC20_Create_returns_a_draft_with_summaries_and_get_returns_the_same()
    {
        var s = await Stock.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 100)));

        var document = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        foreach (var property in DocumentProperties)
            Assert.True(document.TryGetProperty(property, out _), $"Property '{property}' is missing: {document}");
        Assert.False(document.TryGetProperty("tenantId", out _));
        Assert.Equal("receipt", document.Str("type"));
        Assert.Equal("draft", document.Str("status"));
        Assert.Equal(Stock.Date, document.Str("documentDate"));
        JsonBody.AssertNull(document, "number", "postedAt", "postedBy", "reference", "note");
        AssertSummary(s.Warehouse1, document.GetProperty("warehouse"));
        var line = Assert.Single(document.DocumentLines());
        Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
        AssertSummary(s.ArticleA, line.GetProperty("article"));
        AssertSummary(s.Unit, line.GetProperty("unit"));
        Assert.Equal("pcs", line.GetProperty("unit").Str("code"));
        Assert.Equal(100m, line.Quantity());
        Assert.Equal(s.Tenant.ApiKeyId, document.GetProperty("createdBy").GetGuid());
        Assert.EndsWith($"/api/v1/stock-documents/{document.Id()}", response.Headers.Location?.ToString());
        McpAssert.JsonEqual(document, await Stock.GetAsync(s.Http, document.Id()));
    }

    [Fact]
    public async Task AC21_A_draft_has_no_effect_on_stock_or_the_ledger()
    {
        var s = await Stock.SetupAsync(app);

        await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 100));

        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var onHand = await Stock.OnHandAsync(s.Http);
        var ledger = await Stock.LedgerAsync(s.Http);
        Assert.Empty(onHand.Items());
        Assert.Equal(0, onHand.Total());
        Assert.Empty(ledger.Items());
        Assert.Equal(0, ledger.Total());
    }

    [Fact]
    public async Task AC21_R9_A_draft_issue_reserves_nothing()
    {
        // R9: a draft issue for the whole stock does not stop another issue from taking it.
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 10));

        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC22_Lines_are_numbered_in_the_order_given_and_an_article_may_repeat()
    {
        var s = await Stock.SetupAsync(app);

        var document = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1m), (s.B, 2.5m), (s.A, 0.000001m));

        var lines = document.DocumentLines();
        Assert.Equal([1, 2, 3], lines.Select(l => l.GetProperty("lineNo").GetInt32()));
        Assert.Equal([s.A, s.B, s.A], lines.Select(l => l.GetProperty("article").Id()));
        Assert.Equal([1m, 2.5m, 0.000001m], lines.Select(l => l.Quantity()));
    }

    // ---- AC-23 to AC-25: replace and delete ----

    [Fact]
    public async Task AC23_Replace_changes_header_and_lines_but_not_type_id_or_status()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1), (s.A, 2), (s.B, 3));
        var body = new JsonObject
        {
            ["documentDate"] = "2026-11-30", ["warehouseId"] = s.W2.ToString(), ["partnerId"] = null, ["reference"] = "DN-4711",
            ["note"] = "Line one\nline two", ["lines"] = Stock.Lines((s.B, 7)),
        };

        var replaced = await Stock.ReplaceAsync(s.Http, draft.Id(), body);

        Assert.Equal(draft.Id(), replaced.Id());
        Assert.Equal("issue", replaced.Str("type"));
        Assert.Equal("draft", replaced.Str("status"));
        JsonBody.AssertNull(replaced, "number", "postedAt", "postedBy");
        Assert.Equal("2026-11-30", replaced.Str("documentDate"));
        AssertSummary(s.Warehouse2, replaced.GetProperty("warehouse"));
        Assert.Equal("DN-4711", replaced.Str("reference"));
        Assert.Equal("Line one\nline two", replaced.Str("note"));
        var line = Assert.Single(replaced.DocumentLines());
        Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
        Assert.Equal(s.B, line.GetProperty("article").Id());
        Assert.Equal(7m, line.Quantity());
        McpAssert.JsonEqual(replaced, await Stock.GetAsync(s.Http, draft.Id()));
    }

    [Fact]
    public async Task AC23_R10_A_draft_can_be_replaced_any_number_of_times_and_clears_reference_and_note()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http,
            Stock.Draft("receipt", s.W1, (s.A, 1)).With("reference", "REF-1").With("note", "a note"));
        Assert.Equal("REF-1", draft.Str("reference"));

        await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 2), (s.B, 3)));
        var last = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.B, 4)).With("reference", "  "));

        JsonBody.AssertNull(last, "reference", "note");
        var line = Assert.Single(last.DocumentLines());
        Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
        Assert.Equal(4m, line.Quantity());
    }

    [Theory]
    [InlineData("documentDate")]
    [InlineData("warehouseId")]
    [InlineData("partnerId")] // spec 011a, R3 / AC-22
    [InlineData("reference")]
    [InlineData("note")]
    [InlineData("lines")]
    public async Task AC24_Replace_omitting_a_property_is_rejected_with_that_key(string property)
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 100));

        using var response = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W2, (s.B, 7)).Without(property));

        await HttpAssert.ValidationAsync(response, property);
        await Stock.AssertUnchangedAsync(s.Http, draft);
    }

    [Fact]
    public async Task AC24_Replace_with_type_in_the_body_is_rejected()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 100));

        using var response = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 100)).With("type", "issue"));

        await HttpAssert.ValidationAsync(response);
        await Stock.AssertUnchangedAsync(s.Http, draft);
    }

    [Fact]
    public async Task AC25_Delete_removes_the_draft()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 100), (s.B, 1));

        using var deleted = await Stock.DeleteAsync(s.Http, draft.Id());

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var get = await s.Http.GetAsync($"{Stock.Documents}/{draft.Id()}");
        await HttpAssert.NotFoundAsync(get);
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
        using var again = await Stock.DeleteAsync(s.Http, draft.Id());
        await HttpAssert.NotFoundAsync(again);
    }

    // ---- AC-26: validation ----

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("not-an-array")]
    [InlineData("201")]
    public async Task AC26_Lines_missing_empty_or_too_many_are_rejected(string kind)
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 1));
        switch (kind)
        {
            case "missing": body.Remove("lines"); break;
            case "empty": body["lines"] = new JsonArray(); break;
            case "null": body["lines"] = null; break;
            case "not-an-array": body["lines"] = Stock.Line(s.A, 1); break;
            default: body["lines"] = Stock.Lines(Enumerable.Repeat((s.A, 1m), 201).ToArray()); break;
        }

        using var response = await Stock.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response, "lines");
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    [InlineData("1.0000001")]
    [InlineData("1000000000")]
    public async Task AC26_Invalid_quantity_on_the_second_line_is_rejected_with_its_key(string quantity)
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 1));
        body["lines"]!.AsArray().Add(Stock.Line(s.B, JsonNode.Parse(quantity)));

        using var response = await Stock.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "lines[1].quantity");
        Assert.DoesNotContain("lines[0].quantity", McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC26_A_line_without_article_is_rejected_with_its_key()
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 1));
        body["lines"]![0]!.AsObject().Remove("articleId");

        using var response = await Stock.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response, "lines[0].articleId");
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    [InlineData("unit", "\"0199c0de-0000-7000-8000-000000000001\"")]
    [InlineData("lineNo", "1")]
    [InlineData("price", "9.99")]
    public async Task AC26_E2_A_line_with_an_extra_property_is_rejected_with_a_key_of_that_line(string property, string json)
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 1), (s.B, 1));
        body["lines"]![1]!.AsObject()[property] = JsonNode.Parse(json);

        using var response = await Stock.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response);
        Assert.Contains(McpAssert.ErrorKeys(problem), key => key.StartsWith("lines[1]", StringComparison.Ordinal));
        await AssertNothingCreatedAsync(s);
    }

    [Theory]
    [InlineData("documentDate", "\"2026-02-30\"")]
    [InlineData("documentDate", "\"09.10.2026\"")]
    [InlineData("documentDate", "\"\"")]
    [InlineData("documentDate", "\"2026-10-09T10:00:00Z\"")]
    [InlineData("documentDate", null)]
    [InlineData("type", "\"return\"")] // spec 006 made "transfer" a valid type
    [InlineData("type", "\"Receipt\"")]
    [InlineData("type", null)]
    [InlineData("warehouseId", "\"abc\"")] // a missing warehouseId is the default warehouse since spec 011 (011/AC-30)
    public async Task AC26_Invalid_or_missing_header_field_is_rejected_with_its_key(string property, string? json)
    {
        // json == null: the property is omitted.
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 1));
        if (json is null)
            body.Remove(property);
        else
            body[property] = JsonNode.Parse(json);

        using var response = await Stock.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response, property);
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC26_R8_All_invalid_fields_and_lines_are_reported_together()
    {
        var s = await Stock.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 0), (s.B, 1), (s.A, -1)).With("documentDate", "2026-02-30");

        using var response = await Stock.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "documentDate", "lines[0].quantity", "lines[2].quantity");
        Assert.DoesNotContain("lines[1].quantity", McpAssert.ErrorKeys(problem));
    }

    [Fact]
    public async Task AC26_Two_hundred_lines_and_the_largest_and_smallest_quantity_are_accepted()
    {
        var s = await Stock.SetupAsync(app);

        var many = await Stock.CreateAsync(s.Http,
            Stock.Draft("receipt", s.W1).With("lines", Stock.Lines(Enumerable.Repeat((s.A, 1m), 200).ToArray())));
        var extremes = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 999999999.999999m), (s.B, 0.000001m));

        Assert.Equal(200, many.DocumentLines().Length);
        Assert.Equal(Enumerable.Range(1, 200), many.DocumentLines().Select(l => l.GetProperty("lineNo").GetInt32()));
        Assert.Equal([999999999.999999m, 0.000001m], extremes.DocumentLines().Select(l => l.Quantity()));
    }

    [Theory]
    [InlineData("1999-12-31")]
    [InlineData("2099-01-01")]
    [InlineData("2028-02-29")]
    public async Task AC26_R2_Any_real_calendar_date_is_accepted(string date)
    {
        var s = await Stock.SetupAsync(app);

        var document = await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 1)).With("documentDate", date));

        Assert.Equal(date, document.Str("documentDate"));
    }

    // ---- AC-27, AC-28: references ----

    [Fact]
    public async Task AC27_Unknown_or_inactive_warehouse_is_a_reference_conflict()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);

        using var unknown = await Stock.PostAsync(s.Http, Stock.Draft("receipt", Guid.NewGuid(), (s.A, 1)));
        using var inactive = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W2, (s.A, 1)));

        await Stock.ConflictAsync(unknown, "REFERENCE_NOT_FOUND", "warehouseId");
        await Stock.ConflictAsync(inactive, "REFERENCE_INACTIVE", "warehouseId");
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC27_Unknown_inactive_or_service_article_on_the_second_line_is_a_conflict_with_that_lines_key()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);

        using var unknown = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1), (Guid.NewGuid(), 1)));
        using var inactive = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1), (s.B, 1)));
        using var service = await Stock.PostAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 1), (s.S, 1)));

        foreach (var (response, code) in new[]
                 {
                     (unknown, "REFERENCE_NOT_FOUND"), (inactive, "REFERENCE_INACTIVE"), (service, "ARTICLE_NOT_STOCKED"),
                 })
        {
            var problem = await Stock.ConflictAsync(response, code, "lines[1].articleId");
            Assert.DoesNotContain("lines[0].articleId", McpAssert.ErrorKeys(problem));
        }
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC27_R8_Validation_precedes_references_and_the_warehouse_precedes_the_lines()
    {
        var s = await Stock.SetupAsync(app);

        using var invalid = await Stock.PostAsync(s.Http, Stock.Draft("receipt", Guid.NewGuid(), (s.S, 0)));
        using var warehouseFirst = await Stock.PostAsync(s.Http, Stock.Draft("receipt", Guid.NewGuid(), (s.S, 1)));

        await HttpAssert.ValidationAsync(invalid, "lines[0].quantity");
        await Stock.ConflictAsync(warehouseFirst, "REFERENCE_NOT_FOUND", "warehouseId");
    }

    [Fact]
    public async Task AC27_R8_The_first_failing_kind_is_reported_for_all_lines_that_have_it()
    {
        // Lines: unknown, service, unknown -> REFERENCE_NOT_FOUND for lines 0 and 2, nothing about line 1.
        var s = await Stock.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http,
            Stock.Draft("receipt", s.W1, (Guid.NewGuid(), 1), (s.S, 1), (Guid.NewGuid(), 1)));

        var problem = await Stock.ConflictAsync(response, "REFERENCE_NOT_FOUND", "lines[0].articleId", "lines[2].articleId");
        Assert.DoesNotContain("lines[1].articleId", McpAssert.ErrorKeys(problem));
    }

    [Fact]
    public async Task AC28_A_draft_keeps_its_deactivated_masters_but_cannot_take_a_new_inactive_article()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);
        await Stock.SetArticleActiveAsync(s.Http, s.B, false);

        var kept = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 5)));
        using var added = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 5), (s.B, 1)));

        Assert.Equal(5m, Assert.Single(kept.DocumentLines()).Quantity());
        var problem = await Stock.ConflictAsync(added, "REFERENCE_INACTIVE", "lines[1].articleId");
        Assert.DoesNotContain("lines[0].articleId", McpAssert.ErrorKeys(problem));
        await Stock.AssertUnchangedAsync(s.Http, kept);
    }

    [Fact]
    public async Task AC28_E10_A_draft_with_deactivated_masters_can_still_be_read_and_deleted()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1));
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);

        var read = await Stock.GetAsync(s.Http, draft.Id());
        using var moved = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W2, (s.A, 1)));
        using var deleted = await Stock.DeleteAsync(s.Http, draft.Id());

        Assert.Equal(s.W1, read.GetProperty("warehouse").Id());
        // Moving to another (active) warehouse keeps the inactive article, which is not newly assigned.
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    // ---- AC-74 ----

    [Fact]
    public async Task AC74_Unknown_document_is_not_found_for_every_operation()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var unknown = Guid.NewGuid();

        using var get = await s.Http.GetAsync($"{Stock.Documents}/{unknown}");
        using var byNumber = await Stock.ByNumberAsync(s.Http, "SR-999999");
        using var byDraftId = await Stock.ByNumberAsync(s.Http, draft.Id().ToString()); // E12
        using var byOddNumber = await Stock.ByNumberAsync(s.Http, "%' OR 1=1 --"); // E12
        using var put = await Stock.PutAsync(s.Http, unknown, Stock.Replacement(s.W1, (s.A, 1)));
        using var delete = await Stock.DeleteAsync(s.Http, unknown);
        using var post = await Stock.SendPostAsync(s.Http, unknown);

        foreach (var response in new[] { get, byNumber, byDraftId, byOddNumber, put, delete, post })
            await HttpAssert.NotFoundAsync(response);
        Assert.Equal(1, (await Stock.DocumentsAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC74_R8_Validation_precedes_the_existence_check_on_replace()
    {
        var s = await Stock.SetupAsync(app);

        using var response = await Stock.PutAsync(s.Http, Guid.NewGuid(), Stock.Replacement(s.W1, (s.A, 0)));

        await HttpAssert.ValidationAsync(response, "lines[0].quantity");
    }
}
