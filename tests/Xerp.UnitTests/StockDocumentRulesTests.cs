using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 005, AC-04: quantity (R6), numbering (R17), sufficiency (R15) and the draft/post lifecycle
/// (R9-R14) as Domain rules, without HTTP or a database.
/// </summary>
public class StockDocumentRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid Warehouse = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();

    // Spec 007: every line here is in the base unit of its article, as every line was before that spec.
    private static readonly Guid Pcs = Guid.CreateVersion7();

    private static StockLineEntry In(Guid article, decimal quantity) => new(article, Pcs, quantity);

    private static StockDocument Draft(StockDocumentType type, params (Guid, decimal)[] lines) =>
        StockDocument.Create(type, Day, Warehouse, null, null, null, lines.Select(l => new StockLineEntry(l.Item1, Pcs, l.Item2)).ToList(), T0, Actor);

    // ---- R6 quantity

    [Theory]
    [InlineData("0.000001")]
    [InlineData("1")]
    [InlineData("2.5")]
    [InlineData("1.000000")]
    [InlineData("1.0000000")] // seven digits written, six significant: the value has no seventh decimal
    [InlineData("999999999.999999")]
    public void R6_Valid_quantities(string text)
    {
        Assert.True(QuantityRules.IsValid(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.000001")]
    [InlineData("1.0000001")]
    [InlineData("0.0000001")]
    [InlineData("1000000000")]
    [InlineData("999999999.9999991")]
    public void R6_Invalid_quantities_are_rejected_not_rounded(string text)
    {
        Assert.False(QuantityRules.IsValid(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void R6_Quantities_are_exact_decimals()
    {
        Assert.Equal(0.3m, 0.1m + 0.2m);
        Assert.Equal("100", QuantityRules.Normalize(100.000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("0.000001", QuantityRules.Normalize(0.000001m).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- R17 numbering

    [Theory]
    [InlineData(StockDocumentType.Receipt, 1, "SR-000001")]
    [InlineData(StockDocumentType.Issue, 1, "SI-000001")]
    [InlineData(StockDocumentType.Receipt, 42, "SR-000042")]
    [InlineData(StockDocumentType.Issue, 999999, "SI-999999")]
    [InlineData(StockDocumentType.Receipt, 1000000, "SR-1000000")] // at least six digits, more when needed
    [InlineData(StockDocumentType.Issue, 12345678, "SI-12345678")]
    public void R17_Number_is_the_type_prefix_and_the_counter_padded_to_six_digits(StockDocumentType type, long counter, string expected)
    {
        Assert.Equal(expected, DocumentNumber.Format(type, counter));
    }

    [Fact]
    public void R17_A_counter_starts_at_one_and_grows_by_exactly_one()
    {
        var tenant = Guid.CreateVersion7();
        var counter = DocumentCounter.Start(tenant, DocumentSeries.Of(StockDocumentType.Issue));

        Assert.Equal(0, counter.LastNumber);
        Assert.Equal((tenant, StockDocumentTypeNames.Issue), (counter.TenantId, counter.DocumentType));
        Assert.Equal([1L, 2L, 3L], new[] { counter.Next(), counter.Next(), counter.Next() });
        Assert.Equal(3, counter.LastNumber);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentNumber.Format(StockDocumentType.Issue, 0));
    }

    // ---- R15 sufficiency

    private static IReadOnlyList<int> Short(Dictionary<Guid, decimal> onHand, params (Guid, decimal)[] lines) =>
        StockTestSupport.ShortIssueLines(lines.Select(l => new StockLineValues(l.Item1, l.Item2)).ToList(), onHand);

    [Fact]
    public void R15_An_issue_within_stock_has_no_short_lines_also_for_exactly_the_stock()
    {
        var onHand = new Dictionary<Guid, decimal> { [A] = 10m, [B] = 0.3m };

        Assert.Empty(Short(onHand, (A, 4m)));
        Assert.Empty(Short(onHand, (A, 10m)));
        Assert.Empty(Short(onHand, (A, 6m), (A, 4m), (B, 0.1m), (B, 0.2m)));
    }

    [Fact]
    public void R15_E6_Lines_of_one_article_are_summed_and_all_of_them_are_short_together()
    {
        var onHand = new Dictionary<Guid, decimal> { [A] = 10m };

        Assert.Equal([0, 1], Short(onHand, (A, 6m), (A, 6m)));
        Assert.Equal([0], Short(onHand, (A, 10.000001m)));
    }

    [Fact]
    public void R15_E7_Only_the_lines_of_short_articles_are_named()
    {
        var onHand = new Dictionary<Guid, decimal> { [A] = 10m, [B] = 1m };

        Assert.Equal([1], Short(new Dictionary<Guid, decimal> { [A] = 10m }, (A, 5m), (B, 1m))); // never received: zero
        Assert.Equal([1, 3], Short(onHand, (A, 5m), (B, 1m), (A, 5m), (B, 0.000001m)));
    }

    // ---- lifecycle

    [Fact]
    public void R9_A_new_document_is_a_draft_without_number_with_lines_numbered_in_the_order_given()
    {
        var document = StockDocument.Create(
            StockDocumentType.Receipt, Day, Warehouse, null, "  DN-17 ", " line 1\nline 2 ",
            [In(A, 1m), In(B, 2.5m), In(A, 0.000001m)], T0, Actor);

        Assert.Equal(7, document.Id.Version);
        Assert.Equal((StockDocumentType.Receipt, StockDocumentStatus.Draft, Day, Warehouse), (document.Type, document.Status, document.DocumentDate, document.WarehouseId));
        Assert.Null(document.Number);
        Assert.Null(document.PostedAt);
        Assert.Null(document.PostedBy);
        Assert.Equal("DN-17", document.Reference);
        Assert.Equal("line 1\nline 2", document.Note);
        Assert.Equal([(1, A, 1m), (2, B, 2.5m), (3, A, 0.000001m)], document.Lines.Select(l => (l.LineNo, l.ArticleId, l.Quantity)));
        Assert.Equal((T0, T0, Actor, Actor), (document.CreatedAt, document.UpdatedAt, document.CreatedBy, document.UpdatedBy));
    }

    [Fact]
    public void R4_R6_R7_A_document_refuses_values_that_break_its_rules()
    {
        Assert.Throws<ArgumentException>(() => Draft(StockDocumentType.Receipt));
        Assert.Throws<ArgumentException>(() => Draft(StockDocumentType.Receipt, Enumerable.Repeat((A, 1m), 201).ToArray()));
        Assert.Throws<ArgumentException>(() => Draft(StockDocumentType.Receipt, (A, 0m)));
        Assert.Throws<ArgumentException>(() => Draft(StockDocumentType.Receipt, (A, 1.0000001m)));
        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Receipt, Day, Warehouse, null, "a\nb", null, [In(A, 1m)], T0, Actor));
        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Receipt, Day, Warehouse, null, new string('r', 101), null, [In(A, 1m)], T0, Actor));
        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Receipt, Day, Warehouse, null, null, new string('n', 2001), [In(A, 1m)], T0, Actor));
        Assert.Equal(200, Draft(StockDocumentType.Receipt, Enumerable.Repeat((A, 1m), 200).ToArray()).Lines.Count);
    }

    [Fact]
    public void R10_Replacing_a_draft_changes_everything_but_type_identity_and_creation_and_renumbers_the_lines()
    {
        var editor = Guid.CreateVersion7();
        var otherWarehouse = Guid.CreateVersion7();
        var document = Draft(StockDocumentType.Issue, (A, 1m), (B, 2m), (A, 3m));
        var id = document.Id;

        var removed = document.Replace(Day.AddDays(1), otherWarehouse, null, "ref", null, [In(B, 7m)], T0.AddMinutes(1), editor);

        Assert.Equal(2, removed.Count);
        Assert.Equal((id, StockDocumentType.Issue, StockDocumentStatus.Draft), (document.Id, document.Type, document.Status));
        Assert.Equal((Day.AddDays(1), otherWarehouse, "ref"), (document.DocumentDate, document.WarehouseId, document.Reference));
        Assert.Equal([(1, B, 7m)], document.Lines.Select(l => (l.LineNo, l.ArticleId, l.Quantity)));
        Assert.Equal((T0, T0.AddMinutes(1), Actor, editor), (document.CreatedAt, document.UpdatedAt, document.CreatedBy, document.UpdatedBy));

        Assert.Empty(document.Replace(Day, Warehouse, null, null, null, [In(A, 1m), In(B, 2m)], T0, Actor));
        Assert.Equal([(1, A, 1m), (2, B, 2m)], document.Lines.Select(l => (l.LineNo, l.ArticleId, l.Quantity)));

        // A rejected replace leaves the draft untouched.
        Assert.Throws<ArgumentException>(() => document.Replace(Day, Warehouse, null, null, null, [In(A, 5m), In(B, -1m)], T0, Actor));
        Assert.Equal([(1, A, 1m), (2, B, 2m)], document.Lines.Select(l => (l.LineNo, l.ArticleId, l.Quantity)));
    }

    [Fact]
    public void R12_R14_Posting_a_receipt_sets_number_and_posting_and_gives_one_positive_entry_per_line()
    {
        var poster = Guid.CreateVersion7();
        var postedAt = T0.AddHours(1);
        var document = Draft(StockDocumentType.Receipt, (A, 1m), (B, 2m), (A, 3m));

        var entries = document.PostInBaseUnits("SR-000007", postedAt, poster);

        Assert.Equal((StockDocumentStatus.Posted, "SR-000007", postedAt, poster), (document.Status, document.Number, document.PostedAt, document.PostedBy));
        Assert.Equal((Actor, T0), (document.CreatedBy, document.CreatedAt));
        Assert.Equal([(1, A, 1m), (2, B, 2m), (3, A, 3m)], entries.Select(e => (e.LineNo, e.ArticleId, e.Quantity)));
        Assert.All(entries, e =>
        {
            Assert.Equal(7, e.Id.Version);
            Assert.Equal((document.Id, Warehouse, Day, postedAt, poster), (e.DocumentId, e.WarehouseId, e.DocumentDate, e.PostedAt, e.PostedBy));
        });
        Assert.Equal(3, entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void R14_Posting_an_issue_gives_negative_entries()
    {
        var entries = Draft(StockDocumentType.Issue, (A, 40m), (B, 0.5m)).PostInBaseUnits("SI-000001", T0, Actor);

        Assert.Equal([(1, A, -40m), (2, B, -0.5m)], entries.Select(e => (e.LineNo, e.ArticleId, e.Quantity)));
    }

    [Fact]
    public void R11_A_posted_document_is_immutable()
    {
        var document = Draft(StockDocumentType.Receipt, (A, 1m));
        document.PostInBaseUnits("SR-000001", T0, Actor);

        Assert.Throws<InvalidOperationException>(() => document.PostInBaseUnits("SR-000002", T0, Actor));
        Assert.Throws<InvalidOperationException>(() => document.Replace(Day, Warehouse, null, null, null, [In(A, 2m)], T0, Actor));
        Assert.Equal(("SR-000001", 1m), (document.Number, document.Lines.Single().Quantity));
        Assert.Throws<ArgumentException>(() => Draft(StockDocumentType.Receipt, (A, 1m)).PostInBaseUnits(" ", T0, Actor));
    }
}
