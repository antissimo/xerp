using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 009, AC-48, AC-49, AC-50 and AC-54 (spec 010: AC-49): the link from a stock document to an order — its
/// form, what saving checks (the order exists and is open, the lines are lines of the order, warehouse and
/// articles agree) and that it never changes. Written once for every kind of order (ADR-0016).
/// </summary>
public abstract class OrderLinkTests(XerpFixture app, OrderApi o)
{
    private static async Task AssertNoDocumentsAsync(OrderSetup s, int expected = 0) =>
        Assert.Equal(expected, (await Stock.DocumentsAsync(s.Http)).Total());

    /// <summary>The setup with one confirmed order [(A, 10, 1), (B, 5, 2)] and no stock documents.</summary>
    private async Task<(OrderSetup S, JsonElement Order)> OrderedAsync()
    {
        var s = await Orders.SetupAsync(app);
        return (s, await o.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 2m)));
    }

    // ---- AC-48 ----

    [Fact]
    public async Task AC48_S010_AC49_A_linked_document_must_agree_with_the_order()
    {
        var (s, order) = await OrderedAsync();

        using var otherWarehouse = await Stock.PostAsync(s.Http, o.Document(s.W2, order.Id(), (s.A, 4, 1, null)));
        using var otherArticle = await Stock.PostAsync(s.Http, o.Document(s.W1, order.Id(), (s.B, 4, 1, null)));
        using var both = await Stock.PostAsync(s.Http, o.Document(s.W2, order.Id(), (s.A, 1, 1, null), (s.A, 4, 2, null)));
        using var unknownLine = await Stock.PostAsync(s.Http, o.Document(s.W1, order.Id(), (s.A, 4, 99, null)));

        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(otherWarehouse, "ORDER_MISMATCH", "warehouseId")));
        Assert.Equal(new[] { "lines[0].articleId" }, McpAssert.ErrorKeys(await Stock.ConflictAsync(otherArticle, "ORDER_MISMATCH", "lines[0].articleId")));
        Assert.Equal(new[] { "lines[1].articleId", "warehouseId" },
            McpAssert.ErrorKeys(await Stock.ConflictAsync(both, "ORDER_MISMATCH", "warehouseId", "lines[1].articleId")));
        Assert.Equal(new[] { "lines[0].orderLineNo" },
            McpAssert.ErrorKeys(await Stock.ConflictAsync(unknownLine, "REFERENCE_NOT_FOUND", "lines[0].orderLineNo")));
        await AssertNoDocumentsAsync(s);
        await o.AssertUnchangedAsync(s.Http, order);
    }

    [Fact]
    public async Task AC48_R21_Order_of_checks_when_saving_a_linked_document()
    {
        // Warehouse reference -> the order exists -> the order is confirmed -> line kinds -> agreement.
        var (s, order) = await OrderedAsync();
        var closed = await o.CloseAsync(s.Http, (await o.OrderedAsync(s, (s.A, 10, null, 1m))).Id());
        var random = Guid.CreateVersion7();

        using var warehouseFirst = await Stock.PostAsync(s.Http, o.Document(random, random, (s.A, 4, 1, null)));
        using var orderBeforeLines = await Stock.PostAsync(s.Http, o.Document(s.W2, random, (random, 4, 1, null)));
        using var openBeforeLines = await Stock.PostAsync(s.Http, o.Document(s.W2, closed.Id(), (random, 4, 99, null)));
        using var linesBeforeAgreement = await Stock.PostAsync(s.Http, o.Document(s.W2, order.Id(), (s.B, 4, 1, null), (s.A, 1, 99, null)));
        using var articleBeforeAgreement = await Stock.PostAsync(s.Http, o.Document(s.W2, order.Id(), (random, 4, 1, null)));
        using var serviceBeforeAgreement = await Stock.PostAsync(s.Http, o.Document(s.W2, order.Id(), (s.Service, 4, 1, null)));
        using var unitBeforeAgreement = await Stock.PostAsync(s.Http, o.Document(s.W2, order.Id(), (s.B, 4, 1, s.Box)));

        Assert.Equal(new[] { "warehouseId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(warehouseFirst, "warehouseId")));
        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(orderBeforeLines, o.LinkId)));
        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(await Stock.ConflictAsync(openBeforeLines, "ORDER_NOT_OPEN", o.LinkId)));
        Assert.Equal(new[] { "lines[1].orderLineNo" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(linesBeforeAgreement, "lines[1].orderLineNo")));
        Assert.Equal(new[] { "lines[0].articleId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(articleBeforeAgreement, "lines[0].articleId")));
        await Stock.ConflictAsync(serviceBeforeAgreement, "ARTICLE_NOT_STOCKED", "lines[0].articleId");
        await Stock.ConflictAsync(unitBeforeAgreement, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
        await AssertNoDocumentsAsync(s);
    }

    [Fact]
    public async Task AC48_R22_R23_A_linked_draft_may_use_any_unit_repeat_an_order_line_skip_lines_and_exceed_the_order()
    {
        var (s, order) = await OrderedAsync();

        // Order line 1 is 10 pcs of A: two lines for it, one in boxes, together far above the order; line 2 is not covered.
        var draft = await o.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 3, 1, s.Box), (s.A, 500, 1, null));

        Assert.Equal("draft", draft.Str("status"));
        o.AssertLinked(draft, order);
        Assert.Equal([1, 1], draft.DocumentLines().Select(l => l.GetProperty("orderLineNo").GetInt32()));
        Units.AssertLine(draft.DocumentLines()[0], "box", quantity: 3m, factor: 12m, baseQuantity: 36m);
        // R23: a draft reserves nothing of the order.
        await o.AssertUnchangedAsync(s.Http, order);
        Assert.Equal(10m, await o.OnHandAsync(s.Http, s.A, s.W1));
    }

    // ---- AC-49 ----

    [Fact]
    public async Task AC49_Only_the_fulfilment_type_may_carry_the_link()
    {
        var (s, order) = await OrderedAsync();

        foreach (var type in o.OtherTypes)
        {
            var body = o.Document(s.W1, order.Id(), (s.A, 4, 1, null)).With("type", type);
            if (type == "transfer")
                body["toWarehouseId"] = s.W2.ToString();
            using var withLines = await Stock.PostAsync(s.Http, body);
            // The same without orderLineNo on the line: the link alone is refused.
            var plain = Stock.Draft(type, s.W1, (s.A, 4)).With(o.LinkId, order.Id().ToString());
            if (type == "transfer")
                plain["toWarehouseId"] = s.W2.ToString();
            using var linkOnly = await Stock.PostAsync(s.Http, plain);

            await HttpAssert.ValidationAsync(withLines, o.LinkId);
            await HttpAssert.ValidationAsync(linkOnly, o.LinkId);
        }
        await AssertNoDocumentsAsync(s);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("5")]
    [InlineData("\"\"")]
    public async Task AC49_A_malformed_link_is_a_validation_error(string json)
    {
        var (s, order) = await OrderedAsync();
        var body = o.Document(s.W1, order.Id(), (s.A, 4, 1, null)).With(o.LinkId, JsonNode.Parse(json));

        using var response = await Stock.PostAsync(s.Http, body);

        await HttpAssert.ValidationAsync(response, o.LinkId);
        await AssertNoDocumentsAsync(s);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    public async Task AC49_R20_Every_line_of_a_linked_document_needs_an_order_line_number(string json)
    {
        var (s, order) = await OrderedAsync();
        var body = o.Document(s.W1, order.Id(), (s.A, 4, 1, null), (s.B, 1, 2, null));
        if (json == "-")
            body["lines"]![0]!.AsObject().Remove("orderLineNo");
        else
            body["lines"]![0]!["orderLineNo"] = JsonNode.Parse(json);

        using var response = await Stock.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "lines[0].orderLineNo");
        Assert.DoesNotContain("lines[1].orderLineNo", McpAssert.ErrorKeys(problem));
        await AssertNoDocumentsAsync(s);
    }

    [Fact]
    public async Task AC49_R20_An_unlinked_document_must_not_name_an_order_line()
    {
        var (s, _) = await OrderedAsync();
        var line = OrderApi.DocumentLine(s.A, 4, 1);

        using var unlinked = await Stock.PostAsync(s.Http, Stock.Draft(o.DocumentType, s.W1).With("lines", new JsonArray(line.DeepClone())));
        using var nullLink = await Stock.PostAsync(s.Http, Stock.Draft(o.DocumentType, s.W1).With(o.LinkId, null).With("lines", new JsonArray(line.DeepClone())));
        using var transfer = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2).With("lines", new JsonArray(line.DeepClone())));

        await HttpAssert.ValidationAsync(unlinked, "lines[0].orderLineNo");
        await HttpAssert.ValidationAsync(nullLink, "lines[0].orderLineNo");
        await HttpAssert.ValidationAsync(transfer, "lines[0].orderLineNo");
        await AssertNoDocumentsAsync(s);
    }

    [Fact]
    public async Task AC49_Null_link_and_null_order_line_number_mean_an_unlinked_document()
    {
        var (s, order) = await OrderedAsync();
        var body = Stock.Draft(o.DocumentType, s.W1).With(o.LinkId, null)
            .With("lines", new JsonArray(OrderApi.DocumentLine(s.A, 4, null)));

        var created = await Stock.CreateAsync(s.Http, body);
        var plain = await Stock.CreateAsync(s.Http, o.DocumentType, s.W1, (s.A, 4));

        foreach (var document in new[] { created, plain })
        {
            JsonBody.AssertNull(document, o.Link);
            JsonBody.AssertNull(Assert.Single(document.DocumentLines()), "orderLineNo");
        }
        await o.AssertUnchangedAsync(s.Http, order);
    }

    [Fact]
    public async Task AC49_E16_The_link_must_name_an_order_of_this_kind_in_the_tenant()
    {
        var (s, order) = await OrderedAsync();
        var document = await Stock.CreateAsync(s.Http, o.DocumentType, s.W1, (s.A, 1));
        var draftOrder = await o.DraftAsync(s, (s.A, 10, null, 1m));

        using var random = await Stock.PostAsync(s.Http, o.Document(s.W1, Guid.CreateVersion7(), (s.A, 4, 1, null)));
        using var stockDocument = await Stock.PostAsync(s.Http, o.Document(s.W1, document.Id(), (s.A, 4, 1, null)));
        using var partner = await Stock.PostAsync(s.Http, o.Document(s.W1, o.PartnerOf(s).Id(), (s.A, 4, 1, null)));
        using var draft = await Stock.PostAsync(s.Http, o.Document(s.W1, draftOrder.Id(), (s.A, 4, 1, null)));

        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(random, o.LinkId)));
        await HttpAssert.ReferenceNotFoundAsync(stockDocument, o.LinkId);
        await HttpAssert.ReferenceNotFoundAsync(partner, o.LinkId);
        Assert.Equal(new[] { o.LinkId }, McpAssert.ErrorKeys(await Stock.ConflictAsync(draft, "ORDER_NOT_OPEN", o.LinkId)));
        await AssertNoDocumentsAsync(s, expected: 1);
        await o.AssertUnchangedAsync(s.Http, order);
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_The_link_never_changes()
    {
        var (s, order) = await OrderedAsync();
        var other = await o.OrderedAsync(s, (s.A, 10, null, 1m));
        var draft = await o.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 4, 1, null));

        var replaced = await Stock.ReplaceAsync(s.Http, draft.Id(),
            OrderApi.DocumentReplacement(s.W1, (s.B, 2, 2, null), (s.A, 1, 1, s.Box)).With("reference", "DN-17"));

        o.AssertLinked(replaced, order);
        Assert.Equal(draft.Id(), replaced.Id());
        Assert.Equal([2, 1], replaced.DocumentLines().Select(l => l.GetProperty("orderLineNo").GetInt32()));
        Assert.Equal([s.B, s.A], replaced.DocumentLines().Select(l => l.GetProperty("article").Id()));
        Assert.Equal("DN-17", replaced.Str("reference"));

        JsonObject Valid() => OrderApi.DocumentReplacement(s.W1, (s.A, 4, 1, null));
        using var relink = await Stock.PutAsync(s.Http, draft.Id(), Valid().With(o.LinkId, other.Id().ToString()));
        using var sameLink = await Stock.PutAsync(s.Http, draft.Id(), Valid().With(o.LinkId, order.Id().ToString()));
        using var unlink = await Stock.PutAsync(s.Http, draft.Id(), Valid().With(o.LinkId, null));
        using var noLineNo = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 4)));
        using var move = await Stock.PutAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W2, (s.A, 4, 1, null)));
        using var otherArticle = await Stock.PutAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 4, 2, null)));
        using var unknownLine = await Stock.PutAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 4, 3, null)));

        await HttpAssert.ValidationAsync(relink);
        await HttpAssert.ValidationAsync(sameLink);
        await HttpAssert.ValidationAsync(unlink);
        await HttpAssert.ValidationAsync(noLineNo, "lines[0].orderLineNo");
        await Stock.ConflictAsync(move, "ORDER_MISMATCH", "warehouseId");
        await Stock.ConflictAsync(otherArticle, "ORDER_MISMATCH", "lines[0].articleId");
        await HttpAssert.ReferenceNotFoundAsync(unknownLine, "lines[0].orderLineNo");
        await Stock.AssertUnchangedAsync(s.Http, replaced);
    }

    [Fact]
    public async Task AC50_R20_An_unlinked_draft_cannot_be_given_order_lines_by_a_replace()
    {
        var (s, order) = await OrderedAsync();
        var draft = await Stock.CreateAsync(s.Http, o.DocumentType, s.W1, (s.A, 4));

        using var withLineNo = await Stock.PutAsync(s.Http, draft.Id(), OrderApi.DocumentReplacement(s.W1, (s.A, 4, 1, null)));
        using var withLink = await Stock.PutAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.A, 4)).With(o.LinkId, order.Id().ToString()));
        var nullLineNo = await Stock.ReplaceAsync(s.Http, draft.Id(),
            Stock.Replacement(s.W1).With("lines", new JsonArray(OrderApi.DocumentLine(s.A, 5, null))));

        await HttpAssert.ValidationAsync(withLineNo, "lines[0].orderLineNo");
        await HttpAssert.ValidationAsync(withLink);
        JsonBody.AssertNull(nullLineNo, o.Link);
        Assert.Equal(5m, nullLineNo.DocumentLines()[0].Quantity());
    }

    // ---- AC-54 ----

    [Fact]
    public async Task AC54_S010_AC49_The_list_filter_returns_exactly_the_documents_linked_to_the_order()
    {
        var s = await Orders.SetupAsync(app);
        await o.PrepareStockAsync(s);
        var order = await o.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 5, null, 2m));
        var other = await o.OrderedAsync(s, (s.A, 10, null, 1m));
        var posted = await o.FulfilAsync(s.Http, order, 1, 4);
        var draft = await o.DraftDocumentAsync(s.Http, order, 2, 1);
        var reversed = await o.FulfilAsync(s.Http, order, 2, 2);
        var reversing = await Stock.ReverseAsync(s.Http, reversed.Id(), Stock.NextDay);
        var ofOther = await o.FulfilAsync(s.Http, other, 1, 1);
        var unlinked = await Stock.CreateAsync(s.Http, o.DocumentType, s.W1, (s.A, 1));

        var linked = await o.DocumentsAsync(s.Http, order.Id());

        Assert.Equal(new[] { posted.Id(), draft.Id(), reversed.Id(), reversing.Id() }.Order(), linked.Select(d => d.Id()).Order());
        foreach (var summary in linked)
        {
            o.AssertLinked(summary, order);
            Assert.False(summary.TryGetProperty("lines", out _));
        }
        Assert.Equal(new[] { ofOther.Id() }, (await o.DocumentsAsync(s.Http, other.Id())).Select(d => d.Id()));
        Assert.Equal(new[] { draft.Id() }, (await o.DocumentsAsync(s.Http, order.Id(), "&status=draft")).Select(d => d.Id()));
        Assert.Equal(new[] { posted.Id(), reversing.Id() }.Order(), (await o.DocumentsAsync(s.Http, order.Id(), "&status=posted")).Select(d => d.Id()).Order());
        Assert.Empty(await o.DocumentsAsync(s.Http, order.Id(), $"&warehouseId={s.W2}"));
        Assert.Empty(await o.DocumentsAsync(s.Http, Guid.CreateVersion7()));
        // The id of something that is not an order of this kind is just an id nothing is linked to.
        Assert.Empty(await o.DocumentsAsync(s.Http, unlinked.Id()));
        using var malformed = await s.Http.GetAsync($"{Stock.Documents}?{o.LinkId}=abc");
        await HttpAssert.ValidationAsync(malformed, o.LinkId);

        // Every summary of the unfiltered list has the link property: the order's on a linked document, else null.
        var all = (await Stock.DocumentsAsync(s.Http, "?limit=500")).Items();
        foreach (var summary in all)
        {
            Assert.True(summary.TryGetProperty(o.Link, out var link), $"A list summary has no '{o.Link}': {summary}");
            var expected = linked.Any(d => d.Id() == summary.Id()) ? order.Id() : summary.Id() == ofOther.Id() ? other.Id() : (Guid?)null;
            Assert.Equal(expected, link.ValueKind == JsonValueKind.Null ? null : link.Id());
        }
        JsonBody.AssertNull(all.Single(d => d.Id() == unlinked.Id()), o.Link);
    }
}
