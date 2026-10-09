using System.Net;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 007, AC-40 to AC-44 and AC-50 to AC-55: a stock document line may name a unit of its article; the
/// document shows what was entered and what it is in base units; the ledger, stock on hand and every stock
/// rule count base quantities; conversion rounds to six decimals, half away from zero.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockUnitLineTests(XerpFixture app)
{
    private static async Task AssertNothingCreatedAsync(UnitSetup s) =>
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());

    private static JsonObject WithUnit(JsonObject body, int line, JsonNode? unitId)
    {
        body["lines"]![line]!.AsObject()["unitId"] = unitId;
        return body;
    }

    // ---- AC-40, AC-41 ----

    [Fact]
    public async Task AC40_A_line_in_an_alternative_unit_shows_what_was_entered_and_the_base_quantity()
    {
        var s = await Units.SetupAsync(app);

        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));

        var line = Assert.Single(draft.DocumentLines());
        Units.AssertLine(line, "box", 5m, 12m, 60m);
        Assert.Equal(s.Box, line.GetProperty("unit").Id());
        Assert.Equal("Box", line.GetProperty("unit").Str("name"));
        Assert.Equal(s.Pcs, line.GetProperty("baseUnit").Id());
        Assert.Equal("Piece", line.GetProperty("baseUnit").Str("name"));
        Assert.Equal(s.A, line.GetProperty("article").Id());
        McpAssert.JsonEqual(draft, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the created draft");
        // A draft moves nothing (005/R9).
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("null")]
    [InlineData("base")]
    public async Task AC41_A_line_without_a_unit_is_in_the_base_unit(string unit)
    {
        var s = await Units.SetupAsync(app);
        var body = Stock.Draft("receipt", s.W1, (s.A, 7.5m), (s.B, 2));
        if (unit == "null")
            WithUnit(WithUnit(body, 0, null), 1, null);
        else if (unit == "base")
            WithUnit(WithUnit(body, 0, s.Pcs.ToString()), 1, s.Pcs.ToString());

        var draft = await Stock.CreateAsync(s.Http, body);

        var lines = draft.DocumentLines();
        Units.AssertLine(lines[0], "pcs", 7.5m, 1m, 7.5m);
        Units.AssertLine(lines[1], "pcs", 2m, 1m, 2m);
        Assert.Equal(s.Pcs, lines[0].GetProperty("unit").Id());
        // E5: posting it gives what a line without unitId gave before this spec.
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        Units.AssertLine(posted.DocumentLines()[0], "pcs", 7.5m, 1m, 7.5m);
        Assert.Equal(new[] { 7.5m, 2m }, (await Stock.EntriesAsync(s.Http, posted.Id())).Select(e => e.Quantity()).ToArray());
        Assert.Equal(7.5m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-42, AC-43 ----

    [Fact]
    public async Task AC42_Line_unit_errors()
    {
        var s = await Units.SetupAsync(app);

        using var malformed = await Stock.PostAsync(s.Http, WithUnit(Stock.Draft("receipt", s.W1, (s.A, 1)), 0, "abc"));
        using var number = await Stock.PostAsync(s.Http, WithUnit(Stock.Draft("receipt", s.W1, (s.A, 1)), 0, 12));
        using var random = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 1, Guid.NewGuid())));
        // E4: box is a unit of A only.
        using var ofAnotherArticle = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.B, 1, s.Box)));
        using var noConversion = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 1, s.Pack)));

        await HttpAssert.ValidationAsync(malformed, "lines[0].unitId");
        await HttpAssert.ValidationAsync(number, "lines[0].unitId");
        await Stock.ConflictAsync(random, "REFERENCE_NOT_FOUND", "lines[0].unitId");
        await Stock.ConflictAsync(ofAnotherArticle, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await Stock.ConflictAsync(noConversion, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");

        // An inactive unit that is a unit of A.
        await Units.SetAsync(s.Http, s.A, s.Pack, 6);
        await Units.SetUnitActiveAsync(s.Http, s.PackUnit, false);
        using var inactive = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 1, s.Pack)));
        await Stock.ConflictAsync(inactive, "REFERENCE_INACTIVE", "lines[0].unitId");

        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC42_The_key_names_the_line_with_the_wrong_unit()
    {
        var s = await Units.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http,
            Units.Draft("receipt", s.W1, (s.A, 1, s.Box), (s.B, 1, null), (s.B, 1, s.Box), (s.A, 1, s.Pack)));

        var problem = await Stock.ConflictAsync(response, "UNIT_NOT_ON_ARTICLE");
        Assert.Equal(new[] { "lines[2].unitId", "lines[3].unitId" }, McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC42_Line_unit_errors_on_replace_leave_the_draft_unchanged()
    {
        var s = await Units.SetupAsync(app);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));

        using var malformed = await Stock.PutAsync(s.Http, draft.Id(), WithUnit(Stock.Replacement(s.W1, (s.A, 1)), 0, "abc"));
        using var random = await Stock.PutAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 1, Guid.NewGuid())));
        using var notOnArticle = await Stock.PutAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.B, 1, s.Box)));

        await HttpAssert.ValidationAsync(malformed, "lines[0].unitId");
        await Stock.ConflictAsync(random, "REFERENCE_NOT_FOUND", "lines[0].unitId");
        await Stock.ConflictAsync(notOnArticle, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
    }

    [Fact]
    public async Task AC42_R12_A_draft_keeps_a_unit_deactivated_since_but_cannot_take_a_new_inactive_one()
    {
        // R12: "newly" as 005/R5 — the stored draft has no line with that unit.
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 6);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));
        await Units.SetUnitActiveAsync(s.Http, s.BoxUnit, false);
        await Units.SetUnitActiveAsync(s.Http, s.PackUnit, false);

        var kept = await Stock.ReplaceAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 2, s.Box), (s.A, 3, s.Box)));
        using var newInactive = await Stock.PutAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 2, s.Box), (s.A, 1, s.Pack)));

        Units.AssertLine(kept.DocumentLines()[0], "box", 2m, 12m, 24m);
        Units.AssertLine(kept.DocumentLines()[1], "box", 3m, 12m, 36m);
        var problem = await Stock.ConflictAsync(newInactive, "REFERENCE_INACTIVE");
        Assert.Equal(new[] { "lines[1].unitId" }, McpAssert.ErrorKeys(problem));
        await Stock.AssertUnchangedAsync(s.Http, kept);
        // R18: posting does not re-check units for being active.
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC43_An_unknown_reference_is_reported_before_a_unit_that_is_not_on_the_article()
    {
        var s = await Units.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.B, 1, s.Box), (Guid.NewGuid(), 1, null)));

        var problem = await Stock.ConflictAsync(response, "REFERENCE_NOT_FOUND", "lines[1].articleId");
        Assert.DoesNotContain("lines[0].unitId", McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC43_R16_Order_of_kinds_on_lines()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        var inactive = await Uom.CreateAsync(s.Http, "bag", "Bag", isActive: false);
        var notConvertible = (s.A, 0.000001m, (Guid?)s.Pack);

        // REFERENCE_NOT_FOUND covers articles and units together.
        using var notFound = await Stock.PostAsync(s.Http,
            Units.Draft("receipt", s.W1, (s.A, 1, Guid.NewGuid()), (Guid.NewGuid(), 1, null), (s.A, 1, inactive.Id()), (s.B, 1, s.Box)));
        // REFERENCE_INACTIVE before ARTICLE_NOT_STOCKED.
        using var inactiveFirst = await Stock.PostAsync(s.Http,
            Units.Draft("receipt", s.W1, (s.Service, 1, null), (s.A, 1, inactive.Id()), (s.B, 1, s.Box)));
        // ARTICLE_NOT_STOCKED before UNIT_NOT_ON_ARTICLE.
        using var notStocked = await Stock.PostAsync(s.Http,
            Units.Draft("receipt", s.W1, (s.B, 1, s.Box), (s.Service, 1, null), notConvertible));
        // UNIT_NOT_ON_ARTICLE before QUANTITY_NOT_CONVERTIBLE.
        using var notOnArticle = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, notConvertible, (s.B, 1, s.Box)));
        // Validation before all of them.
        using var invalid = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.B, 1, s.Box), (s.A, 0, null)));

        Assert.Equal(new[] { "lines[0].unitId", "lines[1].articleId" },
            McpAssert.ErrorKeys(await Stock.ConflictAsync(notFound, "REFERENCE_NOT_FOUND")));
        Assert.Equal(new[] { "lines[1].unitId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(inactiveFirst, "REFERENCE_INACTIVE")));
        Assert.Equal(new[] { "lines[1].articleId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(notStocked, "ARTICLE_NOT_STOCKED")));
        Assert.Equal(new[] { "lines[1].unitId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(notOnArticle, "UNIT_NOT_ON_ARTICLE")));
        var problem = await HttpAssert.ValidationAsync(invalid, "lines[1].quantity");
        Assert.DoesNotContain("lines[0].unitId", McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_A_draft_line_can_change_between_units()
    {
        var s = await Units.SetupAsync(app);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));

        var inBase = await Stock.ReplaceAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 5, null)));
        Units.AssertLine(Assert.Single(inBase.DocumentLines()), "pcs", 5m, 1m, 5m);

        var inBoxes = await Stock.ReplaceAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 5, s.Box)));
        Units.AssertLine(Assert.Single(inBoxes.DocumentLines()), "box", 5m, 12m, 60m);

        var explicitBase = await Stock.ReplaceAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.A, 5, s.Pcs)));
        Units.AssertLine(Assert.Single(explicitBase.DocumentLines()), "pcs", 5m, 1m, 5m);
        McpAssert.JsonEqual(explicitBase, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the replaced draft");
    }

    // ---- AC-50, AC-51 ----

    [Fact]
    public async Task AC50_Posting_a_line_in_boxes_writes_the_base_quantity_to_the_ledger()
    {
        var s = await Units.SetupAsync(app);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal("SR-000001", posted.Number());
        Units.AssertLine(Assert.Single(posted.DocumentLines()), "box", 5m, 12m, 60m);
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id()));
        Assert.Equal(60m, entry.Quantity());
        Assert.Equal("pcs", entry.GetProperty("unit").Str("code"));
        Assert.Equal(s.Pcs, entry.GetProperty("unit").Id());
        var onHand = Assert.Single((await Stock.OnHandAsync(s.Http)).Items());
        Assert.Equal(60m, onHand.Quantity());
        Assert.Equal("pcs", onHand.GetProperty("unit").Str("code"));
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Fact]
    public async Task AC51_The_same_article_in_two_units_converts_line_by_line()
    {
        var s = await Units.SetupAsync(app);

        var posted = await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 2, s.Box), (s.A, 6, null));

        Units.AssertLine(posted.DocumentLines()[0], "box", 2m, 12m, 24m);
        Units.AssertLine(posted.DocumentLines()[1], "pcs", 6m, 1m, 6m);
        var entries = await Units.AssertEntriesAreBaseQuantitiesAsync(s.Http, posted);
        Assert.Equal(new[] { (1, 24m), (2, 6m) }, entries.Select(e => (e.GetProperty("lineNo").GetInt32(), e.Quantity())).ToArray());
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    // ---- AC-52 ----

    [Fact]
    public async Task AC52_No_negative_stock_in_base_units()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 30);
        var threeBoxes = await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 3, s.Box));
        var boxAndTwenty = await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 1, s.Box), (s.A, 20, null));
        var twoBoxes = await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 2, s.Box));

        using var tooMany = await Stock.SendPostAsync(s.Http, threeBoxes.Id());
        var first = await Stock.ConflictAsync(tooMany, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(first));
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        using var together = await Stock.SendPostAsync(s.Http, boxAndTwenty.Id());
        var second = await Stock.ConflictAsync(together, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { "lines[0].quantity", "lines[1].quantity" }, McpAssert.ErrorKeys(second));
        Assert.Equal(30m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertDraftAsync(s.Http, threeBoxes.Id());
        await Stock.AssertDraftAsync(s.Http, boxAndTwenty.Id());

        var posted = await Stock.PostDocumentAsync(s.Http, twoBoxes.Id());
        Units.AssertLine(posted.DocumentLines()[0], "box", 2m, 12m, 24m);
        Assert.Equal(-24m, Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())).Quantity());
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task AC52_An_issue_in_boxes_for_exactly_the_stock_empties_the_warehouse()
    {
        var s = await Units.SetupAsync(app);
        await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 36, null));

        var posted = await Units.PostedAsync(s.Http, "issue", s.W1, (s.A, 2, s.Box), (s.A, 1, s.Box));
        using var oneMore = await Stock.SendPostAsync(s.Http, (await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 0.000001m, s.Box))).Id());

        Assert.Equal(new[] { -24m, -12m }, (await Stock.EntriesAsync(s.Http, posted.Id())).Select(e => e.Quantity()).ToArray());
        await Stock.ConflictAsync(oneMore, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0, (await Stock.OnHandAsync(s.Http)).Total());
    }

    [Fact]
    public async Task AC52_Parallel_issues_in_boxes_never_overdraw_the_stock()
    {
        // 005/R16 on base quantities: 30 pcs cover two issues of one box (12) and not a third.
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 30);
        var drafts = new List<Guid>();
        for (var i = 0; i < 8; i++)
            drafts.Add((await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 1, s.Box))).Id());

        var responses = await Task.WhenAll(drafts.Select(id => Task.Run(() => Stock.SendPostAsync(s.Http, id))));

        Assert.Equal(Enumerable.Repeat(200, 2).Concat(Enumerable.Repeat(409, 6)).ToArray(), responses.Select(r => (int)r.StatusCode).Order().ToArray());
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK", "lines[0].quantity");
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
        foreach (var response in responses)
            response.Dispose();
    }

    // ---- AC-53 ----

    [Fact]
    public async Task AC53_A_transfer_in_boxes_moves_base_quantities_and_conserves_the_total()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 30);

        var posted = await Units.PostedTransferAsync(s.Http, s.W1, s.W2, (s.A, 1, s.Box));

        Units.AssertLine(Assert.Single(posted.DocumentLines()), "box", 1m, 12m, 12m);
        var entries = await Units.AssertEntriesAreBaseQuantitiesAsync(s.Http, posted);
        Assert.Equal(new[] { (s.W1, -12m), (s.W2, 12m) }, entries.Select(e => (e.GetProperty("warehouse").Id(), e.Quantity())).ToArray());
        Assert.All(entries, e => Assert.Equal("pcs", e.GetProperty("unit").Str("code")));
        Assert.Equal(18m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(12m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
        Assert.Equal(30m, await Stock.TotalAsync(s.Http, s.A));
        // 006/R7 on base quantities: two more boxes are not there.
        var tooMany = await Stock.CreateAsync(s.Http, Units.Transfer(s.W1, s.W2, (s.A, 1, s.Box), (s.A, 7, null)));
        using var refused = await Stock.SendPostAsync(s.Http, tooMany.Id());
        var problem = await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { "lines[0].quantity", "lines[1].quantity" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(18m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    // ---- AC-54 ----

    [Fact]
    public async Task AC54_Conversion_rounds_to_six_decimals_half_away_from_zero()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.333333m);

        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 0.5m, s.Pack), (s.A, 1.5m, s.Pack));

        // 0.5 x 0.333333 = 0.1666665 -> 0.166667; 1.5 x 0.333333 = 0.4999995 -> 0.5.
        Units.AssertLine(draft.DocumentLines()[0], "pack", 0.5m, 0.333333m, 0.166667m);
        Units.AssertLine(draft.DocumentLines()[1], "pack", 1.5m, 0.333333m, 0.5m);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Units.AssertLine(posted.DocumentLines()[0], "pack", 0.5m, 0.333333m, 0.166667m);
        Units.AssertLine(posted.DocumentLines()[1], "pack", 1.5m, 0.333333m, 0.5m);
        Assert.Equal(new[] { 0.166667m, 0.5m }, (await Stock.EntriesAsync(s.Http, posted.Id())).Select(e => e.Quantity()).ToArray());
        Assert.Equal(0.666667m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Theory]
    // Below the half, above it, and exactly half (the last also tells half-away-from-zero from half-to-even).
    [InlineData("0.333333", "0.4", "0.133333")]      // 0.1333332
    [InlineData("0.333333", "0.2", "0.066667")]      // 0.0666666
    [InlineData("1.111111", "1.5", "1.666667")]      // 1.6666665 -> half away from zero
    [InlineData("1.111111", "2.5", "2.777778")]      // 2.7777775 -> half away from zero
    [InlineData("0.000001", "1.5", "0.000002")]      // 0.0000015 -> half away from zero
    [InlineData("0.000001", "2.5", "0.000003")]      // 0.0000025 -> half away from zero (to even would give 0.000002)
    [InlineData("12", "0.000001", "0.000012")]
    [InlineData("999999.999999", "1000", "999999999.999")]
    public async Task AC54_R14_Base_quantity_is_quantity_times_factor_rounded(string factor, string quantity, string expected)
    {
        var s = await Units.SetupAsync(app);
        var (f, q, b) = (Dec(factor), Dec(quantity), Dec(expected));
        await Units.SetAsync(s.Http, s.A, s.Pack, f);

        var posted = await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, q, s.Pack));

        Units.AssertLine(posted.DocumentLines()[0], "pack", q, f, b);
        Assert.Equal(b, Assert.Single(await Stock.EntriesAsync(s.Http, posted.Id())).Quantity());
        Assert.Equal(b, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    private static decimal Dec(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task AC54_Rounded_receipts_can_be_issued_back_to_exactly_zero()
    {
        // Sufficiency compares sums of baseQuantity (R19): what three rounded lines brought in, one line in the
        // base unit takes out.
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.333333m);
        await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 0.5m, s.Pack), (s.A, 0.5m, s.Pack), (s.A, 0.5m, s.Pack));
        Assert.Equal(0.500001m, await Stock.QuantityAsync(s.Http, s.A, s.W1));

        // 1.5 pack is 0.5 pcs: one issue in packs leaves the rounding difference, which is real stock.
        await Units.PostedAsync(s.Http, "issue", s.W1, (s.A, 1.5m, s.Pack));
        Assert.Equal(0.000001m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Units.PostedAsync(s.Http, "issue", s.W1, (s.A, 0.000001m, null));

        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(0m, await Stock.LedgerSumAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-55 ----

    [Fact]
    public async Task AC55_A_quantity_that_converts_to_zero_or_above_the_maximum_is_not_convertible()
    {
        var s = await Units.SetupAsync(app);

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        using var zero = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 0.000001m, s.Pack)));
        var problem = await Stock.ConflictAsync(zero, "QUANTITY_NOT_CONVERTIBLE");
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);

        await Units.SetAsync(s.Http, s.A, s.Pack, 999999.999999m);
        using var tooLarge = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 999999999m, s.Pack)));
        await Stock.ConflictAsync(tooLarge, "QUANTITY_NOT_CONVERTIBLE", "lines[0].quantity");
        await AssertNothingCreatedAsync(s);

        await Units.SetAsync(s.Http, s.A, s.Pack, 0.5m);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 0.000001m, s.Pack));
        Units.AssertLine(draft.DocumentLines()[0], "pack", 0.000001m, 0.5m, 0.000001m);
    }

    [Fact]
    public async Task AC55_R15_Only_the_lines_that_do_not_convert_are_reported_and_the_maximum_itself_converts()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        await Units.SetAsync(s.Http, s.B, s.Box, 1000m);

        using var response = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1,
            (s.A, 0.000001m, s.Pack), (s.A, 1, s.Pack), (s.B, 1000000m, s.Box), (s.A, 0.000001m, null)));
        using var replace = await Stock.PutAsync(s.Http, (await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1, s.Pack))).Id(),
            Units.Replacement(s.W1, (s.A, 1, s.Pack), (s.A, 0.000001m, s.Pack)));
        // 999999.999999 x 1000 = 999999999.999, within the maximum of a quantity.
        var atMaximum = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.B, 999999.999999m, s.Box));

        var problem = await Stock.ConflictAsync(response, "QUANTITY_NOT_CONVERTIBLE");
        Assert.Equal(new[] { "lines[0].quantity", "lines[2].quantity" }, McpAssert.ErrorKeys(problem));
        var onReplace = await Stock.ConflictAsync(replace, "QUANTITY_NOT_CONVERTIBLE");
        Assert.Equal(new[] { "lines[1].quantity" }, McpAssert.ErrorKeys(onReplace));
        Units.AssertLine(atMaximum.DocumentLines()[0], "box", 999999.999999m, 1000m, 999999999.999m, "pcs");
        Assert.Equal(2, (await Stock.DocumentsAsync(s.Http)).Total());
    }
}
