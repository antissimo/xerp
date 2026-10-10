using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011a, AC-10, AC-20 to AC-23, AC-25 to AC-28, AC-30, AC-32 and AC-90: the optional partner in the header
/// of an unlinked receipt or issue — created, read, replaced, validated as a reference, refused on transfers,
/// counts and lines, and carried through posting without changing a quantity. The rules that mirror between
/// receipt / supplier and issue / customer are in <see cref="PartnerOnDocumentTests"/>.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockDocumentPartnerTests(XerpFixture app)
{
    private static readonly MasterApi P = PartnerDocs.P;

    // ---- AC-10 ----

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("123")]
    [InlineData("0199c0de-0000-7000-8000")]
    public async Task AC10_A_malformed_partnerId_filter_is_rejected_with_its_key(string value)
    {
        var s = await PartnerDocs.SetupAsync(app);

        using var response = await s.Http.GetAsync($"{Stock.Documents}?partnerId={value}");

        await HttpAssert.ValidationAsync(response, "partnerId");
    }

    [Fact]
    public async Task AC10_An_unknown_header_property_next_to_partnerId_is_rejected_with_its_key()
    {
        var s = await PartnerDocs.SetupAsync(app);

        using var create = await Stock.PostAsync(s.Http, PartnerDocs.Receipt(s, s.Sup).With("supplierId", s.Sup.ToString()));
        using var partner = await Stock.PostAsync(s.Http, PartnerDocs.Receipt(s, s.Sup).With("partner", new JsonObject { ["id"] = s.Sup.ToString() }));

        await HttpAssert.ValidationAsync(create, "supplierId");
        await HttpAssert.ValidationAsync(partner, "partner");
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 0);
    }

    // ---- AC-20 ----

    [Fact]
    public async Task AC20_A_receipt_names_a_supplier_and_an_issue_a_customer()
    {
        var s = await PartnerDocs.SetupAsync(app);

        using var response = await Stock.PostAsync(s.Http, PartnerDocs.Receipt(s, s.Sup));

        var receipt = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(("receipt", "draft", PartnerDocs.Date), (receipt.Str("type"), receipt.Str("status"), receipt.Str("documentDate")));
        PartnerDocs.AssertPartner(receipt, s.O.Sup);
        McpAssert.JsonEqual(receipt, await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, receipt.Id(), s.O.Sup), "GET differs from the create response");

        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var issue = await PartnerDocs.IssueAsync(s, s.Cus);

        PartnerDocs.AssertPartner(issue, s.O.Cus);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, issue.Id(), s.O.Cus);
        // R4: the partner is in the header only.
        Assert.All(new[] { receipt, issue }, d => Assert.All(d.DocumentLines(), l => Assert.False(l.TryGetProperty("partner", out _), $"A line has a partner: {l}")));
    }

    // ---- AC-21 ----

    [Fact]
    public async Task AC21_A_receipt_without_a_partner_is_created_and_posted_as_before()
    {
        var s = await PartnerDocs.SetupAsync(app);

        var omitted = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 5)));
        var asNull = await Stock.CreateAsync(s.Http, PartnerDocs.Body(s, "receipt", null, 7));

        PartnerDocs.AssertNoPartner(omitted);
        PartnerDocs.AssertNoPartner(asNull);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, omitted.Id(), null);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, asNull.Id(), null);

        var first = await Stock.PostDocumentAsync(s.Http, omitted.Id());
        var second = await Stock.PostDocumentAsync(s.Http, asNull.Id());

        Assert.Equal(("posted", OrderApi.Purchase.DocumentNumber(1)), (first.Str("status"), first.Number()));
        Assert.Equal(("posted", OrderApi.Purchase.DocumentNumber(2)), (second.Str("status"), second.Number()));
        PartnerDocs.AssertNoPartner(first);
        PartnerDocs.AssertNoPartner(second);
        Assert.Equal(12m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(5m, Assert.Single(await Stock.EntriesAsync(s.Http, omitted.Id())).Quantity());
        Assert.Equal(7m, Assert.Single(await Stock.EntriesAsync(s.Http, asNull.Id())).Quantity());
        await PartnerDocs.AssertBalancedAsync(s, "AC-90");
    }

    // ---- AC-22 ----

    [Fact]
    public async Task AC22_Replace_sets_changes_and_removes_the_partner_and_must_send_partnerId()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup);

        var changed = await Stock.ReplaceAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, s.Sup2));

        PartnerDocs.AssertPartner(changed, s.Sup2Partner);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, draft.Id(), s.Sup2Partner);

        // Missing: 400 with the key, and the draft keeps SUP2.
        using (var missing = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, s.Sup, 9).Without("partnerId")))
            await HttpAssert.ValidationAsync(missing, "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, changed);

        var removed = await Stock.ReplaceAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, null));

        PartnerDocs.AssertNoPartner(removed);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, draft.Id(), null);

        // And a draft created without a partner gets one by a replace.
        var set = await Stock.ReplaceAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, s.Sup));
        PartnerDocs.AssertPartner(set, s.O.Sup);
        Assert.Equal(draft.Id(), set.Id());
        Assert.Equal("draft", set.Str("status"));
    }

    [Fact]
    public async Task AC22_An_issue_must_send_partnerId_on_replace_too()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.IssueAsync(s, s.Cus);

        using (var missing = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, null).Without("partnerId")))
            await HttpAssert.ValidationAsync(missing, "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, draft);

        PartnerDocs.AssertNoPartner(await Stock.ReplaceAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, null)));
    }

    // ---- AC-23 ----

    [Fact]
    public async Task AC23_A_partner_with_both_roles_fits_a_receipt_and_an_issue()
    {
        var s = await PartnerDocs.SetupAsync(app);

        var receipt = await PartnerDocs.ReceiptAsync(s, s.Both);
        var issue = await PartnerDocs.IssueAsync(s, s.Both);

        PartnerDocs.AssertPartner(receipt, s.BothPartner);
        PartnerDocs.AssertPartner(issue, s.BothPartner);
    }

    // ---- AC-25 ----

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    public async Task AC25_An_unknown_partner_is_REFERENCE_NOT_FOUND_and_an_inactive_one_REFERENCE_INACTIVE(string type)
    {
        var s = await PartnerDocs.SetupAsync(app);
        var inactive = await Orders.PartnerAsync(s.Http, "GONE", "Inactive partner", isSupplier: true, isCustomer: true, isActive: false);
        var draft = await Stock.CreateAsync(s.Http, Stock.Draft(type, s.W1, (s.A, 1)));

        using var unknown = await Stock.PostAsync(s.Http, PartnerDocs.Body(s, type, Guid.NewGuid().ToString()));
        using var notActive = await Stock.PostAsync(s.Http, PartnerDocs.Body(s, type, inactive.Id().ToString()));
        // An id of another kind of master is no partner either.
        using var aWarehouse = await Stock.PostAsync(s.Http, PartnerDocs.Body(s, type, s.W1.ToString()));
        using var unknownOnReplace = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, Guid.NewGuid()));
        using var notActiveOnReplace = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, inactive.Id()));

        await PartnerDocs.ConflictExactlyAsync(unknown, "REFERENCE_NOT_FOUND", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(notActive, "REFERENCE_INACTIVE", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(aWarehouse, "REFERENCE_NOT_FOUND", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(unknownOnReplace, "REFERENCE_NOT_FOUND", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(notActiveOnReplace, "REFERENCE_INACTIVE", "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 1);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"0199c0de-0000-7000-8000\"")]
    public async Task AC25_E8_A_partnerId_that_is_not_a_uuid_or_null_is_rejected_with_its_key(string json)
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup);

        using var create = await Stock.PostAsync(s.Http, PartnerDocs.Body(s, "receipt", JsonNode.Parse(json)));
        using var issue = await Stock.PostAsync(s.Http, PartnerDocs.Body(s, "issue", JsonNode.Parse(json)));
        using var replace = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, JsonNode.Parse(json)));

        await HttpAssert.ValidationAsync(create, "partnerId");
        await HttpAssert.ValidationAsync(issue, "partnerId");
        await HttpAssert.ValidationAsync(replace, "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, draft);
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 1);
    }

    [Fact]
    public async Task AC25_R7_E2_Header_references_answer_by_stage_and_report_warehouse_and_partner_together()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var inactive = await Orders.PartnerAsync(s.Http, "GONE", "Inactive partner", isSupplier: true, isCustomer: false, isActive: false);
        var inactiveCustomer = await Orders.PartnerAsync(s.Http, "GONE2", "Inactive customer", isSupplier: false, isCustomer: true, isActive: false);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W2, false);
        JsonObject In(Guid warehouse, Guid partner) => PartnerDocs.Receipt(s, partner).With("warehouseId", warehouse.ToString());

        // E2: the not-found stage comes first and answers alone.
        using var unknownPartnerInactiveWarehouse = await Stock.PostAsync(s.Http, In(s.W2, Guid.NewGuid()));
        using var bothUnknown = await Stock.PostAsync(s.Http, In(Guid.NewGuid(), Guid.NewGuid()));
        using var bothInactive = await Stock.PostAsync(s.Http, In(s.W2, inactive.Id()));
        // Activity before role; role before lines.
        using var inactiveWithoutRole = await Stock.PostAsync(s.Http, In(s.W1, inactiveCustomer.Id()));
        using var roleBeforeLines = await Stock.PostAsync(s.Http,
            PartnerDocs.Receipt(s, s.Cus).With("lines", Stock.Lines((Guid.NewGuid(), 1), (s.S.S, 1))));
        // Validation before everything.
        using var validationFirst = await Stock.PostAsync(s.Http, PartnerDocs.Receipt(s, Guid.NewGuid(), 0));

        await PartnerDocs.ConflictExactlyAsync(unknownPartnerInactiveWarehouse, "REFERENCE_NOT_FOUND", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(bothUnknown, "REFERENCE_NOT_FOUND", "partnerId", "warehouseId");
        await PartnerDocs.ConflictExactlyAsync(bothInactive, "REFERENCE_INACTIVE", "partnerId", "warehouseId");
        await PartnerDocs.ConflictExactlyAsync(inactiveWithoutRole, "REFERENCE_INACTIVE", "partnerId");
        await PartnerDocs.ConflictExactlyAsync(roleBeforeLines, "PARTNER_ROLE_MISSING", "partnerId");
        Assert.Equal(new[] { "lines[0].quantity" }, McpAssert.ErrorKeys(await HttpAssert.ValidationAsync(validationFirst, "lines[0].quantity")));
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 0);
    }

    // ---- AC-26 ----

    [Fact]
    public async Task AC26_A_transfer_has_no_partner()
    {
        var s = await PartnerDocs.SetupAsync(app);

        using var withPartner = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).With("partnerId", s.Sup.ToString()));
        using var withBoth = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).With("partnerId", s.Both.ToString()));
        // R1: any non-null value is refused as input, whether it is a partner or not.
        using var withUnknown = await Stock.PostAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).With("partnerId", Guid.NewGuid().ToString()));

        await HttpAssert.ValidationAsync(withPartner, "partnerId");
        await HttpAssert.ValidationAsync(withBoth, "partnerId");
        await HttpAssert.ValidationAsync(withUnknown, "partnerId");
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 0);

        var omitted = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 1));
        var asNull = await Stock.CreateAsync(s.Http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).With("partnerId", null));
        PartnerDocs.AssertNoPartner(omitted);
        PartnerDocs.AssertNoPartner(asNull);

        var without = await Stock.ReplaceAsync(s.Http, omitted.Id(), Stock.TransferReplacement(s.W1, s.W2, (s.A, 2)));
        var withNull = await Stock.ReplaceAsync(s.Http, omitted.Id(), Stock.TransferReplacement(s.W1, s.W2, (s.A, 3)).With("partnerId", null));
        using var replaceWithPartner = await Stock.PutAsync(s.Http, omitted.Id(),
            Stock.TransferReplacement(s.W1, s.W2, (s.A, 4)).With("partnerId", s.Sup.ToString()));

        Assert.False(Stock.TransferReplacement(s.W1, s.W2).ContainsKey("partnerId"));
        PartnerDocs.AssertNoPartner(without);
        PartnerDocs.AssertNoPartner(withNull);
        await HttpAssert.ValidationAsync(replaceWithPartner, "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, withNull);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, omitted.Id(), null);
    }

    [Fact]
    public async Task AC26_A_count_has_no_partner()
    {
        var s = await PartnerDocs.SetupAsync(app);

        using var withPartner = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, 1, null)).With("partnerId", s.Sup.ToString()));
        using var withUnknown = await Stock.PostAsync(s.Http, Counts.Draft(s.W1, (s.A, 1, null)).With("partnerId", Guid.NewGuid().ToString()));

        await HttpAssert.ValidationAsync(withPartner, "partnerId");
        await HttpAssert.ValidationAsync(withUnknown, "partnerId");
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 0);

        var omitted = await Counts.CountAsync(s.Http, s.W1, s.A, 1);
        var asNull = await Stock.CreateAsync(s.Http, Counts.Draft(s.W1, (s.B, 1, null)).With("partnerId", null));
        PartnerDocs.AssertNoPartner(omitted);
        PartnerDocs.AssertNoPartner(asNull);

        var sameValues = Counts.SameValues(omitted);
        Assert.False(sameValues.ContainsKey("partnerId"));
        var without = await Stock.ReplaceAsync(s.Http, omitted.Id(), sameValues);
        var withNull = await Stock.ReplaceAsync(s.Http, omitted.Id(), Counts.SameValues(omitted).With("partnerId", null));
        using var replaceWithPartner = await Stock.PutAsync(s.Http, omitted.Id(), Counts.SameValues(omitted).With("partnerId", s.Sup.ToString()));

        PartnerDocs.AssertNoPartner(without);
        PartnerDocs.AssertNoPartner(withNull);
        await HttpAssert.ValidationAsync(replaceWithPartner, "partnerId");
        await Stock.AssertUnchangedAsync(s.Http, withNull);

        // Posted, a count still has none.
        var posted = await Stock.PostDocumentAsync(s.Http, omitted.Id());
        PartnerDocs.AssertNoPartner(posted);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, omitted.Id(), null);
    }

    // ---- AC-27 ----

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    [InlineData("transfer")]
    public async Task AC27_A_line_with_partnerId_is_rejected(string type)
    {
        var s = await PartnerDocs.SetupAsync(app);
        var partner = type == "issue" ? s.Cus : s.Sup;
        var lines = new JsonArray(Stock.Line(s.A, 1).With("partnerId", partner.ToString()));
        var body = (type == "transfer" ? Stock.Transfer(s.W1, s.W2) : Stock.Draft(type, s.W1)).With("lines", lines);
        var nullOnLine = Stock.Draft("receipt", s.W1).With("lines", new JsonArray(Stock.Line(s.A, 1).With("partnerId", null)));

        using var response = await Stock.PostAsync(s.Http, body);
        using var asNull = await Stock.PostAsync(s.Http, nullOnLine);

        await HttpAssert.ValidationAsync(response);
        await HttpAssert.ValidationAsync(asNull);
        await PartnerDocs.AssertDocumentCountAsync(s.Http, 0);
    }

    // ---- AC-28 ----

    [Fact]
    public async Task AC28_R13_A_document_shows_the_current_code_and_name_of_its_partner()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup);
        var posted = await PartnerDocs.PostedReceiptAsync(s, s.Sup, 5);
        var other = await PartnerDocs.ReceiptAsync(s, s.Sup2);

        var renamed = await Orders.ChangePartnerAsync(s.Http, s.Sup, b =>
        {
            b["name"] = "Bolt Works International";
            b["code"] = "BOLT";
        });

        Assert.Equal(("BOLT", "Bolt Works International"), (renamed.Str("code"), renamed.Str("name")));
        var draftNow = await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, draft.Id(), renamed);
        var postedNow = await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, posted.Id(), renamed);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, other.Id(), s.Sup2Partner);
        // Nothing else about the documents moved: a rename of a master is not an edit of the document.
        Assert.Equal(draft.Str("updatedAt"), draftNow.Str("updatedAt"));
        Assert.Equal(posted.Str("updatedAt"), postedNow.Str("updatedAt"));
        Assert.Equal(posted.Number(), postedNow.Number());
    }

    // ---- AC-30 ----

    [Fact]
    public async Task AC30_Posting_keeps_the_partner_and_writes_the_ledger_of_spec_005()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var plain = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 3);
        var plainEntry = Assert.Single(await Stock.EntriesAsync(s.Http, plain.Id()));
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup, 5);

        using var response = await Stock.SendPostAsync(s.Http, draft.Id());

        var posted = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(("posted", OrderApi.Purchase.DocumentNumber(2)), (posted.Str("status"), posted.Number()));
        PartnerDocs.AssertPartner(posted, s.O.Sup);
        Assert.Equal(8m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        var entry = Assert.Single(await Stock.EntriesAsync(s.Http, draft.Id()));
        Assert.Equal(5m, entry.Quantity());
        Assert.Equal((s.A, s.W1), (entry.GetProperty("article").Id(), entry.GetProperty("warehouse").Id()));
        Assert.Equal(PartnerDocs.Date, entry.Str("documentDate"));
        // R11: nothing about the partner is written to the ledger.
        Assert.Equal(plainEntry.PropertyNames(), entry.PropertyNames());
        Assert.False(entry.TryGetProperty("partner", out _), $"The ledger entry has a partner: {entry}");

        // R13: immutable, with its partner.
        using var put = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, s.Sup2));
        using var remove = await Stock.PutAsync(s.Http, draft.Id(), PartnerDocs.Replacement(s, null));
        await Stock.ConflictAsync(put, "INVALID_STATE");
        await Stock.ConflictAsync(remove, "INVALID_STATE");
        await Stock.AssertUnchangedAsync(s.Http, posted);
        await PartnerDocs.AssertPartnerEverywhereAsync(s.Http, draft.Id(), s.O.Sup);
        await PartnerDocs.AssertBalancedAsync(s, "AC-90");
    }

    [Fact]
    public async Task AC30_AC90_A_posted_issue_keeps_its_customer_and_takes_the_stock()
    {
        var s = await PartnerDocs.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var draft = await PartnerDocs.IssueAsync(s, s.Cus, 4);

        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());

        Assert.Equal(("posted", OrderApi.Sales.DocumentNumber(1)), (posted.Str("status"), posted.Number()));
        PartnerDocs.AssertPartner(posted, s.O.Cus);
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(-4m, Assert.Single(await Stock.EntriesAsync(s.Http, draft.Id())).Quantity());
        // Too much is still refused, with or without a partner (R11: the rules of spec 005 are unchanged).
        var tooMuch = await PartnerDocs.IssueAsync(s, s.Cus, 7);
        using (var refused = await Stock.SendPostAsync(s.Http, tooMuch.Id()))
            await Stock.ConflictAsync(refused, "INSUFFICIENT_STOCK");
        PartnerDocs.AssertPartner(await Stock.AssertDraftAsync(s.Http, tooMuch.Id()), s.O.Cus);
        var balanced = await PartnerDocs.AssertBalancedAsync(s, "AC-90");
        Assert.Equal(6m, balanced[(s.A, s.W1)]);
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_Warehouse_and_partner_inactive_at_posting_are_reported_together_and_alone()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Sup, 5);
        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, false);
        await Orders.SetPartnerActiveAsync(s.Http, s.Sup, false);
        await Stock.SetArticleActiveAsync(s.Http, s.A, false);

        // 005/R13: the header masters together, and alone — the inactive article is not reported yet.
        using (var both = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(both, "REFERENCE_INACTIVE", "partnerId", "warehouseId");

        await Stock.SetWarehouseActiveAsync(s.Http, s.W1, true);
        using (var partner = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(partner, "REFERENCE_INACTIVE", "partnerId");

        await Orders.SetPartnerActiveAsync(s.Http, s.Sup, true);
        using (var article = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(article, "REFERENCE_INACTIVE", "lines[0].articleId");

        PartnerDocs.AssertPartner(await Stock.AssertDraftAsync(s.Http, draft.Id()), s.O.Sup);
        Assert.Equal(0, (await Stock.LedgerAsync(s.Http)).Total());
        await Stock.SetArticleActiveAsync(s.Http, s.A, true);
        var posted = await Stock.PostDocumentAsync(s.Http, draft.Id());
        Assert.Equal(OrderApi.Purchase.DocumentNumber(1), posted.Number());
        await PartnerDocs.AssertBalancedAsync(s, "AC-90");
    }

    [Fact]
    public async Task AC32_R12_An_inactive_partner_without_the_role_is_reported_as_inactive_first()
    {
        var s = await PartnerDocs.SetupAsync(app);
        var draft = await PartnerDocs.ReceiptAsync(s, s.Both, 5);
        await Orders.ChangePartnerAsync(s.Http, s.Both, b =>
        {
            b["isSupplier"] = false;
            b["isActive"] = false;
        });

        using (var inactive = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(inactive, "REFERENCE_INACTIVE", "partnerId");

        await Orders.SetPartnerActiveAsync(s.Http, s.Both, true);
        using (var role = await Stock.SendPostAsync(s.Http, draft.Id()))
            await PartnerDocs.ConflictExactlyAsync(role, "PARTNER_ROLE_MISSING", "partnerId");
        await Stock.AssertDraftAsync(s.Http, draft.Id());
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }
}
