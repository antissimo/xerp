using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 012, AC-40 to AC-44: a tenant that sets no rule behaves exactly as specs 001–011a say; the refusals a
/// rule causes name it ("refused by"), and a refusal no rule caused has no <c>rules</c> member (R38–R40).
/// </summary>
[Collection(XerpCollection.Name)]
public class RuleDefaultsTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;

    [Fact]
    public async Task AC40_A_tenant_that_sets_nothing_behaves_as_specs_001_to_011a_say()
    {
        var s = await Orders.SetupAsync(app);

        await Rules.AssertDefaultBehaviourAsync(s, named: false);
    }

    [Fact]
    public async Task AC41_A_tenant_that_set_every_rule_to_the_other_value_and_reset_it_behaves_the_same()
    {
        var s = await Orders.SetupAsync(app);
        foreach (var key in Rules.Keys)
            Rules.AssertTenantValue(await Rules.SetAsync(s.Http, key, !Rules.Default(key)), key, !Rules.Default(key), s.Tenant.ApiKeyId);
        foreach (var key in Rules.Keys)
            Rules.AssertAtDefault(await Rules.ResetAsync(s.Http, key), key);

        await Rules.AssertDefaultBehaviourAsync(s, named: true);
    }

    [Fact]
    public async Task AC41_R7_A_tenant_whose_own_values_equal_the_defaults_behaves_the_same()
    {
        var s = await Orders.SetupAsync(app);
        foreach (var key in Rules.Keys)
            await Rules.SetAsync(s.Http, key, Rules.Default(key));

        await Rules.AssertDefaultBehaviourAsync(s, named: true);
    }

    [Fact]
    public async Task AC42_The_five_refusals_of_the_defaults_name_their_rule_with_the_value_in_force()
    {
        var s = await Orders.SetupAsync(app);

        await Rules.AssertDefaultBehaviourAsync(s, named: true);
    }

    [Fact]
    public async Task AC42_R38_A_refusal_for_several_lines_names_the_rule_once_with_all_its_fields()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 5);
        var issue = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 6), (s.B, 1));
        var order = await PO.OrderedAsync(s, (s.A, 10, null, 1m), (s.B, 10, null, 1m));
        var receipt = await PO.CreateDocumentAsync(s.Http, s.W1, order.Id(), (s.A, 11, 1, null), (s.B, 10.5m, 2, null));

        using var aboveStock = await Stock.SendPostAsync(s.Http, issue.Id());
        using var aboveOrder = await Stock.SendPostAsync(s.Http, receipt.Id());

        await Rules.InsufficientAsync(aboveStock, "lines[0].quantity", "lines[1].quantity");
        await Rules.ExceedsAsync(aboveOrder, PO, "lines[0].quantity", "lines[1].quantity");
        await Rules.AssertBalancedAsync(s.Http);
    }

    [Fact]
    public async Task AC42_R38_A_refused_reversal_and_a_refused_transfer_name_the_negative_stock_rule()
    {
        var s = await Orders.SetupAsync(app);
        var receipt = await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Stock.IssueAsync(s.Http, s.W1, s.A, 4);
        var transfer = await Stock.CreateTransferAsync(s.Http, s.W1, s.W2, (s.A, 7));

        using var reversal = await Stock.SendReverseAsync(s.Http, receipt.Id());
        using var move = await Stock.SendPostAsync(s.Http, transfer.Id());

        await Rules.InsufficientAsync(reversal, "lines[0].quantity");
        await Rules.InsufficientAsync(move, "lines[0].quantity");
        Assert.Equal(6m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        await Rules.AssertBalancedAsync(s.Http);
    }

    // ---- AC-43 ----

    public static TheoryData<string> Kinds() => new() { "purchase", "sales" };

    private static OrderApi Kind(string kind) => kind == "purchase" ? PO : SO;

    [Fact]
    public async Task AC43_A_validation_failure_of_a_stock_document_that_no_rule_caused_has_no_rules_member()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        JsonObject Receipt(params (Guid, decimal)[] lines) => Stock.Draft("receipt", s.W1, lines);

        using var negative = await Stock.PostAsync(http, Receipt((s.A, -1)));
        using var empty = await Stock.PostAsync(http, Receipt());
        using var unknown = await Stock.PostAsync(http, Receipt((s.A, 1)).With("x", 1));
        // Six decimals and 200 lines are platform bounds (I10), not rules.
        using var sevenDecimals = await Stock.PostAsync(http, Receipt((s.A, 1.0000001m)));
        using var tooManyLines = await Stock.PostAsync(http, Receipt().With("lines", Stock.Lines(Enumerable.Repeat((s.A, 1m), 201).ToArray())));

        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(negative, "lines[0].quantity"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(empty, "lines"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(unknown, "x"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(sevenDecimals, "lines[0].quantity"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(tooManyLines, "lines"));

        var sixDecimals = await Stock.CreateAsync(http, Receipt((s.A, 1.000001m)));
        var twoHundred = await Stock.CreateAsync(http, Receipt().With("lines", Stock.Lines(Enumerable.Repeat((s.A, 1m), 200).ToArray())));
        Assert.Equal(1.000001m, sixDecimals.DocumentLines()[0].Quantity());
        Assert.Equal(200, twoHundred.DocumentLines().Length);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AC43_A_validation_failure_of_an_order_that_no_rule_caused_has_no_rules_member(string kind)
    {
        var o = Kind(kind);
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var draft = await o.DraftAsync(s, (s.A, 1, null, 1m));
        JsonArray Lines(int count) => new(Enumerable.Range(0, count).Select(_ => (JsonNode)Orders.Line(s.B, 1, 0.5m)).ToArray());

        using var negative = await o.PostAsync(http, o.Body(s, (s.A, -1, null, 1m)));
        using var empty = await o.PostAsync(http, o.Body(s));
        using var unknown = await o.PostAsync(http, o.Body(s, (s.A, 1, null, 1m)).With("x", 1));
        using var sevenDecimals = await o.PostAsync(http, o.Body(s, (s.A, 1.0000001m, null, 1m)));
        using var tooManyLines = await o.PostAsync(http, o.Body(s).With("lines", Lines(201)));
        // R32: the shape of the partner is an invariant, whatever the rule's value.
        using var malformed = await o.PostAsync(http, o.Body(s, (s.A, 1, null, 1m)).With(o.PartnerId, "abc"));
        using var notAString = await o.PostAsync(http, o.Body(s, (s.A, 1, null, 1m)).With(o.PartnerId, 5));
        using var replaceWithout = await o.PutAsync(http, draft.Id(), o.Replacement(s, (s.A, 1, null, 1m)).Without(o.PartnerId));

        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(negative, "lines[0].quantity"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(empty, "lines"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(unknown, "x"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(sevenDecimals, "lines[0].quantity"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(tooManyLines, "lines"));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(malformed, o.PartnerId));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(notAString, o.PartnerId));
        Rules.AssertNamesNoRule(await HttpAssert.ValidationAsync(replaceWithout, o.PartnerId));
        await o.AssertUnchangedAsync(http, draft);

        var sixDecimals = await o.CreateAsync(http, o.Body(s, (s.A, 1.000001m, null, 1m)));
        var twoHundred = await o.CreateAsync(http, o.Body(s).With("lines", Lines(200)));
        Assert.Equal(1.000001m, sixDecimals.OrderLines()[0].Dec("quantity"));
        Assert.Equal(200, twoHundred.OrderLines().Length);
    }

    [Fact]
    public async Task AC43_A_conflict_that_no_rule_caused_has_no_rules_member()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;

        // REFERENCE_NOT_FOUND
        using var unknownArticle = await Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (Guid.CreateVersion7(), 1)));
        Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(unknownArticle, HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND"));
        using var unknownSupplier = await PO.PostAsync(http, PO.Body(Guid.CreateVersion7(), s.W1, (s.A, 1, null, 1m)));
        Rules.AssertNamesNoRule(await HttpAssert.ReferenceNotFoundAsync(unknownSupplier, "supplierId"));

        // PARTNER_ROLE_MISSING
        using var wrongRole = await PO.PostAsync(http, PO.Body(s.Cus.Id(), s.W1, (s.A, 1, null, 1m)));
        Rules.AssertNamesNoRule(await Stock.ConflictAsync(wrongRole, "PARTNER_ROLE_MISSING", "supplierId"));
        using var wrongRoleSales = await SO.PostAsync(http, SO.Body(s.Sup.Id(), s.W1, (s.A, 1, null, 1m)));
        Rules.AssertNamesNoRule(await Stock.ConflictAsync(wrongRoleSales, "PARTNER_ROLE_MISSING", "customerId"));

        // INVALID_STATE
        var posted = await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        using var postAgain = await Stock.SendPostAsync(http, posted.Id());
        Rules.AssertNamesNoRule(await Stock.ConflictAsync(postAgain, "INVALID_STATE"));

        // ORDER_NOT_OPEN
        var order = await PO.OrderedAsync(s, (s.A, 10, null, 1m));
        var receipt = await PO.DraftDocumentAsync(http, order, 1, 5);
        await PO.CloseAsync(http, order.Id());
        using var againstClosed = await Stock.SendPostAsync(http, receipt.Id());
        Rules.AssertNamesNoRule(await Stock.ConflictAsync(againstClosed, "ORDER_NOT_OPEN", "purchaseOrderId"));

        // COUNT_OUTDATED
        var count = await Counts.CountAsync(http, s.W1, s.A, 9);
        await Stock.ReceiveAsync(http, s.W1, s.A, 1);
        using var outdated = await Stock.SendPostAsync(http, count.Id());
        Rules.AssertNamesNoRule(await Stock.ConflictAsync(outdated, "COUNT_OUTDATED"));

        await Rules.AssertBalancedAsync(http);
    }

    // ---- AC-44 ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AC44_In_a_mixed_validation_failure_the_rule_names_only_the_field_it_refused(string kind)
    {
        var o = Kind(kind);
        var s = await Orders.SetupAsync(app);
        var body = o.NoPartner(s, (s.A, -1, null, 1m)).With("orderDate", "2026-02-30");

        using var response = await o.PostAsync(s.Http, body);

        var problem = await HttpAssert.ValidationAsync(response, "orderDate", o.PartnerId, "lines[0].quantity");
        Assert.Equal(new[] { "lines[0].quantity", "orderDate", o.PartnerId }.Order(StringComparer.Ordinal), McpAssert.ErrorKeys(problem));
        var rule = Rules.AssertRule(problem, Rules.PartnerRule(o), true);
        Assert.Equal(new[] { o.PartnerId }, Rules.Fields(rule));
        Assert.Equal(0, (await o.ListAsync(s.Http)).Total());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AC44_The_same_on_replace(string kind)
    {
        var o = Kind(kind);
        var s = await Orders.SetupAsync(app);
        var draft = await o.DraftAsync(s, (s.A, 1, null, 1m));

        using var response = await o.PutAsync(s.Http, draft.Id(), o.NoPartnerReplacement(s, (s.A, -1, null, 1m)).With("orderDate", "2026-02-30"));

        var problem = await HttpAssert.ValidationAsync(response, "orderDate", o.PartnerId, "lines[0].quantity");
        Assert.Equal(3, McpAssert.ErrorKeys(problem).Length);
        Assert.Equal(new[] { o.PartnerId }, Rules.Fields(Rules.AssertRule(problem, Rules.PartnerRule(o), true)));
        await o.AssertUnchangedAsync(s.Http, draft);
    }
}
