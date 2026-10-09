using System.Text.Json;
using Xerp.Application.Common;
using Xerp.Application.Stock;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 005: the input rules of stock documents (R1-R8) and the decisions about lines that posting and
/// saving take from facts already read (R5, R13, R15), in Application, without HTTP or a database.
/// </summary>
public class StockDocumentValidationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string W = Guid.CreateVersion7().ToString();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid S = Guid.CreateVersion7();

    private static StockLineInput Line(Guid article, decimal quantity) => new(article.ToString(), quantity);

    private static CreateStockDocumentInput Receipt(params StockLineInput?[] lines) =>
        new("receipt", "2026-10-09", W, lines);

    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), result.Error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    private static void AssertError(AppError? error, string code, params string[] expectedKeys)
    {
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Create_returns_type_header_and_lines_in_the_order_given()
    {
        var result = StockDocumentValidation.Create(new CreateStockDocumentInput(
            "issue", "2026-10-09", W, [Line(A, 1m), Line(B, 2.5m), Line(A, 0.000001m)], "  DN-1 ", " a\nb "));

        Assert.True(result.IsSuccess);
        Assert.Equal(StockDocumentType.Issue, result.Value.Type);
        var values = result.Value.Values;
        Assert.Equal((new DateOnly(2026, 10, 9), Guid.Parse(W), "DN-1", "a\nb"), (values.DocumentDate, values.WarehouseId, values.Reference, values.Note));
        Assert.Equal([new StockLineRequest(A, 1m), new StockLineRequest(B, 2.5m), new StockLineRequest(A, 0.000001m)], values.Lines);
    }

    [Fact]
    public void R7_Empty_reference_and_note_are_no_value()
    {
        var values = StockDocumentValidation.Create(Receipt(Line(A, 1m)) with { Reference = "  ", Note = "" }).Value!.Values;

        Assert.Null(values.Reference);
        Assert.Null(values.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Receipt")]
    [InlineData("return")]
    [InlineData(" receipt")]
    public void R1_E5_Type_is_exactly_receipt_or_issue(string? type)
    {
        AssertInvalid(StockDocumentValidation.Create(Receipt(Line(A, 1m)) with { Type = type }), "type");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-02-30")]
    [InlineData("09.10.2026")]
    [InlineData("2026-10-09T10:00:00Z")]
    [InlineData("2026-1-9")]
    [InlineData(" 2026-10-09")]
    public void R2_E4_Document_date_is_a_real_calendar_date_as_YYYY_MM_DD(string? date)
    {
        AssertInvalid(StockDocumentValidation.Create(Receipt(Line(A, 1m)) with { DocumentDate = date }), "documentDate");
    }

    [Theory]
    [InlineData("1999-12-31")]
    [InlineData("2099-01-01")]
    [InlineData("2028-02-29")]
    public void R2_Any_date_past_or_future_is_allowed(string date)
    {
        Assert.True(StockDocumentValidation.Create(Receipt(Line(A, 1m)) with { DocumentDate = date }).IsSuccess);
    }

    [Fact]
    public void R3_Warehouse_id_is_required_and_a_uuid()
    {
        AssertInvalid(StockDocumentValidation.Create(Receipt(Line(A, 1m)) with { WarehouseId = null }), "warehouseId");
        AssertInvalid(StockDocumentValidation.Create(Receipt(Line(A, 1m)) with { WarehouseId = "abc" }), "warehouseId");
    }

    [Fact]
    public void R4_E1_Lines_are_required_one_to_two_hundred()
    {
        AssertInvalid(StockDocumentValidation.Create(Receipt() with { Lines = null }), "lines");
        AssertInvalid(StockDocumentValidation.Create(Receipt()), "lines");
        AssertInvalid(StockDocumentValidation.Create(Receipt(Enumerable.Repeat(Line(A, 1m), 201).ToArray())), "lines");
        Assert.Equal(200, StockDocumentValidation.Create(Receipt(Enumerable.Repeat(Line(A, 1m), 200).ToArray())).Value!.Values.Lines.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0000001")]
    [InlineData("1000000000")]
    public void R6_E3_An_invalid_quantity_is_reported_under_its_line(string quantity)
    {
        var bad = decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture);

        AssertInvalid(StockDocumentValidation.Create(Receipt(Line(A, 1m), Line(B, bad))), "lines[1].quantity");
    }

    [Fact]
    public void R6_The_limits_of_a_quantity_are_accepted()
    {
        var values = StockDocumentValidation.Create(Receipt(Line(A, 0.000001m), Line(A, 999999999.999999m))).Value!.Values;

        Assert.Equal([0.000001m, 999999999.999999m], values.Lines.Select(l => l.Quantity));
    }

    [Fact]
    public void E2_A_line_must_be_an_object_with_article_and_quantity()
    {
        AssertInvalid(StockDocumentValidation.Create(Receipt(new StockLineInput(null, 1m))), "lines[0].articleId");
        AssertInvalid(StockDocumentValidation.Create(Receipt(new StockLineInput("abc", 1m))), "lines[0].articleId");
        AssertInvalid(StockDocumentValidation.Create(Receipt(new StockLineInput(A.ToString(), null))), "lines[0].quantity");
        AssertInvalid(StockDocumentValidation.Create(Receipt(Line(A, 1m), null)), "lines[1]");
    }

    [Fact]
    public void R8_All_invalid_fields_and_lines_are_reported_together_valid_lines_are_not()
    {
        var input = new CreateStockDocumentInput(
            "return", "2026-02-30", "abc", [Line(A, 0m), Line(B, 1m), new StockLineInput(null, -1m)], "a\nb", "a\u0000b");

        AssertInvalid(StockDocumentValidation.Create(input),
            "type", "documentDate", "warehouseId", "reference", "note", "lines[0].quantity", "lines[2].articleId", "lines[2].quantity");
    }

    [Fact]
    public void R7_Replace_requires_all_five_fields_reference_and_note_may_be_null()
    {
        const string date = "2026-10-10";
        var lines = $$"""[{"articleId":"{{A}}","quantity":7}]""";
        ReplaceStockDocumentInput Body(string json) => JsonSerializer.Deserialize<ReplaceStockDocumentInput>(json, Web)!;

        var full = StockDocumentValidation.Replace(Body($$"""{"documentDate":"{{date}}","warehouseId":"{{W}}","reference":null,"note":null,"lines":{{lines}}}"""));
        Assert.True(full.IsSuccess);
        Assert.Equal((new DateOnly(2026, 10, 10), null, null), (full.Value.DocumentDate, full.Value.Reference, full.Value.Note));
        Assert.Equal([new StockLineRequest(A, 7m)], full.Value.Lines);

        AssertInvalid(StockDocumentValidation.Replace(Body($$"""{"warehouseId":"{{W}}","reference":null,"note":null,"lines":{{lines}}}""")), "documentDate");
        AssertInvalid(StockDocumentValidation.Replace(Body($$"""{"documentDate":"{{date}}","reference":null,"note":null,"lines":{{lines}}}""")), "warehouseId");
        AssertInvalid(StockDocumentValidation.Replace(Body($$"""{"documentDate":"{{date}}","warehouseId":"{{W}}","note":null,"lines":{{lines}}}""")), "reference");
        AssertInvalid(StockDocumentValidation.Replace(Body($$"""{"documentDate":"{{date}}","warehouseId":"{{W}}","reference":"x","lines":{{lines}}}""")), "note");
        AssertInvalid(StockDocumentValidation.Replace(Body($$"""{"documentDate":"{{date}}","warehouseId":"{{W}}","reference":null,"note":null}""")), "lines");
    }

    [Fact]
    public void R20_R21_R22_List_queries_take_well_formed_filters_only()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(new StockDocumentListQuery(null, null, null, null, 50, 0), StockDocumentValidation.List(new ListStockDocumentsInput()).Value);
        Assert.Equal(
            new StockDocumentListQuery(StockDocumentType.Issue, StockDocumentStatus.Draft, id, "sr-0", 10, 5),
            StockDocumentValidation.List(new ListStockDocumentsInput("issue", "draft", id.ToString(), " sr-0 ", 10, 5)).Value);
        AssertInvalid(StockDocumentValidation.List(new ListStockDocumentsInput("return", "open", "abc", null, 0, -1)),
            "type", "status", "warehouseId", "limit", "offset");
        AssertInvalid(StockDocumentValidation.List(new ListStockDocumentsInput("Receipt", "Posted")), "type", "status");

        Assert.Equal(new StockOnHandQuery(id, null, 50, 0), StockDocumentValidation.OnHand(new ListStockOnHandInput(id.ToString())).Value);
        AssertInvalid(StockDocumentValidation.OnHand(new ListStockOnHandInput("abc", "x", 501)), "articleId", "warehouseId", "limit");
        Assert.Equal(new StockLedgerQuery(null, id, id, 1, 2), StockDocumentValidation.Ledger(new ListStockLedgerEntriesInput(null, id.ToString(), id.ToString(), 1, 2)).Value);
        AssertInvalid(StockDocumentValidation.Ledger(new ListStockLedgerEntriesInput("abc", null, "1")), "articleId", "documentId");
    }

    // ---- decisions about lines

    // Spec 007: every line here is in the base unit of its article (no unitId), as every line was before that spec.
    private static readonly Guid Pcs = Guid.CreateVersion7();

    private static readonly Dictionary<Guid, ArticleFacts> Articles = new()
    {
        [A] = new ArticleFacts(true, ArticleType.Stock, Pcs),
        [B] = new ArticleFacts(false, ArticleType.Stock, Pcs),
        [S] = new ArticleFacts(true, ArticleType.Service, Pcs),
    };

    private static readonly StockLineFacts Facts = new(Articles, new Dictionary<Guid, bool>(), new Dictionary<(Guid, Guid), decimal>());

    private static List<StockLineRequest> Lines(params Guid[] articles) => articles.Select(a => new StockLineRequest(a, 1m)).ToList();

    private static List<StockLineEntry> Entries(params Guid[] articles) => articles.Select(a => new StockLineEntry(a, Pcs, 1m)).ToList();

    private static Result<IReadOnlyList<StockLineEntry>> References(List<StockLineRequest> lines, params Guid[] alreadyOnDocument) =>
        StockLineChecks.References(lines, Facts, alreadyOnDocument.ToHashSet(), new HashSet<Guid>(), StockDocumentType.Receipt);

    [Fact]
    public void R5_Stock_articles_that_are_active_or_already_on_the_draft_are_accepted()
    {
        Assert.Null(References(Lines(A, A)).Error);
        Assert.Null(References(Lines(A, B), B).Error); // E10: kept although inactive
    }

    [Fact]
    public void R5_R8_The_first_failing_kind_is_reported_for_all_lines_that_have_it()
    {
        var unknown = Guid.CreateVersion7();

        AssertError(References(Lines(unknown, S, unknown, B)).Error,
            ErrorCodes.ReferenceNotFound, "lines[0].articleId", "lines[2].articleId");
        AssertError(References(Lines(A, B, S, B)).Error,
            ErrorCodes.ReferenceInactive, "lines[1].articleId", "lines[3].articleId");
        AssertError(References(Lines(A, S)).Error,
            ErrorCodes.ArticleNotStocked, "lines[1].articleId");
        AssertError(References(Lines(B, S), B).Error,
            ErrorCodes.ArticleNotStocked, "lines[1].articleId");
    }

    [Fact]
    public void R13_Posting_needs_every_article_active_also_those_assigned_earlier()
    {
        Assert.Null(StockLineChecks.ActiveForPosting([], Entries(A, A), Articles));
        AssertError(StockLineChecks.ActiveForPosting([], Entries(A, B), Articles), ErrorCodes.ReferenceInactive, "lines[1].articleId");
    }

    [Fact]
    public void R15_Insufficient_stock_names_the_quantity_of_every_line_of_every_short_article_only()
    {
        var onHand = new Dictionary<Guid, decimal> { [A] = 10m };
        List<StockLineValues> lines = [new(A, 6m), new(B, 1m), new(A, 4m)];

        Assert.Null(StockLineChecks.Sufficiency([new(A, 6m), new(A, 4m)], onHand));
        AssertError(StockLineChecks.Sufficiency(lines, onHand), ErrorCodes.InsufficientStock, "lines[1].quantity");
        AssertError(StockLineChecks.Sufficiency([new(A, 6m), new(A, 6m)], onHand), ErrorCodes.InsufficientStock, "lines[0].quantity", "lines[1].quantity");
    }
}
