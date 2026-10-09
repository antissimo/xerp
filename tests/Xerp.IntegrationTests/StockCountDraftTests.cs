using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 008, AC-10 and AC-20 to AC-26: the draft of a stock count — counted quantity (zero allowed), each
/// article once, the book quantity recorded when the draft is saved, and the difference it shows.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockCountDraftTests(XerpFixture app)
{
    private static async Task AssertNothingCreatedAsync(UnitSetup s, int documents = 0) =>
        Assert.Equal(documents, (await Stock.DocumentsAsync(s.Http, "?status=draft")).Total());

    private static void AssertNoBook(JsonElement document)
    {
        Assert.NotEmpty(document.DocumentLines());
        foreach (var line in document.DocumentLines())
            JsonBody.AssertNull(line, "bookQuantity", "differenceQuantity");
    }

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_A_count_line_with_an_unknown_property_is_rejected_and_the_type_filter_knows_counts()
    {
        var s = await Units.SetupAsync(app);
        var book = Counts.Draft(s.W1, (s.A, 5, null));
        book["lines"]![0]!.AsObject()["bookQuantity"] = 5;
        var difference = Counts.Draft(s.W1, (s.A, 5, null));
        difference["lines"]![0]!.AsObject()["differenceQuantity"] = 0;

        using var withBook = await Stock.PostAsync(s.Http, book);
        using var withDifference = await Stock.PostAsync(s.Http, difference);
        var counts = await Stock.DocumentsAsync(s.Http, "?type=count");

        await HttpAssert.ValidationAsync(withBook);
        await HttpAssert.ValidationAsync(withDifference);
        Assert.Equal(0, counts.Total());
        await AssertNothingCreatedAsync(s);
    }

    // ---- AC-20 ----

    [Fact]
    public async Task AC20_A_draft_count_shows_the_book_quantity_and_the_difference_and_moves_nothing()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var ledger = await Stock.LedgerAsync(s.Http);

        using var response = await Stock.PostAsync(s.Http, new JsonObject
        {
            ["type"] = "count", ["documentDate"] = "2026-10-09", ["warehouseId"] = s.W1.ToString(),
            ["lines"] = new JsonArray(new JsonObject { ["articleId"] = s.A.ToString(), ["quantity"] = 97 }),
        });

        var draft = await HttpAssert.JsonAsync(response, System.Net.HttpStatusCode.Created);
        Assert.Equal("count", draft.Str("type"));
        Assert.Equal("draft", draft.Str("status"));
        JsonBody.AssertNull(draft, "toWarehouse", "number", "postedAt", "postedBy", "reversalOf", "reversedBy");
        Assert.Equal(s.W1, draft.GetProperty("warehouse").Id());
        var line = Assert.Single(draft.DocumentLines());
        Counts.AssertLine(line, quantity: 97m, baseQuantity: 97m, book: 100m, difference: -3m);
        Units.AssertLine(line, "pcs", 97m, 1m, 97m);
        McpAssert.JsonEqual(draft, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the created draft");
        // R8: no effect on stock or the ledger.
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(s.Http), "The ledger changed");
    }

    [Fact]
    public async Task AC20_R8_A_draft_count_reserves_nothing_and_blocks_no_posting()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Counts.CountAsync(s.Http, s.W1, s.A, 0);

        await Stock.IssueAsync(s.Http, s.W1, s.A, 100);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        await Stock.TransferAsync(s.Http, s.W1, s.W2, s.A, 5);

        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(5m, await Stock.QuantityAsync(s.Http, s.A, s.W2));
    }

    // ---- AC-21 ----

    [Fact]
    public async Task AC21_Lines_of_the_other_types_have_a_null_book_quantity_and_difference()
    {
        var s = await Units.SetupAsync(app);

        var receipt = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10, null), (s.A, 1, s.Box));
        AssertNoBook(receipt);
        var postedReceipt = await Stock.PostDocumentAsync(s.Http, receipt.Id());
        AssertNoBook(postedReceipt);
        var issue = await Units.CreateAsync(s.Http, "issue", s.W1, (s.A, 2, null));
        AssertNoBook(issue);
        AssertNoBook(await Stock.PostDocumentAsync(s.Http, issue.Id()));
        var transfer = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 3));
        AssertNoBook(transfer);
        var postedTransfer = await Stock.PostDocumentAsync(s.Http, transfer.Id());
        AssertNoBook(postedTransfer);
        AssertNoBook(await Stock.ReverseAsync(s.Http, postedTransfer.Id()));
        AssertNoBook(await Stock.GetAsync(s.Http, postedReceipt.Id()));
    }

    // ---- AC-22 ----

    [Fact]
    public async Task AC22_Each_article_at_most_once_on_a_count()
    {
        var s = await Units.SetupAsync(app);

        using var repeated = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, 5, null), (s.B, 1, null), (s.A, 2, null)));
        // In different units it is still the same article (ADR-0015: the caller adds the quantities up).
        using var inTwoUnits = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, 1, s.Box), (s.A, 6, null)));
        using var threeTimes = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.B, 1, null), (s.B, 1, null), (s.A, 1, null), (s.B, 1, null)));

        var problem = await HttpAssert.ValidationAsync(repeated, "lines[0].articleId", "lines[2].articleId");
        Assert.DoesNotContain("lines[1].articleId", McpAssert.ErrorKeys(problem));
        await HttpAssert.ValidationAsync(inTwoUnits, "lines[0].articleId", "lines[1].articleId");
        var three = await HttpAssert.ValidationAsync(threeTimes, "lines[0].articleId", "lines[1].articleId", "lines[3].articleId");
        Assert.DoesNotContain("lines[2].articleId", McpAssert.ErrorKeys(three));
        await AssertNothingCreatedAsync(s);

        // On the other types repeats stay allowed.
        var receipt = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, null), (s.A, 2, null));
        Assert.Equal(2, receipt.DocumentLines().Length);
    }

    [Fact]
    public async Task AC22_Count_validation_and_references()
    {
        var s = await Units.SetupAsync(app);

        using var negative = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, -1, null)));
        using var destination = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, 5, null)).With("toWarehouseId", s.W2.ToString()));
        using var service = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.Service, 1, null)));
        using var zeroReceipt = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 0)));
        using var zeroIssue = await Stock.PostAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 0)));
        using var zeroTransfer = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.A, 0)));

        await HttpAssert.ValidationAsync(negative, "lines[0].quantity");
        await HttpAssert.ValidationAsync(destination, "toWarehouseId");
        await Stock.ConflictAsync(service, "ARTICLE_NOT_STOCKED", "lines[0].articleId");
        await HttpAssert.ValidationAsync(zeroReceipt, "lines[0].quantity");
        await HttpAssert.ValidationAsync(zeroIssue, "lines[0].quantity");
        await HttpAssert.ValidationAsync(zeroTransfer, "lines[0].quantity");
        await AssertNothingCreatedAsync(s);

        // "toWarehouseId": null is the same as omitted (R1).
        var withNull = await Stock.CreateAsync(s.Http, Counts.Draft(s.W1, (s.A, 5, null)).With("toWarehouseId", null));
        JsonBody.AssertNull(withNull, "toWarehouse");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.000001")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    [InlineData("1.0000001")]
    [InlineData("1000000000")]
    public async Task AC22_E1_Invalid_counted_quantity_is_rejected_with_its_key(string quantity)
    {
        var s = await Units.SetupAsync(app);
        var body = Counts.Draft(s.W1, (s.B, 1, null), (s.A, 1, null));
        body["lines"]![1]!.AsObject()["quantity"] = JsonNode.Parse(quantity);

        using var response = await Stock.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "lines[1].quantity");
        Assert.DoesNotContain("lines[0].quantity", McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s);
    }

    [Fact]
    public async Task AC22_Count_lines_are_limited_and_follow_the_order_of_checks_of_the_other_types()
    {
        var s = await Units.SetupAsync(app);

        using var empty = await Stock.PostAsync(s.Http, Stock.Draft("count", s.W1));
        // Validation (repeat) before references (unknown warehouse, unknown article).
        using var repeatAndUnknownWarehouse = await Stock.PostAsync(s.Http, Counts.Draft(Guid.NewGuid(), (s.A, 1, null), (s.A, 2, null)));
        using var unknownWarehouse = await Stock.PostAsync(s.Http, Counts.Draft(Guid.NewGuid(), (s.A, 1, null)));
        using var unknownArticle = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, 1, null), (Guid.NewGuid(), 0, null)));
        using var wrongUnit = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.B, 0, s.Box)));

        await HttpAssert.ValidationAsync(empty, "lines");
        await HttpAssert.ValidationAsync(repeatAndUnknownWarehouse, "lines[0].articleId", "lines[1].articleId");
        await Stock.ConflictAsync(unknownWarehouse, "REFERENCE_NOT_FOUND", "warehouseId");
        await Stock.ConflictAsync(unknownArticle, "REFERENCE_NOT_FOUND", "lines[1].articleId");
        await Stock.ConflictAsync(wrongUnit, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await AssertNothingCreatedAsync(s);
    }

    // ---- AC-23 ----

    [Fact]
    public async Task AC23_Counted_zero_and_an_article_never_received()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        var zero = await Counts.CountAsync(s.Http, s.W1, s.A, 0);
        var neverReceived = await Counts.CountAsync(s.Http, s.W1, s.B, 5);
        // E5: counted 0 of an article with no stock.
        var nothing = await Counts.CountAsync(s.Http, s.W2, s.B, 0);

        Counts.AssertLine(Assert.Single(zero.DocumentLines()), quantity: 0m, baseQuantity: 0m, book: 100m, difference: -100m);
        Counts.AssertLine(Assert.Single(neverReceived.DocumentLines()), quantity: 5m, baseQuantity: 5m, book: 0m, difference: 5m);
        Counts.AssertLine(Assert.Single(nothing.DocumentLines()), quantity: 0m, baseQuantity: 0m, book: 0m, difference: 0m);
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-24 ----

    [Fact]
    public async Task AC24_A_count_line_in_boxes_is_compared_in_base_units()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);

        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.A, 8, s.Box));

        var line = Assert.Single(draft.DocumentLines());
        Units.AssertLine(line, "box", 8m, 12m, 96m);
        Counts.AssertLine(line, quantity: 8m, baseQuantity: 96m, book: 100m, difference: -4m);
    }

    [Fact]
    public async Task AC24_R5_Counted_zero_in_any_unit_converts_to_zero_and_a_positive_count_must_convert()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 0.4m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 24);

        var zeroBoxes = await Counts.CreateAsync(s.Http, s.W1, (s.A, 0, s.Box));
        using var notConvertible = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.B, 0, null), (s.A, 0.000001m, s.Pack)));

        var line = Assert.Single(zeroBoxes.DocumentLines());
        Units.AssertLine(line, "box", 0m, 12m, 0m);
        Counts.AssertLine(line, quantity: 0m, baseQuantity: 0m, book: 24m, difference: -24m);
        var problem = await Stock.ConflictAsync(notConvertible, "QUANTITY_NOT_CONVERTIBLE");
        Assert.Equal(new[] { "lines[1].quantity" }, McpAssert.ErrorKeys(problem));
        await AssertNothingCreatedAsync(s, documents: 1);
    }

    [Fact]
    public async Task AC24_E9_A_changed_factor_changes_base_quantity_and_difference_of_a_draft_but_not_the_book_quantity()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.A, 8, s.Box));

        await Units.SetAsync(s.Http, s.A, s.Box, 10);

        var read = await Stock.GetAsync(s.Http, draft.Id());
        Units.AssertLine(read.DocumentLines()[0], "box", 8m, 10m, 80m);
        Counts.AssertLine(read.DocumentLines()[0], quantity: 8m, baseQuantity: 80m, book: 100m, difference: -20m);

        // Stock did not move, so the count is current; posting writes the difference as it is then.
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Counts.AssertLine(posted.DocumentLines()[0], quantity: 8m, baseQuantity: 80m, book: 100m, difference: -20m);
        Assert.Equal(new[] { (1, -20m) }, await Counts.MovementsAsync(s.Http, posted.Id()));
        Assert.Equal(80m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-25 ----

    [Fact]
    public async Task AC25_The_book_quantity_is_taken_when_the_draft_is_saved_and_not_when_it_is_read()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 97);

        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);

        var read = await Stock.GetAsync(s.Http, draft.Id());
        Counts.AssertLine(read.DocumentLines()[0], quantity: 97m, book: 100m);
        McpAssert.JsonEqual(draft, read, "Reading the draft changed it");
        var listed = (await Stock.DocumentsAsync(s.Http, "?type=count")).Items().Single();
        Assert.Equal(draft.Str("updatedAt"), listed.Str("updatedAt"));

        var saved = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 97)));

        Counts.AssertLine(saved.DocumentLines()[0], quantity: 97m, baseQuantity: 97m, book: 110m, difference: -13m);
        McpAssert.JsonEqual(saved, await Stock.GetAsync(s.Http, draft.Id()), "GET differs from the saved draft");
    }

    [Fact]
    public async Task AC25_R6_Every_save_records_the_book_quantity_of_every_line_anew()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 7);
        var draft = await Counts.CreateAsync(s.Http, s.W1, (s.A, 97, null), (s.B, 7, null));

        await Stock.IssueAsync(s.Http, s.W1, s.A, 10);
        await Stock.IssueAsync(s.Http, s.W1, s.B, 7);
        // The replace changes only A's counted quantity; B's line is sent as it was.
        var saved = await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 90), (s.B, 7)));

        Counts.AssertLine(saved.DocumentLines()[0], quantity: 90m, book: 90m);
        Counts.AssertLine(saved.DocumentLines()[1], quantity: 7m, book: 0m);

        // A refused replace records nothing.
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1);
        using var refused = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 90), (s.A, 1)));
        await HttpAssert.ValidationAsync(refused, "lines[0].articleId", "lines[1].articleId");
        await Stock.AssertUnchangedAsync(s.Http, saved);
    }

    // ---- AC-26 ----

    [Fact]
    public async Task AC26_The_book_quantity_is_per_warehouse()
    {
        var s = await Units.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100);
        await Stock.ReceiveAsync(s.Http, s.W2, s.A, 40);

        var inW2 = await Counts.CountAsync(s.Http, s.W2, s.A, 40);
        Counts.AssertLine(inW2.DocumentLines()[0], quantity: 40m, book: 40m);

        // R6: moving the draft to the other warehouse takes that warehouse's stock.
        var moved = await Stock.ReplaceAsync(s.Http, inW2.Id(), Stock.Replacement(s.W1, (s.A, 40)));
        Assert.Equal(s.W1, moved.GetProperty("warehouse").Id());
        Counts.AssertLine(moved.DocumentLines()[0], quantity: 40m, book: 100m);
    }

    // ---- E10 ----

    [Fact]
    public async Task AC22_E10_A_replace_cannot_turn_a_receipt_into_a_count_and_a_count_keeps_its_rules()
    {
        var s = await Units.SetupAsync(app);
        var receipt = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var count = await Counts.CountAsync(s.Http, s.W1, s.A, 1);

        using var typeInBody = await Stock.PutAsync(s.Http, receipt.Id(), Stock.Replacement(s.W1, (s.A, 1)).With("type", "count"));
        // A receipt stays a receipt: quantity 0 is invalid on it.
        using var zeroOnReceipt = await Stock.PutAsync(s.Http, receipt.Id(), Stock.Replacement(s.W1, (s.A, 0)));
        // A count stays a count: zero allowed, repeats and a destination not.
        var zeroOnCount = await Stock.ReplaceAsync(s.Http, count.Id(), Stock.Replacement(s.W1, (s.A, 0)));
        using var repeatOnCount = await Stock.PutAsync(s.Http, count.Id(), Stock.Replacement(s.W1, (s.A, 1), (s.A, 2)));
        using var destinationOnCount = await Stock.PutAsync(s.Http, count.Id(), Stock.TransferReplacement(s.W1, s.W2, (s.A, 1)));

        await HttpAssert.ValidationAsync(typeInBody);
        await HttpAssert.ValidationAsync(zeroOnReceipt, "lines[0].quantity");
        Assert.Equal("count", zeroOnCount.Str("type"));
        Counts.AssertLine(zeroOnCount.DocumentLines()[0], quantity: 0m, book: 0m);
        await HttpAssert.ValidationAsync(repeatOnCount, "lines[0].articleId", "lines[1].articleId");
        await HttpAssert.ValidationAsync(destinationOnCount, "toWarehouseId");
        Assert.Equal("receipt", (await Stock.GetAsync(s.Http, receipt.Id())).Str("type"));
        JsonBody.AssertNull((await Stock.GetAsync(s.Http, receipt.Id())).DocumentLines()[0], "bookQuantity", "differenceQuantity");
    }

    [Fact]
    public async Task AC20_A_draft_count_can_be_deleted()
    {
        var s = await Units.SetupAsync(app);
        var draft = await Counts.CountAsync(s.Http, s.W1, s.A, 5);

        using var delete = await Stock.DeleteAsync(s.Http, draft.Id());

        Assert.Equal(System.Net.HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http, "?type=count")).Total());
    }
}
