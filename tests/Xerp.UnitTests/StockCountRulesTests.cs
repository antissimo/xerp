using Xerp.Application.Common;
using Xerp.Application.Stock;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 008 (AC-02): the stock count as Domain and Application rules, without HTTP or a database - the
/// difference rule (R7), the "current" rule (R10), what posting writes (R11, R12, R14), each article once (R3),
/// the counted quantity (R4, R5), the book quantity of a draft (R6) and the reversal (R17).
/// </summary>
public class StockCountRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid C = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();
    private static readonly Guid Box = Guid.CreateVersion7();

    private static StockDocument Count(StockLineEntry[] lines, decimal[] book) =>
        StockDocument.Create(StockDocumentType.Count, Day, W1, null, null, null, lines, T0, Actor, book);

    private static List<decimal> Ones(int n) => Enumerable.Repeat(1m, n).ToList();

    private static void AssertError(AppError? error, string code, params string[] expectedKeys)
    {
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    // ---- Domain: type, number, quantity

    [Fact]
    public void R1_R15_Count_is_a_document_type_with_its_own_name_and_number_series()
    {
        Assert.Equal("count", StockDocumentType.Count.ToName());
        Assert.True(StockDocumentTypeNames.TryParse("count", out var type));
        Assert.Equal(StockDocumentType.Count, type);
        Assert.False(StockDocumentTypeNames.TryParse("Count", out _));
        Assert.Equal("SC-000001", DocumentNumber.Format(StockDocumentType.Count, 1));
        Assert.True(TransferRules.IsValidDestination(StockDocumentType.Count, W1, null));
        Assert.False(TransferRules.IsValidDestination(StockDocumentType.Count, W1, W2));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.000001", true)]
    [InlineData("999999999.999999", true)]
    [InlineData("-0.000001", false)]
    [InlineData("-1", false)]
    [InlineData("1.0000001", false)]
    [InlineData("1000000000", false)]
    public void R4_A_counted_quantity_is_zero_or_greater_and_otherwise_a_quantity(string text, bool valid)
    {
        var quantity = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(valid, QuantityRules.IsValidOn(StockDocumentType.Count, quantity));
    }

    [Theory]
    [InlineData(StockDocumentType.Receipt)]
    [InlineData(StockDocumentType.Issue)]
    [InlineData(StockDocumentType.Transfer)]
    public void R4_Zero_stays_invalid_on_the_other_types(StockDocumentType type)
    {
        Assert.False(QuantityRules.IsValidOn(type, 0m));
        Assert.True(QuantityRules.IsValidOn(type, 0.000001m));
        Assert.Throws<ArgumentException>(() =>
            StockDocument.Create(type, Day, W1, type == StockDocumentType.Transfer ? W2 : null, null, null, [new(A, Pcs, 0m)], T0, Actor));
    }

    [Fact]
    public void R5_A_counted_zero_converts_to_zero_and_a_counted_quantity_above_zero_obeys_the_conversion_rule()
    {
        Assert.True(UnitConversion.TryToBaseOn(StockDocumentType.Count, 0m, 12m, out var none));
        Assert.Equal(0m, none);
        Assert.True(UnitConversion.TryToBaseOn(StockDocumentType.Count, 8m, 12m, out var boxes));
        Assert.Equal(96m, boxes);
        // 0.000001 × 0.4 rounds to 0: something was counted, yet it would be nothing in base units.
        Assert.False(UnitConversion.TryToBaseOn(StockDocumentType.Count, 0.000001m, 0.4m, out _));
        Assert.False(UnitConversion.TryToBaseOn(StockDocumentType.Count, 999999999m, 2m, out _));
        Assert.False(UnitConversion.TryToBaseOn(StockDocumentType.Receipt, 0m, 12m, out _));
    }

    // ---- Domain: each article once

    [Fact]
    public void R3_Every_line_of_a_repeated_article_is_named_whatever_the_unit()
    {
        Assert.Empty(CountRules.RepeatedLines([A, B, C]));
        Assert.Equal([0, 2], CountRules.RepeatedLines([A, B, A]));
        Assert.Equal([0, 1, 2, 3, 4], CountRules.RepeatedLines([A, B, A, B, A]));
        // A line whose article is not known repeats nothing.
        Assert.Equal([1, 3], CountRules.RepeatedLines([null, A, null, A]));

        Assert.Throws<ArgumentException>(() => Count([new(A, Box, 1m), new(A, Pcs, 6m)], [0m, 0m]));
    }

    [Fact]
    public void R3_On_the_other_types_an_article_may_repeat()
    {
        var receipt = StockDocument.Create(StockDocumentType.Receipt, Day, W1, null, null, null, [new(A, Pcs, 1m), new(A, Box, 1m)], T0, Actor);

        Assert.Equal(2, receipt.Lines.Count);
    }

    // ---- Domain: book quantity and difference

    [Theory]
    [InlineData("97", "100", "-3")]
    [InlineData("104", "100", "4")]
    [InlineData("100", "100", "0")]
    [InlineData("0", "100", "-100")]
    [InlineData("5", "0", "5")]
    [InlineData("0", "0", "0")]
    [InlineData("1.166666", "1.166667", "-0.000001")]
    public void R7_The_difference_is_counted_minus_book(string counted, string book, string expected)
    {
        static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(D(expected), CountRules.Difference(D(counted), D(book)));
    }

    [Fact]
    public void R6_A_draft_count_keeps_the_book_quantity_given_with_the_save_and_every_save_replaces_it()
    {
        var count = Count([new(A, Pcs, 97m), new(B, Pcs, 0m)], [100m, 7m]);

        Assert.Equal([(A, 97m, (decimal?)100m), (B, 0m, 7m)], count.Lines.Select(l => (l.ArticleId, l.Quantity, l.BookQuantity)));
        // A draft has no base quantity yet, so no difference of its own: it is computed when the draft is read.
        Assert.All(count.Lines, l => Assert.Null(l.DifferenceQuantity));

        // The same lines saved again, against other stock: all book quantities are new, also of the line sent unchanged.
        count.Replace(Day, W1, null, null, null, [new(A, Pcs, 97m), new(B, Pcs, 0m), new(C, Pcs, 1m)], T0, Actor, [110m, 0m, 0m]);

        Assert.Equal([(A, (decimal?)110m), (B, 0m), (C, 0m)], count.Lines.Select(l => (l.ArticleId, l.BookQuantity)));
    }

    [Fact]
    public void R6_A_count_cannot_be_saved_without_book_quantities_and_no_other_type_takes_any()
    {
        StockLineEntry[] lines = [new(A, Pcs, 1m), new(B, Pcs, 1m)];

        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Count, Day, W1, null, null, null, lines, T0, Actor));
        Assert.Throws<ArgumentException>(() => Count(lines, [1m]));
        Assert.Throws<ArgumentException>(() => Count(lines, [1m, -1m]));
        Assert.Throws<ArgumentException>(() => Count(lines, [1m, 0.0000001m]));
        Assert.Throws<ArgumentException>(() =>
            StockDocument.Create(StockDocumentType.Receipt, Day, W1, null, null, null, lines, T0, Actor, [0m, 0m]));
        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Count, Day, W1, W2, null, null, lines, T0, Actor, [0m, 0m]));

        var receipt = StockDocument.Create(StockDocumentType.Receipt, Day, W1, null, null, null, lines, T0, Actor);
        Assert.All(receipt.Lines, l => Assert.Null(l.BookQuantity));
    }

    // ---- Domain: the count is current

    [Fact]
    public void R10_A_line_is_outdated_when_stock_no_longer_equals_its_book_quantity()
    {
        List<StockLineValues> book = [new(A, 100m), new(B, 0m), new(C, 5m)];

        Assert.Empty(CountRules.OutdatedLines(book, new Dictionary<Guid, decimal> { [A] = 100m, [C] = 5m }));
        // An article without stock has none: book 0 is current, book 5 is not.
        Assert.Equal([0, 2], CountRules.OutdatedLines(book, new Dictionary<Guid, decimal>()));
        Assert.Equal([1], CountRules.OutdatedLines(book, new Dictionary<Guid, decimal> { [A] = 100m, [B] = 0.000001m, [C] = 5m }));
        Assert.Equal([0], CountRules.OutdatedLines(book, new Dictionary<Guid, decimal> { [A] = 99.999999m, [C] = 5m }));
    }

    [Fact]
    public void R10_Only_the_quantity_is_compared_not_what_was_counted()
    {
        // Book 100, counted 97, stock is 97 by now: still outdated, although posting would write nothing (008-q, T-Q4).
        Assert.Equal([0], CountRules.OutdatedLines([new(A, 100m)], new Dictionary<Guid, decimal> { [A] = 97m }));
    }

    [Fact]
    public void R10_An_outdated_count_is_COUNT_OUTDATED_with_the_quantity_key_of_exactly_the_outdated_lines()
    {
        List<StockLineValues> book = [new(A, 100m), new(B, 7m)];

        Assert.Null(StockLineChecks.Current(book, new Dictionary<Guid, decimal> { [A] = 100m, [B] = 7m }));
        AssertError(StockLineChecks.Current(book, new Dictionary<Guid, decimal> { [A] = 100m, [B] = 6m }),
            ErrorCodes.CountOutdated, "lines[1].quantity");
        AssertError(StockLineChecks.Current(book, new Dictionary<Guid, decimal> { [A] = 90m }),
            ErrorCodes.CountOutdated, "lines[0].quantity", "lines[1].quantity");
    }

    // ---- Domain: posting

    [Fact]
    public void R11_Posting_writes_the_difference_of_every_line_and_no_entry_for_a_line_without_one()
    {
        var count = Count(
            [new(A, Pcs, 97m), new(B, Pcs, 104m), new(C, Pcs, 5m)],
            [100m, 100m, 5m]);

        var entries = count.Post("SC-000001", Ones(3), T0, Actor);

        Assert.Equal((StockDocumentStatus.Posted, "SC-000001"), (count.Status, count.Number));
        Assert.Equal([(1, A, W1, -3m), (2, B, W1, 4m)], entries.Select(e => (e.LineNo, e.ArticleId, e.WarehouseId, e.Quantity)));
        Assert.All(entries, e => Assert.Equal((count.Id, Day), (e.DocumentId, e.DocumentDate)));
        // R14: the posted line keeps what it was posted with; the difference shown is the difference written.
        Assert.Equal(
            [((decimal?)97m, (decimal?)100m, (decimal?)-3m), (104m, 100m, 4m), (5m, 5m, 0m)],
            count.Lines.Select(l => (l.BaseQuantity, l.BookQuantity, l.DifferenceQuantity)));
    }

    [Fact]
    public void R12_After_posting_stock_of_every_counted_article_equals_the_counted_base_quantity()
    {
        // Surplus, shortage, no difference, counted zero with stock, counted zero without, never received, in boxes.
        var articles = Enumerable.Range(0, 7).Select(_ => Guid.CreateVersion7()).ToArray();
        decimal[] book = [100m, 100m, 20.5m, 40m, 0m, 0m, 100m];
        StockLineEntry[] lines =
        [
            new(articles[0], Pcs, 104m), new(articles[1], Pcs, 97m), new(articles[2], Pcs, 20.5m), new(articles[3], Pcs, 0m),
            new(articles[4], Pcs, 0m), new(articles[5], Pcs, 0.000001m), new(articles[6], Box, 8m),
        ];
        decimal[] factors = [1m, 1m, 1m, 1m, 1m, 1m, 12m];
        var stock = articles.Zip(book).ToDictionary(p => p.First, p => p.Second);
        var count = Count(lines, book);

        foreach (var entry in count.Post("SC-000001", factors, T0, Actor))
            stock[entry.ArticleId] += entry.Quantity;

        Assert.All(count.Lines, l => Assert.Equal(l.BaseQuantity, stock[l.ArticleId]));
        Assert.Equal([104m, 97m, 20.5m, 0m, 0m, 0.000001m, 96m], articles.Select(a => stock[a]));
        Assert.All(stock.Values, quantity => Assert.True(quantity >= 0, "R13: a count never makes stock negative."));
    }

    [Fact]
    public void R14_A_count_without_any_difference_posts_with_a_number_and_no_entries()
    {
        var count = Count([new(A, Pcs, 100m), new(B, Pcs, 0m)], [100m, 0m]);

        var entries = count.Post("SC-000002", Ones(2), T0, Actor);

        Assert.Empty(entries);
        Assert.Equal((StockDocumentStatus.Posted, "SC-000002"), (count.Status, count.Number));
        Assert.All(count.Lines, l => Assert.Equal(0m, l.DifferenceQuantity));
    }

    [Fact]
    public void E9_Posting_converts_with_the_factor_of_that_moment_and_writes_the_difference_to_the_stored_book_quantity()
    {
        // Saved as 8 boxes of 12 against book 100; the factor is 10 when it is posted.
        var count = Count([new(A, Box, 8m)], [100m]);

        var entries = count.Post("SC-000001", [10m], T0, Actor);

        var line = count.Lines.Single();
        Assert.Equal(((decimal?)10m, (decimal?)80m, (decimal?)100m, (decimal?)-20m), (line.Factor, line.BaseQuantity, line.BookQuantity, line.DifferenceQuantity));
        Assert.Equal([-20m], entries.Select(e => e.Quantity));
    }

    [Fact]
    public void R5_Posting_refuses_a_counted_quantity_above_zero_that_no_longer_converts_and_leaves_the_draft_untouched()
    {
        var count = Count([new(A, Pcs, 5m), new(B, Box, 0.000001m)], [0m, 0m]);

        Assert.Throws<InvalidOperationException>(() => count.Post("SC-000001", [1m, 0.4m], T0, Actor));

        Assert.True(count.IsDraft);
        Assert.Null(count.Number);
        Assert.All(count.Lines, l => Assert.Equal(((decimal?)null, (decimal?)null), (l.Factor, l.BaseQuantity)));
    }

    [Fact]
    public void R11_A_count_line_moves_the_difference_and_nothing_when_there_is_none()
    {
        Assert.Equal([new StockMovement(A, W1, -3m)], StockMovements.OfCountLine(W1, new(A, 97m), 100m));
        Assert.Equal([new StockMovement(A, W1, 50m)], StockMovements.OfCountLine(W1, new(A, 50m), 0m));
        Assert.Empty(StockMovements.OfCountLine(W1, new(A, 100m), 100m));
        Assert.Empty(StockMovements.OfCountLine(W1, new(A, 0m), 0m));
        // The sign rules of the other types do not know a count: its lines move differences, not quantities.
        Assert.Throws<ArgumentOutOfRangeException>(() => StockMovements.OfLine(StockDocumentType.Count, W1, null, new(A, 97m)));
    }

    // ---- Domain: reversal

    [Fact]
    public void R17_The_reversing_count_copies_the_posted_lines_and_negates_the_entries()
    {
        var count = Count([new(A, Box, 8m), new(B, Pcs, 104m), new(C, Pcs, 5m)], [100m, 100m, 5m]);
        var entries = count.Post("SC-000001", [12m, 1m, 1m], T0, Actor);

        var (reversal, reversing) = count.Reverse(entries, Day, "wrong shelf", "SC-000002", T0.AddHours(1), Actor);

        Assert.Equal((StockDocumentType.Count, StockDocumentStatus.Posted, "SC-000002", (Guid?)count.Id), (reversal.Type, reversal.Status, reversal.Number, reversal.ReversalOfId));
        Assert.Equal((StockDocumentStatus.Reversed, (Guid?)reversal.Id), (count.Status, count.ReversedById));
        // The lines say what the original counted - the same sign of the difference - the entries what the reversal did.
        Assert.Equal(
            count.Lines.Select(l => (l.LineNo, l.ArticleId, l.UnitId, l.Quantity, l.Factor, l.BaseQuantity, l.BookQuantity, l.DifferenceQuantity)),
            reversal.Lines.Select(l => (l.LineNo, l.ArticleId, l.UnitId, l.Quantity, l.Factor, l.BaseQuantity, l.BookQuantity, l.DifferenceQuantity)));
        Assert.Equal([(1, A, W1, 4m), (2, B, W1, -4m)], reversing.Select(e => (e.LineNo, e.ArticleId, e.WarehouseId, e.Quantity)));
        Assert.Equal(0m, entries.Concat(reversing).Where(e => e.ArticleId == A).Sum(e => e.Quantity));
    }

    [Fact]
    public void R17_A_posted_count_that_wrote_no_entries_is_reversed_like_any_other()
    {
        var count = Count([new(A, Pcs, 100m)], [100m]);
        var entries = count.Post("SC-000001", Ones(1), T0, Actor);
        Assert.Empty(entries);

        var (reversal, reversing) = count.Reverse(entries, Day, null, "SC-000002", T0, Actor);

        Assert.Empty(reversing);
        Assert.Equal(("SC-000002", StockDocumentStatus.Posted, (Guid?)count.Id), (reversal.Number, reversal.Status, reversal.ReversalOfId));
        Assert.Equal(StockDocumentStatus.Reversed, count.Status);
        Assert.Equal((decimal?)100m, reversal.Lines.Single().BookQuantity);
    }

    [Fact]
    public void R17_A_count_is_reversed_with_exactly_its_entries_and_the_other_types_still_need_some()
    {
        var count = Count([new(A, Pcs, 97m), new(B, Pcs, 104m)], [100m, 100m]);
        var entries = count.Post("SC-000001", Ones(2), T0, Actor);

        // A count that wrote entries cannot be reversed without them, or with only some.
        Assert.Throws<ArgumentException>(() => count.Reverse([], Day, null, "SC-000002", T0, Actor));
        Assert.Throws<ArgumentException>(() => count.Reverse([entries[0]], Day, null, "SC-000002", T0, Actor));
        Assert.Equal(StockDocumentStatus.Posted, count.Status);

        var receipt = StockDocument.Create(StockDocumentType.Receipt, Day, W1, null, null, null, [new(A, Pcs, 1m)], T0, Actor);
        receipt.PostInBaseUnits("SR-000001", T0, Actor);
        Assert.Throws<ArgumentException>(() => receipt.Reverse([], Day, null, "SR-000002", T0, Actor));
    }

    [Fact]
    public void R18_Reversing_a_surplus_needs_it_still_on_hand_and_a_shortage_always_reverses()
    {
        // Line 1 found 3 less (the reversal brings 3 back), line 2 found 4 more (the reversal takes 4 out).
        List<StockLineValues> lines = [new(A, 97m), new(B, 104m)];
        List<StockMovement> movements = [new(A, W1, 3m), new(B, W1, -4m)];

        Assert.Null(StockLineChecks.ReversalSufficiency(lines, movements,
            new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 0m, [(B, W1)] = 4m }));
        AssertError(
            StockLineChecks.ReversalSufficiency(lines, movements, new Dictionary<(Guid, Guid), decimal> { [(B, W1)] = 3.999999m }),
            ErrorCodes.InsufficientStock, "lines[1].quantity");
    }

    // ---- Application: input rules that differ by type

    private static CreateStockDocumentInput Document(string type, params StockLineInput[] lines) =>
        new(type, "2026-10-09", W1.ToString(), lines);

    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        AssertError(result.Error, ErrorCodes.ValidationFailed, expectedKeys);
    }

    [Fact]
    public void R4_Create_accepts_zero_on_a_count_only()
    {
        var count = StockDocumentValidation.Create(Document("count", new StockLineInput(A.ToString(), 0m), new StockLineInput(B.ToString(), 20.5m)));

        Assert.True(count.IsSuccess);
        Assert.Equal(StockDocumentType.Count, count.Value!.Type);
        Assert.Equal([0m, 20.5m], count.Value.Values.Lines.Select(l => l.Quantity));
        AssertInvalid(StockDocumentValidation.Create(Document("count", new StockLineInput(A.ToString(), -1m))), "lines[0].quantity");
        AssertInvalid(StockDocumentValidation.Create(Document("count", new StockLineInput(A.ToString(), 1.0000001m))), "lines[0].quantity");
        AssertInvalid(StockDocumentValidation.Create(Document("count", new StockLineInput(A.ToString(), null))), "lines[0].quantity");
        AssertInvalid(StockDocumentValidation.Create(Document("receipt", new StockLineInput(A.ToString(), 0m))), "lines[0].quantity");
        AssertInvalid(StockDocumentValidation.Create(Document("issue", new StockLineInput(A.ToString(), 0m))), "lines[0].quantity");
        // An unknown type is judged like a receipt.
        AssertInvalid(StockDocumentValidation.Create(Document("return", new StockLineInput(A.ToString(), 0m))), "type", "lines[0].quantity");
    }

    [Fact]
    public void R3_Create_refuses_a_repeated_article_on_a_count_only_with_the_key_of_every_such_line()
    {
        StockLineInput[] lines = [new(A.ToString(), 5m), new(B.ToString(), 1m), new(A.ToString(), 2m, Box.ToString())];

        AssertInvalid(StockDocumentValidation.Create(Document("count", lines)), "lines[0].articleId", "lines[2].articleId");
        Assert.True(StockDocumentValidation.Create(Document("receipt", lines)).IsSuccess);
        // With the other invalid fields, all together; a malformed articleId repeats nothing.
        AssertInvalid(
            StockDocumentValidation.Create(new CreateStockDocumentInput("count", "2026-10-09", W1.ToString(),
                [new(A.ToString(), -1m), new("abc", 1m), new(A.ToString(), 1m), new("abc", 1m)], ToWarehouseId: W2.ToString())),
            "toWarehouseId", "lines[0].quantity", "lines[0].articleId", "lines[1].articleId", "lines[2].articleId", "lines[3].articleId");
    }

    [Fact]
    public void R1_A_count_has_no_destination()
    {
        AssertInvalid(StockDocumentValidation.Create(new CreateStockDocumentInput("count", "2026-10-09", W1.ToString(),
            [new(A.ToString(), 1m)], ToWarehouseId: W2.ToString())), "toWarehouseId");
        Assert.True(StockDocumentValidation.Create(new CreateStockDocumentInput("count", "2026-10-09", W1.ToString(),
            [new(A.ToString(), 1m)], ToWarehouseId: null)).IsSuccess);
        Assert.Equal(StockDocumentType.Count, StockDocumentValidation.List(new ListStockDocumentsInput("count")).Value!.Type);
    }

    [Fact]
    public void E10_A_replace_has_no_type_so_the_stored_type_decides_on_zero_repeats_and_destination()
    {
        var replace = new ReplaceStockDocumentInput
        {
            DocumentDate = "2026-10-09", WarehouseId = W1.ToString(), Reference = null, Note = null, PartnerId = null,
            Lines = [new(A.ToString(), 0m), new(B.ToString(), 1m), new(A.ToString(), 2m)],
        };

        // The form alone passes: a zero and a repeat are each acceptable on some type.
        var values = StockDocumentValidation.Replace(replace);
        Assert.True(values.IsSuccess);

        AssertError(StockDocumentValidation.OfType(StockDocumentType.Count, values.Value!), ErrorCodes.ValidationFailed,
            "lines[0].articleId", "lines[2].articleId");
        AssertError(StockDocumentValidation.OfType(StockDocumentType.Receipt, values.Value!), ErrorCodes.ValidationFailed, "lines[0].quantity");
        AssertError(StockDocumentValidation.OfType(StockDocumentType.Transfer, values.Value!), ErrorCodes.ValidationFailed,
            "toWarehouseId", "lines[0].quantity");
        Assert.Null(StockDocumentValidation.OfType(StockDocumentType.Count, values.Value! with { Lines = [new(A, 0m), new(B, 1m)] }));
        AssertError(StockDocumentValidation.OfType(StockDocumentType.Count, values.Value! with { ToWarehouseId = W2, Lines = [new(A, 0m)] }),
            ErrorCodes.ValidationFailed, "toWarehouseId");
        // No document to ask: a zero is valid on a count only, so it stays a validation error (005/R8, AC-74).
        AssertError(StockDocumentValidation.WithoutDocument(values.Value!), ErrorCodes.ValidationFailed, "lines[0].quantity");
        Assert.Null(StockDocumentValidation.WithoutDocument(values.Value! with { Lines = [new(A, 1m), new(A, 2m)] }));
        // Negative is invalid on every type, so the form already refuses it.
        AssertInvalid(StockDocumentValidation.Replace(replace with { Lines = [new(A.ToString(), -1m)] }), "lines[0].quantity");
    }

    // ---- Application: lines of a count against the masters

    private static StockLineFacts Facts() => new(
        new Dictionary<Guid, ArticleFacts>
        {
            [A] = new ArticleFacts(true, ArticleType.Stock, Pcs),
            [B] = new ArticleFacts(true, ArticleType.Stock, Pcs),
        },
        new Dictionary<Guid, bool> { [Pcs] = true, [Box] = true },
        new Dictionary<(Guid, Guid), decimal> { [(A, Box)] = 0.4m });

    [Fact]
    public void R5_Saving_a_count_accepts_a_counted_zero_in_any_unit_of_the_article_and_refuses_what_rounds_to_zero()
    {
        var empty = new HashSet<Guid>();

        var saved = StockLineChecks.References([new(A, 0m, Box), new(B, 0m)], Facts(), empty, empty, StockDocumentType.Count);
        Assert.True(saved.IsSuccess);
        Assert.Equal([new StockLineEntry(A, Box, 0m), new StockLineEntry(B, Pcs, 0m)], saved.Value);

        var rounds = StockLineChecks.References([new(A, 0.000001m, Box)], Facts(), empty, empty, StockDocumentType.Count);
        AssertError(rounds.Error, ErrorCodes.QuantityNotConvertible, "lines[0].quantity");
        // The quantity plays no part in whether the unit is a unit of the article (008-q, T-Q2).
        var notOnArticle = StockLineChecks.References([new(B, 0m, Box)], Facts(), empty, empty, StockDocumentType.Count);
        AssertError(notOnArticle.Error, ErrorCodes.UnitNotOnArticle, "lines[0].unitId");

        var converted = StockLineChecks.Convert([new(A, Box, 0m), new(A, Box, 10m)], Facts(), StockDocumentType.Count);
        Assert.Equal([(0.4m, 0m), (0.4m, 4m)], converted.Value!.Select(c => (c.Factor, c.BaseQuantity)));
    }
}
