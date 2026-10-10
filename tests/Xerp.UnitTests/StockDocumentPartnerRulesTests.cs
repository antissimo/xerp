using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Stock;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 011a: the partner in the header of a stock document. The rules of Domain and Application, without a
/// database - which types have a partner (R1, R3), the header references and the role of an unlinked document
/// (R5-R7, R12), the order's partner on a linked one (R8, R9) and the copy on reversal (R14).
/// </summary>
public class StockDocumentPartnerRulesTests
{
    private const string Date = "2026-10-10";
    private static readonly DateOnly Day = new(2026, 10, 10);
    private static readonly DateTime Now = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();
    private static readonly Guid Sup = Guid.CreateVersion7();
    private static readonly Guid Sup2 = Guid.CreateVersion7();

    private static readonly StockLineEntry[] OneLine = [new(A, Pcs, 1m)];

    private static StockDocument Draft(StockDocumentType type, Guid? partnerId = null) =>
        StockDocument.Create(
            type, Day, W1, type == StockDocumentType.Transfer ? W2 : null, null, null, OneLine, Now, Actor,
            type == StockDocumentType.Count ? [0m] : null, partnerId: partnerId);

    private static void AssertKeys(AppError? error, string code, params string[] keys)
    {
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(keys.Order(StringComparer.Ordinal), error.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    private static CreateStockDocumentInput NewDocument(string type, string? partnerId, string? toWarehouseId = null) =>
        new(type, Date, W1.ToString(), [new StockLineInput(A.ToString(), 1m)], ToWarehouseId: toWarehouseId, PartnerId: partnerId);

    // ---- Domain

    [Theory]
    [InlineData(StockDocumentType.Receipt)]
    [InlineData(StockDocumentType.Issue)]
    public void R1_A_receipt_and_an_issue_keep_the_partner_they_are_created_with(StockDocumentType type)
    {
        Assert.Equal(Sup, Draft(type, Sup).PartnerId);
        Assert.Null(Draft(type).PartnerId);
    }

    [Fact]
    public void R3_A_replace_sets_the_partner_like_every_header_field_and_null_removes_it()
    {
        var draft = Draft(StockDocumentType.Receipt, Sup);

        draft.Replace(Day, W1, null, null, null, OneLine, Now, Actor, partnerId: Sup2);
        Assert.Equal(Sup2, draft.PartnerId);

        draft.Replace(Day, W1, null, null, null, OneLine, Now, Actor);
        Assert.Null(draft.PartnerId);
    }

    [Theory]
    [InlineData(StockDocumentType.Transfer)]
    [InlineData(StockDocumentType.Count)]
    public void R1_A_transfer_and_a_count_never_have_a_partner(StockDocumentType type)
    {
        Assert.False(StockDocument.CanNamePartner(type));
        Assert.Throws<ArgumentException>(() => Draft(type, Sup));

        var draft = Draft(type);
        var destination = type == StockDocumentType.Transfer ? W2 : (Guid?)null;
        IReadOnlyList<decimal>? book = type == StockDocumentType.Count ? [0m] : null;
        Assert.Throws<ArgumentException>(() => draft.Replace(Day.AddDays(1), W1, destination, "changed", null, OneLine, Now, Actor, book, Sup));
        // A refused replace leaves the draft as it was.
        Assert.Equal((Day, (string?)null, (Guid?)null), (draft.DocumentDate, draft.Reference, draft.PartnerId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R11_R14_Posting_keeps_the_partner_and_the_reversing_document_carries_it(bool withPartner)
    {
        Guid? partner = withPartner ? Sup : null;
        var original = Draft(StockDocumentType.Receipt, partner);

        var entries = original.Post("SR-000001", [1m], Now, Actor);
        Assert.Equal(partner, original.PartnerId);

        var (reversal, _) = original.Reverse(entries.ToList(), Day, null, "SR-000002", Now, Actor);
        Assert.Equal(partner, reversal.PartnerId);
        Assert.Equal(partner, original.PartnerId);
    }

    // ---- which types, and when the property must be sent

    [Fact]
    public void R1_R3_The_type_decides_whether_a_partner_may_be_named_and_whether_a_replace_must_send_it()
    {
        foreach (var type in new[] { StockDocumentType.Receipt, StockDocumentType.Issue })
        {
            Assert.Null(StockPartnerChecks.OfType(type, Sup, given: true));
            Assert.Null(StockPartnerChecks.OfType(type, null, given: true));
            Assert.NotNull(StockPartnerChecks.OfType(type, null, given: false));
        }
        foreach (var type in new[] { StockDocumentType.Transfer, StockDocumentType.Count })
        {
            Assert.NotNull(StockPartnerChecks.OfType(type, Sup, given: true));
            Assert.Null(StockPartnerChecks.OfType(type, null, given: true));
            Assert.Null(StockPartnerChecks.OfType(type, null, given: false));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("SUP-1")]
    public void R3_A_create_refuses_a_partner_id_that_is_not_a_uuid(string partnerId) =>
        AssertKeys(StockDocumentValidation.Create(NewDocument("receipt", partnerId)).Error, ErrorCodes.ValidationFailed, "partnerId");

    [Fact]
    public void R3_On_a_create_an_omitted_partner_and_null_are_the_same_and_a_uuid_is_taken()
    {
        Assert.Null(StockDocumentValidation.Create(NewDocument("receipt", null)).Value!.Values.PartnerId);
        Assert.Equal(Sup, StockDocumentValidation.Create(NewDocument("issue", Sup.ToString())).Value!.Values.PartnerId);
    }

    [Fact]
    public void R1_A_create_of_a_transfer_or_a_count_with_a_partner_is_invalid_and_without_one_is_not()
    {
        AssertKeys(StockDocumentValidation.Create(NewDocument("transfer", Sup.ToString(), W2.ToString())).Error, ErrorCodes.ValidationFailed, "partnerId");
        AssertKeys(StockDocumentValidation.Create(NewDocument("count", Sup.ToString())).Error, ErrorCodes.ValidationFailed, "partnerId");
        Assert.True(StockDocumentValidation.Create(NewDocument("transfer", null, W2.ToString())).IsSuccess);
        Assert.True(StockDocumentValidation.Create(NewDocument("count", null)).IsSuccess);
        // An unknown type cannot say that it has no partner: only the type is reported.
        AssertKeys(StockDocumentValidation.Create(NewDocument("return", Sup.ToString())).Error, ErrorCodes.ValidationFailed, "type");
    }

    private static ReplaceStockDocumentInput Replacement() =>
        new() { DocumentDate = Date, WarehouseId = W1.ToString(), Reference = null, Note = null, Lines = [new StockLineInput(A.ToString(), 1m)] };

    [Fact]
    public void R3_A_replace_tells_an_omitted_partner_from_null()
    {
        var omitted = StockDocumentValidation.Replace(Replacement()).Value!;
        Assert.False(omitted.PartnerGiven);
        AssertKeys(StockDocumentValidation.OfType(StockDocumentType.Receipt, omitted), ErrorCodes.ValidationFailed, "partnerId");
        AssertKeys(StockDocumentValidation.OfType(StockDocumentType.Issue, omitted, linked: false), ErrorCodes.ValidationFailed, "partnerId");
        Assert.Null(StockDocumentValidation.OfType(StockDocumentType.Count, omitted));

        var removed = StockDocumentValidation.Replace(Replacement() with { PartnerId = null }).Value!;
        Assert.True(removed.PartnerGiven);
        Assert.Null(removed.PartnerId);
        Assert.Null(StockDocumentValidation.OfType(StockDocumentType.Receipt, removed));

        var named = StockDocumentValidation.Replace(Replacement() with { PartnerId = Sup.ToString() }).Value!;
        Assert.Equal(Sup, named.PartnerId);
        Assert.Null(StockDocumentValidation.OfType(StockDocumentType.Issue, named));
        AssertKeys(StockDocumentValidation.OfType(StockDocumentType.Count, named), ErrorCodes.ValidationFailed, "partnerId");

        AssertKeys(StockDocumentValidation.Replace(Replacement() with { PartnerId = "abc" }).Error, ErrorCodes.ValidationFailed, "partnerId");
    }

    [Fact]
    public void R16_The_list_filter_is_a_uuid_and_an_empty_value_is_no_filter()
    {
        Assert.Equal(Sup, StockDocumentValidation.List(new ListStockDocumentsInput(PartnerId: Sup.ToString())).Value!.PartnerId);
        Assert.Null(StockDocumentValidation.List(new ListStockDocumentsInput(PartnerId: "")).Value!.PartnerId);
        Assert.Null(StockDocumentValidation.List(new ListStockDocumentsInput()).Value!.PartnerId);
        AssertKeys(StockDocumentValidation.List(new ListStockDocumentsInput(PartnerId: "not-a-uuid")).Error, ErrorCodes.ValidationFailed, "partnerId");
    }

    // ---- an unlinked document: header references, then the role

    [Fact]
    public void R7_Unknown_header_masters_are_reported_together_and_before_any_inactive_one()
    {
        var warehouse = new HeaderReference("warehouseId", W1, IsActive: true);
        var partner = new HeaderReference("partnerId", Sup, IsActive: true);

        Assert.Null(StockPartnerChecks.HeaderReferences(warehouse, partner));
        Assert.Null(StockPartnerChecks.HeaderReferences(warehouse));
        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse, partner with { IsActive = null }), ErrorCodes.ReferenceNotFound, "partnerId");
        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse with { IsActive = null }, partner with { IsActive = null }),
            ErrorCodes.ReferenceNotFound, "partnerId", "warehouseId");
        // E2: the not-found stage answers alone.
        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse with { IsActive = false }, partner with { IsActive = null }),
            ErrorCodes.ReferenceNotFound, "partnerId");
        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse with { IsActive = null }, partner with { IsActive = false }),
            ErrorCodes.ReferenceNotFound, "warehouseId");
    }

    [Fact]
    public void R5_Inactive_header_masters_are_reported_together_unless_the_draft_already_has_them()
    {
        var warehouse = new HeaderReference("warehouseId", W1, IsActive: false);
        var partner = new HeaderReference("partnerId", Sup, IsActive: false);

        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse, partner), ErrorCodes.ReferenceInactive, "partnerId", "warehouseId");
        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse with { IsActive = true }, partner), ErrorCodes.ReferenceInactive, "partnerId");
        // E3: a draft keeps a master deactivated since.
        AssertKeys(StockPartnerChecks.HeaderReferences(warehouse, partner with { AlreadyAssigned = true }), ErrorCodes.ReferenceInactive, "warehouseId");
        Assert.Null(StockPartnerChecks.HeaderReferences(warehouse with { AlreadyAssigned = true }, partner with { AlreadyAssigned = true }));
    }

    [Fact]
    public void R6_A_receipt_needs_a_supplier_and_an_issue_a_customer_by_the_rule_orders_use()
    {
        Assert.Equal(OrderKind.Purchase, StockPartnerChecks.Side(StockDocumentType.Receipt));
        Assert.Equal(OrderKind.Sales, StockPartnerChecks.Side(StockDocumentType.Issue));

        Assert.Null(StockPartnerChecks.Role(StockDocumentType.Receipt, Sup, hasRole: true));
        var receipt = StockPartnerChecks.Role(StockDocumentType.Receipt, Sup, hasRole: false);
        AssertKeys(receipt, ErrorCodes.PartnerRoleMissing, "partnerId");
        Assert.Contains("isSupplier", receipt!.Detail, StringComparison.Ordinal);
        var issue = StockPartnerChecks.Role(StockDocumentType.Issue, Sup, hasRole: false);
        AssertKeys(issue, ErrorCodes.PartnerRoleMissing, "partnerId");
        Assert.Contains("isCustomer", issue!.Detail, StringComparison.Ordinal);

        // The order's own answer is the same rule under the order's field.
        AssertKeys(PartnerChecks.RoleMissing(OrderKind.Purchase, OrderKind.Purchase.PartnerField, "a purchase order", Sup),
            ErrorCodes.PartnerRoleMissing, "supplierId");
    }

    // ---- a linked document

    private static readonly LinkedOrderFacts Order =
        new(Guid.CreateVersion7(), OrderStatus.Confirmed, W1, new Dictionary<int, Guid> { [1] = A }, Sup);

    [Fact]
    public void R8_On_a_linked_document_no_partner_and_the_orders_partner_both_agree_with_the_order()
    {
        StockLineEntry[] lines = [new(A, Pcs, 1m, 1)];

        Assert.Null(OrderLinkChecks.Agreement(W1, lines, Order));
        Assert.Null(OrderLinkChecks.Agreement(W1, lines, Order, partnerId: null));
        Assert.Null(OrderLinkChecks.Agreement(W1, lines, Order, Sup));
    }

    [Fact]
    public void R9_Another_partner_is_an_order_mismatch_together_with_the_other_keys_that_apply()
    {
        var other = Guid.CreateVersion7();

        AssertKeys(OrderLinkChecks.Agreement(W1, [new(A, Pcs, 1m, 1)], Order, Sup2), ErrorCodes.OrderMismatch, "partnerId");
        AssertKeys(OrderLinkChecks.Agreement(W2, [new(A, Pcs, 1m, 1)], Order, Sup2), ErrorCodes.OrderMismatch, "partnerId", "warehouseId");
        AssertKeys(OrderLinkChecks.Agreement(W2, [new(other, Pcs, 1m, 1)], Order, Sup2),
            ErrorCodes.OrderMismatch, "lines[0].articleId", "partnerId", "warehouseId");
    }
}
