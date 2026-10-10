using Xerp.Application.ArticleUnits;
using Xerp.Application.Common;
using Xerp.Application.Stock;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 007: lines in a unit of their article as Domain and Application rules, without HTTP or a database -
/// what a line keeps (R12, R17), that the ledger is in base units (R19), that a reversal copies the posted
/// line (R20), the order of kinds on lines (R16) and the input rules of a conversion and of a line's unit.
/// </summary>
public class StockUnitLineRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid S = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();
    private static readonly Guid Kg = Guid.CreateVersion7();
    private static readonly Guid Box = Guid.CreateVersion7();
    private static readonly Guid Pack = Guid.CreateVersion7();
    private static readonly Guid Bag = Guid.CreateVersion7();

    private static StockDocument Draft(StockDocumentType type, params StockLineEntry[] lines) =>
        StockDocument.Create(type, Day, W1, type == StockDocumentType.Transfer ? W2 : null, null, null, lines, T0, Actor);

    // ---- Domain: what a line keeps

    [Fact]
    public void R12_R17_A_draft_line_keeps_what_was_entered_and_has_no_factor_or_base_quantity()
    {
        var document = Draft(StockDocumentType.Receipt, new(A, Box, 5m), new(A, Pcs, 6m));

        Assert.Equal([(1, A, Box, 5m), (2, A, Pcs, 6m)], document.Lines.Select(l => (l.LineNo, l.ArticleId, l.UnitId, l.Quantity)));
        Assert.All(document.Lines, l => Assert.Equal(((decimal?)null, (decimal?)null), (l.Factor, l.BaseQuantity)));
        Assert.Equal(new StockLineEntry(A, Box, 5m), document.Lines[0].Entry);
        Assert.Throws<InvalidOperationException>(() => document.Lines[0].BaseValues);
    }

    [Fact]
    public void R10_Replacing_a_draft_replaces_the_unit_of_a_line_too()
    {
        var document = Draft(StockDocumentType.Receipt, new StockLineEntry(A, Box, 5m));

        document.Replace(Day, W1, null, null, null, [new(A, Pcs, 5m), new(B, Box, 1m)], T0, Actor);

        Assert.Equal([(A, Pcs, 5m), (B, Box, 1m)], document.Lines.Select(l => (l.ArticleId, l.UnitId, l.Quantity)));
    }

    [Fact]
    public void R17_R19_Posting_stores_factor_and_base_quantity_on_each_line_and_the_ledger_is_in_base_units()
    {
        var document = Draft(StockDocumentType.Receipt, new(A, Box, 5m), new(A, Pcs, 6m), new(B, Pack, 0.5m));

        var entries = document.Post("SR-000001", [12m, 1m, 0.333333m], T0, Actor);

        Assert.Equal(
            [(Box, 5m, (decimal?)12m, (decimal?)60m), (Pcs, 6m, 1m, 6m), (Pack, 0.5m, 0.333333m, 0.166667m)],
            document.Lines.Select(l => (l.UnitId, l.Quantity, l.Factor, l.BaseQuantity)));
        // Every entry is plus its line's base quantity - not the entered quantity.
        Assert.Equal([(1, A, 60m), (2, A, 6m), (3, B, 0.166667m)], entries.Select(e => (e.LineNo, e.ArticleId, e.Quantity)));
        Assert.Equal(document.Lines.Select(l => l.BaseQuantity!.Value), entries.Select(e => e.Quantity));
        Assert.Equal(new StockLineValues(A, 60m), document.Lines[0].BaseValues);
    }

    [Fact]
    public void R19_An_issue_and_a_transfer_move_base_quantities()
    {
        var issue = Draft(StockDocumentType.Issue, new StockLineEntry(A, Box, 2m)).Post("SI-000001", [12m], T0, Actor);
        var transfer = Draft(StockDocumentType.Transfer, new(A, Box, 1m), new(A, Pcs, 7m)).Post("ST-000001", [12m, 1m], T0, Actor);

        Assert.Equal([(W1, -24m)], issue.Select(e => (e.WarehouseId, e.Quantity)));
        Assert.Equal([(W1, -12m), (W2, 12m), (W1, -7m), (W2, 7m)], transfer.Select(e => (e.WarehouseId, e.Quantity)));
        // Conservation (006/R8) holds on base quantities.
        Assert.Equal(0m, transfer.Sum(e => e.Quantity));
    }

    [Fact]
    public void R15_R18_A_line_that_does_not_convert_refuses_the_posting_and_leaves_the_draft_untouched()
    {
        var document = Draft(StockDocumentType.Receipt, new(B, Pcs, 1m), new(A, Pack, 0.000002m));

        Assert.Throws<InvalidOperationException>(() => document.Post("SR-000001", [1m, 0.2m], T0, Actor));     // rounds to zero
        Assert.Throws<InvalidOperationException>(() => Draft(StockDocumentType.Receipt, new StockLineEntry(A, Pack, 999999999m))
            .Post("SR-000001", [999999.999999m], T0, Actor));                                                   // above the maximum
        Assert.Throws<ArgumentException>(() => document.Post("SR-000001", [1m], T0, Actor));                    // a factor per line
        Assert.Throws<ArgumentException>(() => document.Post("SR-000001", [1m, 0m], T0, Actor));                // a factor obeys R2

        Assert.Equal((StockDocumentStatus.Draft, null, null), (document.Status, document.Number, document.PostedAt));
        Assert.All(document.Lines, l => Assert.Equal(((decimal?)null, (decimal?)null), (l.Factor, l.BaseQuantity)));

        // With a factor that converts it posts.
        Assert.Equal([1m, 0.000001m], document.Post("SR-000001", [1m, 0.5m], T0, Actor).Select(e => e.Quantity));
    }

    [Fact]
    public void R20_A_reversal_copies_the_posted_lines_and_negates_the_entries_whatever_the_factor_is_now()
    {
        var original = Draft(StockDocumentType.Transfer, new(A, Box, 2m), new(A, Pack, 0.5m));
        var entries = original.Post("ST-000001", [12m, 0.333333m], T0, Actor);

        // Nothing about the current conversions is asked for: a reversal has no factors to be given.
        var (reversal, reversing) = original.Reverse(entries, Day, null, "ST-000002", T0.AddHours(1), Actor);

        Assert.Equal(
            original.Lines.Select(l => (l.LineNo, l.ArticleId, l.UnitId, l.Quantity, l.Factor, l.BaseQuantity)),
            reversal.Lines.Select(l => (l.LineNo, l.ArticleId, l.UnitId, l.Quantity, l.Factor, l.BaseQuantity)));
        Assert.Equal([(Box, (decimal?)12m, (decimal?)24m), (Pack, 0.333333m, 0.166667m)], reversal.Lines.Select(l => (l.UnitId, l.Factor, l.BaseQuantity)));
        Assert.Empty(reversal.Lines.Select(l => l.Id).Intersect(original.Lines.Select(l => l.Id)));
        // Net zero (006/R15) per (article, warehouse), in base units.
        var sums = entries.Concat(reversing).GroupBy(e => (e.ArticleId, e.WarehouseId)).Select(g => g.Sum(e => e.Quantity));
        Assert.All(sums, sum => Assert.Equal(0m, sum));
        // Per line: back out of the destination, back into the source.
        Assert.Equal([(W2, -24m), (W1, 24m), (W2, -0.166667m), (W1, 0.166667m)], reversing.Select(e => (e.WarehouseId, e.Quantity)));
        // The original's lines did not change.
        Assert.Equal([(decimal?)24m, 0.166667m], original.Lines.Select(l => l.BaseQuantity));
    }

    // ---- Application: the facts and the order of kinds on lines

    private static readonly StockLineFacts Facts = new(
        new Dictionary<Guid, ArticleFacts>
        {
            [A] = new(true, ArticleType.Stock, Pcs),
            [B] = new(true, ArticleType.Stock, Kg),
            [S] = new(true, ArticleType.Service, Pcs),
        },
        new Dictionary<Guid, bool> { [Pcs] = true, [Kg] = false, [Box] = true, [Pack] = false, [Bag] = false },
        new Dictionary<(Guid, Guid), decimal> { [(A, Box)] = 12m, [(A, Pack)] = 0.4m, [(B, Box)] = 50m });

    private static Result<IReadOnlyList<StockLineEntry>> References(IReadOnlyList<StockLineRequest> lines, params Guid[] unitsOnDocument) =>
        StockLineChecks.References(lines, Facts, new HashSet<Guid>(), unitsOnDocument.ToHashSet(), StockDocumentType.Receipt);

    private static void AssertError<T>(Result<T> result, string code, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), result.Error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void R5_R14_The_base_unit_has_factor_one_an_alternative_unit_its_own_and_another_unit_none()
    {
        Assert.Equal(1m, Facts.Factor(A, Pcs));
        Assert.Equal(12m, Facts.Factor(A, Box));
        Assert.Equal(50m, Facts.Factor(B, Box));   // R1: the same unit, another factor on another article
        Assert.Equal(1m, Facts.Factor(B, Kg));
        Assert.Null(Facts.Factor(B, Pcs));         // A's base unit is not a unit of B
        Assert.Null(Facts.Factor(B, Pack));
        Assert.Null(Facts.Factor(Guid.CreateVersion7(), Pcs));
    }

    [Fact]
    public void R12_A_line_without_a_unit_is_stored_in_the_base_unit_and_a_given_unit_is_kept()
    {
        var result = References([new(A, 5m), new(A, 5m, Pcs), new(A, 5m, Box), new(B, 2m), new(B, 2m, Box)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [new StockLineEntry(A, Pcs, 5m), new(A, Pcs, 5m), new(A, Box, 5m), new(B, Kg, 2m), new(B, Box, 2m)],
            result.Value);
    }

    [Fact]
    public void R12_The_base_unit_is_never_checked_for_being_active_given_or_omitted()
    {
        // Kg, the base unit of B, is inactive: 002/R9 lets an article keep it, and so does a line (E5).
        Assert.True(References([new(B, 1m)]).IsSuccess);
        Assert.True(References([new(B, 1m, Kg)]).IsSuccess);
    }

    [Fact]
    public void R12_An_inactive_alternative_unit_cannot_be_newly_assigned_but_stays_on_a_draft_that_has_it()
    {
        AssertError(References([new(A, 1m), new(A, 1m, Pack)]), ErrorCodes.ReferenceInactive, "lines[1].unitId");
        // "Newly" as 005/R5: the stored draft has a line in that unit - any line, at any position.
        Assert.True(References([new(A, 10m), new(A, 10m, Pack), new(A, 20m, Pack)], Pack).IsSuccess);
    }

    [Fact]
    public void R12_E4_A_unit_that_is_not_a_unit_of_the_lines_article_is_refused()
    {
        var active = new StockLineFacts(Facts.Articles, new Dictionary<Guid, bool> { [Pcs] = true, [Kg] = true, [Box] = true, [Pack] = true }, Facts.Factors);
        var result = StockLineChecks.References(
            [new(A, 1m, Box), new(B, 1m), new(B, 1m, Pack), new(B, 1m, Pcs), new(A, 1m, Kg)], active, new HashSet<Guid>(), new HashSet<Guid>(), StockDocumentType.Receipt);

        // Pack is a unit of A only; Pcs is A's base unit, not B's; Kg is B's base unit, not A's.
        AssertError(result, ErrorCodes.UnitNotOnArticle, "lines[2].unitId", "lines[3].unitId", "lines[4].unitId");
    }

    [Fact]
    public void R16_The_first_failing_kind_is_reported_for_all_lines_that_have_it()
    {
        var unknownArticle = Guid.CreateVersion7();
        var unknownUnit = Guid.CreateVersion7();
        StockLineRequest notConvertible = new(A, 0.000001m, Pack);

        // Unknown articles and unknown units together, before everything else.
        AssertError(References([new(A, 1m, unknownUnit), new(unknownArticle, 1m), new(A, 1m, Bag), new(B, 1m, Pack), new(S, 1m)]),
            ErrorCodes.ReferenceNotFound, "lines[0].unitId", "lines[1].articleId");
        AssertError(References([new(unknownArticle, 1m, unknownUnit)]),
            ErrorCodes.ReferenceNotFound, "lines[0].articleId", "lines[0].unitId");
        // Inactive before "not a stock article": a unit's activity is judged without asking whose unit it is.
        AssertError(References([new(S, 1m), new(A, 1m, Bag), new(B, 1m, Pcs)]),
            ErrorCodes.ReferenceInactive, "lines[1].unitId");
        // Not a stock article before a unit that is not on the article.
        AssertError(References([new(B, 1m, Pcs), new(S, 1m), notConvertible], Pack),
            ErrorCodes.ArticleNotStocked, "lines[1].articleId");
        // A unit that is not on the article before a quantity that does not convert.
        AssertError(References([notConvertible, new(B, 1m, Pcs)], Pack),
            ErrorCodes.UnitNotOnArticle, "lines[1].unitId");
        // Only then the conversion, and only the lines that do not convert.
        AssertError(References([notConvertible, new(A, 1m, Pack), new(B, 20000000m, Box), new(A, 0.000001m)], Pack),
            ErrorCodes.QuantityNotConvertible, "lines[0].quantity", "lines[2].quantity");
    }

    [Fact]
    public void R15_R17_Conversion_uses_the_factors_it_is_given_and_reports_the_lines_that_do_not_convert()
    {
        List<StockLineEntry> lines = [new(A, Box, 5m), new(A, Pcs, 6m), new(A, Pack, 0.000002m)];
        StockLineFacts With(decimal box, decimal pack) =>
            new(Facts.Articles, Facts.Units, new Dictionary<(Guid, Guid), decimal> { [(A, Box)] = box, [(A, Pack)] = pack });

        var converted = StockLineChecks.Convert(lines, With(12m, 0.5m), StockDocumentType.Receipt);
        Assert.True(converted.IsSuccess);
        Assert.Equal(
            [new ConvertedLine(A, 12m, 60m), new(A, 1m, 6m), new(A, 0.5m, 0.000001m)],
            converted.Value);
        Assert.Equal(new StockLineValues(A, 60m), converted.Value[0].BaseValues);

        // The same lines after the factors changed (E7, E8).
        Assert.Equal(50m, StockLineChecks.Convert(lines, With(10m, 0.5m), StockDocumentType.Receipt).Value![0].BaseQuantity);
        AssertError(StockLineChecks.Convert(lines, With(10m, 0.2m), StockDocumentType.Receipt), ErrorCodes.QuantityNotConvertible, "lines[2].quantity");
        AssertError(StockLineChecks.Convert([new(A, Box, 999999999m)], With(999999.999999m, 1m), StockDocumentType.Receipt), ErrorCodes.QuantityNotConvertible, "lines[0].quantity");

        // A line in a unit that is not a unit of its article cannot exist on a saved document.
        Assert.Throws<InvalidOperationException>(() => StockLineChecks.Convert([new(B, Pack, 1m)], Facts, StockDocumentType.Receipt));
    }

    [Fact]
    public void R19_Sufficiency_counts_base_quantities()
    {
        // 30 pcs on hand: 2 box (24) and 6 pcs are covered, 3 box (36) are not, 1 box and 20 pcs are not.
        var onHand = new Dictionary<Guid, decimal> { [A] = 30m };
        IReadOnlyList<StockLineValues> Base(params StockLineEntry[] lines) =>
            StockLineChecks.Convert(lines, Facts, StockDocumentType.Receipt).Value!.Select(c => c.BaseValues).ToList();

        Assert.Null(StockTestSupport.IssueSufficiency(Base(new(A, Box, 2m), new(A, Pcs, 6m)), onHand));
        Assert.Equal(["lines[0].quantity"], StockTestSupport.IssueSufficiency(Base(new StockLineEntry(A, Box, 3m)), onHand)!.Errors!.Keys);
        Assert.Equal(["lines[0].quantity", "lines[1].quantity"], StockTestSupport.IssueSufficiency(Base(new(A, Box, 1m), new(A, Pcs, 20m)), onHand)!.Errors!.Keys.Order());
    }

    // ---- Application: input rules

    [Fact]
    public void R12_A_lines_unit_is_optional_and_must_be_a_uuid_when_given()
    {
        var w = Guid.CreateVersion7().ToString();
        CreateStockDocumentInput Receipt(params StockLineInput[] lines) => new("receipt", "2026-10-09", w, lines);

        var valid = StockDocumentValidation.Create(Receipt(new(A.ToString(), 5m, Box.ToString()), new(A.ToString(), 6m), new(A.ToString(), 7m, null)));
        Assert.True(valid.IsSuccess);
        Assert.Equal([new StockLineRequest(A, 5m, Box), new(A, 6m, null), new(A, 7m, null)], valid.Value.Values.Lines);

        foreach (var unit in new[] { "abc", "", "box", "12" })
        {
            var invalid = StockDocumentValidation.Create(Receipt(new(A.ToString(), 1m), new(A.ToString(), 1m, unit)));
            Assert.Equal(ErrorCodes.ValidationFailed, invalid.Error!.Code);
            Assert.Equal(["lines[1].unitId"], invalid.Error.Errors!.Keys);
        }
        // Reported together with the other invalid fields of the request.
        var both = StockDocumentValidation.Create(Receipt(new StockLineInput(A.ToString(), 0m, "abc")));
        Assert.Equal(["lines[0].quantity", "lines[0].unitId"], both.Error!.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("0.000001")]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("999999.999999")]
    public void R2_E1_Set_accepts_a_valid_factor(string factor)
    {
        var value = decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(value, ArticleUnitValidation.Set(new SetArticleUnitInput(value)).Value!.Factor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0000001")]
    [InlineData("1000000")]
    public void R2_E1_Set_rejects_a_missing_or_invalid_factor_under_its_key(string? factor)
    {
        decimal? value = factor is null ? null : decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        AssertError(ArticleUnitValidation.Set(new SetArticleUnitInput(value)), ErrorCodes.ValidationFailed, "factor");
    }

    [Fact]
    public void List_of_article_units_takes_paging_only()
    {
        Assert.Equal(new ArticleUnitListQuery(50, 0), ArticleUnitValidation.List(new ListArticleUnitsInput()).Value);
        Assert.Equal(new ArticleUnitListQuery(2, 1), ArticleUnitValidation.List(new ListArticleUnitsInput(2, 1)).Value);
        AssertError(ArticleUnitValidation.List(new ListArticleUnitsInput(0, -1)), ErrorCodes.ValidationFailed, "limit", "offset");
        AssertError(ArticleUnitValidation.List(new ListArticleUnitsInput(501)), ErrorCodes.ValidationFailed, "limit");
    }
}
