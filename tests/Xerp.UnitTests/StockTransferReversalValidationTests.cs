using Xerp.Application.Common;
using Xerp.Application.Stock;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 006: the input rules of a transfer (R2, R3) and of a reversal (R12), and the decisions posting and
/// reversing take from facts already read (R5, R16), in Application, without HTTP or a database.
/// </summary>
public class StockTransferReversalValidationTests
{
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 10, 9);

    private static CreateStockDocumentInput Document(string type, string? toWarehouseId) =>
        new(type, "2026-10-09", W1.ToString(), [new StockLineInput(A.ToString(), 30m)], ToWarehouseId: toWarehouseId);

    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        AssertError(result.Error, ErrorCodes.ValidationFailed, expectedKeys);
    }

    private static void AssertError(AppError? error, string code, params string[] expectedKeys)
    {
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    // ---- transfer: input

    [Fact]
    public void R1_A_transfer_is_created_with_source_and_destination()
    {
        var result = StockDocumentValidation.Create(Document("transfer", W2.ToString()));

        Assert.True(result.IsSuccess);
        Assert.Equal(StockDocumentType.Transfer, result.Value.Type);
        Assert.Equal((W1, (Guid?)W2), (result.Value.Values.WarehouseId, result.Value.Values.ToWarehouseId));
    }

    [Fact]
    public void R2_E1_A_transfer_needs_a_destination_that_is_a_uuid_and_differs_from_the_source()
    {
        AssertInvalid(StockDocumentValidation.Create(Document("transfer", null)), "toWarehouseId");
        AssertInvalid(StockDocumentValidation.Create(Document("transfer", "abc")), "toWarehouseId");
        AssertInvalid(StockDocumentValidation.Create(Document("transfer", "")), "toWarehouseId");
        // Equal to the source: the destination is named, never the source.
        AssertInvalid(StockDocumentValidation.Create(Document("transfer", W1.ToString())), "toWarehouseId");
        AssertInvalid(StockDocumentValidation.Create(Document("transfer", W1.ToString().ToUpperInvariant())), "toWarehouseId");
    }

    [Fact]
    public void R3_E2_A_receipt_or_an_issue_has_no_destination()
    {
        Assert.Null(StockDocumentValidation.Create(Document("receipt", null)).Value!.Values.ToWarehouseId);
        Assert.True(StockDocumentValidation.Create(Document("issue", null)).IsSuccess);
        AssertInvalid(StockDocumentValidation.Create(Document("receipt", W2.ToString())), "toWarehouseId");
        AssertInvalid(StockDocumentValidation.Create(Document("issue", W2.ToString())), "toWarehouseId");
        AssertInvalid(StockDocumentValidation.Create(Document("receipt", "abc")), "toWarehouseId");
    }

    [Fact]
    public void R4_The_destination_is_reported_together_with_the_other_invalid_fields()
    {
        var input = new CreateStockDocumentInput("transfer", "09.10.2026", "abc", [new StockLineInput(A.ToString(), 0m)]);

        AssertInvalid(StockDocumentValidation.Create(input), "documentDate", "warehouseId", "toWarehouseId", "lines[0].quantity");
        // An unknown type cannot say whether a destination is needed: only the type is reported for it.
        AssertInvalid(StockDocumentValidation.Create(Document("return", null)), "type");
        AssertInvalid(StockDocumentValidation.Create(Document("return", "abc")), "type", "toWarehouseId");
    }

    [Fact]
    public void R2_R3_On_replace_the_stored_type_decides_about_the_destination()
    {
        var body = new ReplaceStockDocumentInput
        {
            DocumentDate = "2026-10-09", WarehouseId = W1.ToString(), ToWarehouseId = W2.ToString(),
            Reference = null, Note = null, Lines = [new StockLineInput(A.ToString(), 1m)],
        };

        // The form is checked without the type; the rule with it.
        Assert.Equal(W2, StockDocumentValidation.Replace(body).Value!.ToWarehouseId);
        Assert.Null(StockDocumentValidation.Replace(body with { ToWarehouseId = null }).Value!.ToWarehouseId);
        AssertInvalid(StockDocumentValidation.Replace(body with { ToWarehouseId = "abc" }), "toWarehouseId");

        Assert.Null(StockDocumentValidation.Destination(StockDocumentType.Transfer, W1, W2));
        Assert.Null(StockDocumentValidation.Destination(StockDocumentType.Receipt, W1, null));
        Assert.Null(StockDocumentValidation.Destination(StockDocumentType.Issue, W1, null));
        AssertError(StockDocumentValidation.Destination(StockDocumentType.Transfer, W1, null), ErrorCodes.ValidationFailed, "toWarehouseId");
        AssertError(StockDocumentValidation.Destination(StockDocumentType.Transfer, W1, W1), ErrorCodes.ValidationFailed, "toWarehouseId");
        AssertError(StockDocumentValidation.Destination(StockDocumentType.Receipt, W1, W2), ErrorCodes.ValidationFailed, "toWarehouseId");
        AssertError(StockDocumentValidation.Destination(StockDocumentType.Issue, W1, W2), ErrorCodes.ValidationFailed, "toWarehouseId");
    }

    [Fact]
    public void List_accepts_transfer_and_reversed()
    {
        var query = StockDocumentValidation.List(new ListStockDocumentsInput("transfer", "reversed")).Value!;

        Assert.Equal((StockDocumentType.Transfer, StockDocumentStatus.Reversed), (query.Type!.Value, query.Status!.Value));
        AssertInvalid(StockDocumentValidation.List(new ListStockDocumentsInput("return", "cancelled")), "type", "status");
    }

    // ---- transfer: posting

    [Fact]
    public void R5_Posting_reports_every_inactive_master_together()
    {
        var articles = new Dictionary<Guid, ArticleFacts>
        {
            [A] = new ArticleFacts(true, ArticleType.Stock),
            [B] = new ArticleFacts(false, ArticleType.Stock),
        };
        List<StockLineValues> lines = [new(A, 1m), new(B, 1m)];

        Assert.Null(StockLineChecks.ActiveForPosting([], [new(A, 1m)], articles));
        AssertError(StockLineChecks.ActiveForPosting([("warehouseId", W1), ("toWarehouseId", W2)], [new(A, 1m)], articles),
            ErrorCodes.ReferenceInactive, "warehouseId", "toWarehouseId");
        AssertError(StockLineChecks.ActiveForPosting([("toWarehouseId", W2)], [new(A, 1m)], articles),
            ErrorCodes.ReferenceInactive, "toWarehouseId");
        AssertError(StockLineChecks.ActiveForPosting([("warehouseId", W1)], lines, articles),
            ErrorCodes.ReferenceInactive, "warehouseId", "lines[1].articleId");
    }

    // ---- reversal: input

    [Fact]
    public void R12_A_reversal_has_a_real_date_and_an_optional_note()
    {
        var full = StockDocumentValidation.Reverse(new ReverseStockDocumentInput("2026-10-10", "  wrong article\nsecond line "));

        Assert.Equal(new StockReversalValues(new DateOnly(2026, 10, 10), "wrong article\nsecond line"), full.Value);
        Assert.Null(StockDocumentValidation.Reverse(new ReverseStockDocumentInput("2026-10-10")).Value!.Note);
        Assert.Null(StockDocumentValidation.Reverse(new ReverseStockDocumentInput("2026-10-10", "  ")).Value!.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-02-30")]
    [InlineData("2026-10-09T10:00:00Z")]
    [InlineData("09.10.2026")]
    [InlineData("2026-1-9")]
    public void R12_E9_A_reversal_without_a_real_date_is_invalid(string? date)
    {
        AssertInvalid(StockDocumentValidation.Reverse(new ReverseStockDocumentInput(date)), "documentDate");
    }

    [Fact]
    public void R12_Date_and_note_are_reported_together()
    {
        AssertInvalid(StockDocumentValidation.Reverse(new ReverseStockDocumentInput(null, new string('n', 2001))), "documentDate", "note");
        AssertInvalid(StockDocumentValidation.Reverse(new ReverseStockDocumentInput("2026-10-10", "a\u0000b")), "note");
    }

    [Fact]
    public void R12_E8_A_reversal_is_not_dated_before_its_original()
    {
        Assert.Null(StockDocumentValidation.ReversalDate(Day, Day));
        Assert.Null(StockDocumentValidation.ReversalDate(Day.AddYears(1), Day));
        AssertError(StockDocumentValidation.ReversalDate(Day.AddDays(-1), Day), ErrorCodes.ValidationFailed, "documentDate");
    }

    // ---- reversal: no negative stock (R16)

    [Fact]
    public void R16_Reversing_a_receipt_needs_the_goods_still_on_hand_and_names_the_lines_of_short_articles_only()
    {
        // Received: A 6, B 1, A 4 into W1. Since then 0.000001 of A left.
        List<StockLineValues> lines = [new(A, 6m), new(B, 1m), new(A, 4m)];
        List<StockMovement> reversing = [new(A, W1, -6m), new(B, W1, -1m), new(A, W1, -4m)];

        Assert.Null(StockLineChecks.ReversalSufficiency(lines, reversing, new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 10m, [(B, W1)] = 1m }));
        var error = StockLineChecks.ReversalSufficiency(lines, reversing, new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 9.999999m, [(B, W1)] = 1m });
        AssertError(error, ErrorCodes.InsufficientStock, "lines[0].quantity", "lines[2].quantity");
        Assert.Contains("9.999999", error!.Errors!["lines[0].quantity"][0]);
        // Nothing left at all.
        AssertError(StockLineChecks.ReversalSufficiency(lines, reversing, new Dictionary<(Guid, Guid), decimal>()),
            ErrorCodes.InsufficientStock, "lines[0].quantity", "lines[1].quantity", "lines[2].quantity");
    }

    [Fact]
    public void R16_Reversing_an_issue_always_passes()
    {
        List<StockLineValues> lines = [new(A, 40m)];

        Assert.Null(StockLineChecks.ReversalSufficiency(lines, [new(A, W1, 40m)], new Dictionary<(Guid, Guid), decimal>()));
    }

    [Fact]
    public void R16_E7_Reversing_a_transfer_needs_the_goods_in_the_destination_whatever_the_source_holds()
    {
        // Transferred 30 of A from W1 to W2; 10 were issued from W2 since.
        List<StockLineValues> lines = [new(A, 30m)];
        List<StockMovement> reversing = [new(A, W1, 30m), new(A, W2, -30m)];

        AssertError(
            StockLineChecks.ReversalSufficiency(lines, reversing, new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 70m, [(A, W2)] = 20m }),
            ErrorCodes.InsufficientStock, "lines[0].quantity");
        Assert.Null(StockLineChecks.ReversalSufficiency(lines, reversing, new Dictionary<(Guid, Guid), decimal> { [(A, W2)] = 30m }));
    }
}
