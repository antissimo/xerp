using System.Reflection;
using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Ports;
using Xerp.Application.Stock;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;
using Xerp.Domain.Rules;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 012, the builder's criteria without HTTP and without a database: the registry (R1, R4), the value a
/// tenant has (R3, S4), AC-03 - R17 and R27 as pure functions of the change, the stock, the reserved quantity
/// and the rule's boolean, R22 as a function of received, document and ordered quantity and the boolean - R23,
/// the partner rule in validation (R32, R33, R39), that a refusal names its rule (R38), and AC-04: no layer
/// above Application reads a rule.
/// </summary>
public class ConfigurableRuleTests
{
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static readonly Dictionary<(Guid, Guid), decimal> Nothing = [];

    // ---- the registry (R1, R4; ADR-0020, decision 2)

    [Fact]
    public void R1_The_registry_has_exactly_the_six_rules_ordered_by_key_with_their_defaults()
    {
        Assert.Equal(
            [
                ("purchase.overReceiptAllowed", false),
                ("purchase.partnerRequired", true),
                ("sales.overDeliveryAllowed", false),
                ("sales.partnerRequired", true),
                ("sales.reservedStockProtected", false),
                ("stock.negativeStockAllowed", false),
            ],
            RuleRegistry.All.Select(r => (r.Key, r.Default)));
    }

    [Fact]
    public void R1_Every_rule_has_a_key_of_two_lower_camel_segments_its_group_a_name_and_a_description()
    {
        foreach (var rule in RuleRegistry.All)
        {
            Assert.Matches("^[a-z][A-Za-z]*\\.[a-z][A-Za-z]*$", rule.Key);
            Assert.True(rule.Key.Length <= RuleDefinition.KeyMaxLength);
            Assert.Equal(rule.Key.Split('.')[0], rule.Group);
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            // Written for an agent: what true does and what false does.
            Assert.Contains("When true", rule.Description, StringComparison.Ordinal);
            Assert.Contains("When false", rule.Description, StringComparison.Ordinal);
        }
        Assert.Equal(RuleRegistry.All.Count, RuleRegistry.All.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("Stock.NegativeStockAllowed")]
    [InlineData("stock.negativestockallowed")]
    [InlineData(" stock.negativeStockAllowed")]
    [InlineData("stock.negativeStock")]
    [InlineData("quantity.decimals")]
    [InlineData("partner.roleRequired")]
    public void R4_Anything_that_is_not_exactly_a_key_of_the_registry_names_no_rule(string? key)
    {
        Assert.Null(RuleRegistry.Find(key));
    }

    [Fact]
    public void R4_A_key_of_the_registry_finds_its_rule()
    {
        foreach (var rule in RuleRegistry.All)
            Assert.Same(rule, RuleRegistry.Find(rule.Key));
        Assert.Same(RuleRegistry.NegativeStockAllowed, RuleRegistry.Find("stock.negativeStockAllowed"));
    }

    [Fact]
    public void Each_kind_of_order_has_its_own_two_rules()
    {
        Assert.Same(RuleRegistry.PurchasePartnerRequired, OrderKind.Purchase.PartnerRequired);
        Assert.Same(RuleRegistry.OverReceiptAllowed, OrderKind.Purchase.OverFulfilmentAllowed);
        Assert.Same(RuleRegistry.SalesPartnerRequired, OrderKind.Sales.PartnerRequired);
        Assert.Same(RuleRegistry.OverDeliveryAllowed, OrderKind.Sales.OverFulfilmentAllowed);
    }

    // ---- the value a tenant has (R3, R7, S4)

    [Fact]
    public void R3_A_tenant_that_has_set_nothing_has_every_default()
    {
        foreach (var rule in RuleRegistry.All)
            Assert.Equal(rule.Default, TenantRules.Defaults[rule]);
    }

    [Fact]
    public void R5_A_value_the_tenant_has_set_is_the_value_in_force_and_only_for_that_rule()
    {
        var rules = new TenantRules(new Dictionary<string, bool>
        {
            [RuleRegistry.NegativeStockAllowed.Key] = true,
            [RuleRegistry.PurchasePartnerRequired.Key] = false,
            // R7: the default's own value is a value like any other.
            [RuleRegistry.SalesPartnerRequired.Key] = true,
        });

        Assert.True(rules[RuleRegistry.NegativeStockAllowed]);
        Assert.False(rules[RuleRegistry.PurchasePartnerRequired]);
        Assert.True(rules[RuleRegistry.SalesPartnerRequired]);
        Assert.False(rules[RuleRegistry.OverReceiptAllowed]);
        Assert.False(rules[RuleRegistry.OverDeliveryAllowed]);
        Assert.False(rules[RuleRegistry.ReservedStockProtected]);
    }

    [Fact]
    public void S4_A_stored_value_whose_key_is_no_rule_is_ignored()
    {
        var rules = new TenantRules(new Dictionary<string, bool> { ["stock.negativeStock"] = true, ["Stock.NegativeStockAllowed"] = true, [""] = true });

        foreach (var rule in RuleRegistry.All)
            Assert.Equal(rule.Default, rules[rule]);
    }

    [Fact]
    public void R10_A_change_records_the_action_the_values_before_and_after_when_and_who()
    {
        var change = RuleChange.Record(RuleRegistry.NegativeStockAllowed, RuleChangeAction.Reset, oldValue: true, newValue: false, Now, Actor);

        Assert.Equal(7, change.Id.Version);
        Assert.Equal(("stock.negativeStockAllowed", RuleChangeAction.Reset, true, false, Now, Actor),
            (change.Key, change.Action, change.OldValue, change.NewValue, change.ChangedAt, change.ChangedBy));
        Assert.Equal(("set", "reset"), (RuleChangeAction.Set.ToName(), RuleChangeAction.Reset.ToName()));
        Assert.Equal(RuleChangeAction.Reset, RuleChangeActionNames.Parse("reset"));
        Assert.Throws<ArgumentException>(() => RuleChangeActionNames.Parse("Set"));

        var value = RuleValue.Set(RuleRegistry.NegativeStockAllowed, true, Now, Actor);
        var later = Now.AddMinutes(1);
        value.Change(false, later, A);
        Assert.Equal(("stock.negativeStockAllowed", false, later, A), (value.Key, value.Value, value.UpdatedAt, value.UpdatedBy));
    }

    // ---- AC-03, R17, R18: stock.negativeStockAllowed as a function of the change, the stock and the boolean

    [Theory]
    // onHand, delta, refused where negative stock is not allowed
    [InlineData("10", "-10", false)] // exactly what is there may go
    [InlineData("10", "-10.000001", true)]
    [InlineData("0", "-1", true)]
    [InlineData("0", "1", false)]
    [InlineData("0", "0", false)]
    // R21: a pair that is already below zero - a movement that raises it passes, also when it stays negative ...
    [InlineData("-5", "3", false)]
    [InlineData("-5", "5", false)]
    [InlineData("-5", "0", false)]
    // ... and one that lowers it is refused.
    [InlineData("-5", "-0.000001", true)]
    [InlineData("-5", "-1", true)]
    public void AC03_R17_A_pair_goes_below_zero_when_it_is_lowered_to_less_than_nothing(string onHand, string delta, bool refused)
    {
        var (q, d) = (decimal.Parse(onHand), decimal.Parse(delta));

        Assert.Equal(refused, StockMovements.GoesBelowZero(q, d, negativeStockAllowed: false));
        // R18: where negative stock is allowed nothing is refused.
        Assert.False(StockMovements.GoesBelowZero(q, d, negativeStockAllowed: true));
    }

    [Fact]
    public void AC03_R17_Short_pairs_are_judged_by_the_net_change_per_pair_and_by_the_rule()
    {
        var onHand = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 10m, [(B, W1)] = -5m };
        StockMovement[] movements =
        [
            new(A, W1, -6m), new(A, W1, -5m),   // 11 of 10: short
            new(A, W2, 11m),                    // the destination of a transfer is only raised
            new(B, W1, 3m),                     // raises a negative pair
            new(B, W2, -1m),                    // a pair without stock is at zero
        ];

        Assert.Equal([(A, W1), (B, W2)], StockMovements.ShortPairs(movements, onHand, negativeStockAllowed: false));
        Assert.Empty(StockMovements.ShortPairs(movements, onHand, negativeStockAllowed: true));
        Assert.Empty(StockMovements.ShortPairs([new(A, W1, -6m), new(A, W1, -4m)], onHand, negativeStockAllowed: false));
    }

    [Fact]
    public void R17_R38_Insufficient_stock_names_the_rule_its_value_and_the_keys_it_produced()
    {
        List<StockLineValues> lines = [new(A, 6m), new(B, 1m), new(A, 5m)];
        var onHand = new Dictionary<Guid, decimal> { [A] = 10m, [B] = 1m };

        var refused = StockTestSupport.IssueSufficiency(lines, onHand);

        Assert.Equal(ErrorCodes.InsufficientStock, refused!.Code);
        Assert.Equal(["lines[0].quantity", "lines[2].quantity"], refused.Errors!.Keys);
        var rule = Assert.Single(refused.Rules!);
        Assert.Equal(("stock.negativeStockAllowed", false), (rule.Key, rule.Value));
        Assert.Equal(refused.Errors.Keys, rule.Fields);
        // R18: with true there is no such check.
        Assert.Null(StockTestSupport.IssueSufficiency(lines, onHand, negativeStockAllowed: true));
        Assert.Null(StockTestSupport.IssueSufficiency([new(A, 1_000_000m)], new Dictionary<Guid, decimal>(), negativeStockAllowed: true));
    }

    [Fact]
    public void R17_A_reversal_is_judged_by_the_same_rule_and_names_it()
    {
        // Reversing a receipt of 10 A into W1 of which 4 have left.
        List<StockLineValues> original = [new(A, 10m)];
        StockMovement[] reversing = [new(A, W1, -10m)];
        var onHand = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 6m };

        var refused = StockLineChecks.ReversalSufficiency(original, reversing, onHand, negativeStockAllowed: false);

        Assert.Equal(ErrorCodes.InsufficientStock, refused!.Code);
        Assert.Equal(["lines[0].quantity"], refused.Errors!.Keys);
        Assert.Equal(("stock.negativeStockAllowed", false, "lines[0].quantity"),
            (refused.Rules!.Single().Key, refused.Rules!.Single().Value, refused.Rules!.Single().Fields.Single()));
        Assert.Null(StockLineChecks.ReversalSufficiency(original, reversing, onHand, negativeStockAllowed: true));
    }

    // ---- AC-03, R26, R27: sales.reservedStockProtected as a function of the change, the stock, what is reserved and the boolean

    [Theory]
    // onHand, delta, reserved before, reserved after, refused where reserved stock is protected
    [InlineData("10", "-2", "8", "8", false)]   // E6: available 2 -> 0
    [InlineData("10", "-3", "8", "8", true)]    // E6: available 2 -> -1
    [InlineData("10", "-8", "8", "0", false)]   // E6: the delivery of the order itself
    [InlineData("10", "-10", "20", "10", false)] // E7: one of two orders is delivered, availability stays -10
    [InlineData("5", "-1", "10", "10", true)]   // E8: already negative, and lowered further
    [InlineData("5", "1", "10", "10", false)]   // E8: a receipt
    [InlineData("0", "-1", "5", "5", true)]     // AC-77: independent of negative stock
    [InlineData("0", "-1", "0", "0", false)]    // nothing is reserved
    [InlineData("20", "-11", "20", "10", true)] // T-Q13: 11 for an order of 10, another order still awaits 10
    [InlineData("20", "-10", "20", "10", false)]
    [InlineData("20", "-15", "10", "0", false)] // T-Q13: nothing is reserved afterwards
    [InlineData("10", "-10", "8", "8", true)]   // AC-73: reversing the receipt the order relies on
    [InlineData("10", "0", "8", "8", false)]
    public void AC03_R27_Reserved_stock_is_taken_when_something_stays_reserved_stock_no_longer_covers_it_and_it_got_worse(
        string onHand, string delta, string reservedBefore, string reservedAfter, bool refused)
    {
        var (q, d, before, after) = (decimal.Parse(onHand), decimal.Parse(delta), decimal.Parse(reservedBefore), decimal.Parse(reservedAfter));

        Assert.Equal(refused, StockMovements.TakesReserved(q, d, before, after, reservedStockProtected: true));
        // R26: where reserved stock is not protected, the reserved quantity blocks nothing.
        Assert.False(StockMovements.TakesReserved(q, d, before, after, reservedStockProtected: false));
    }

    [Fact]
    public void R27_R38_Stock_reserved_names_the_rule_and_every_line_with_the_article_of_a_pair_it_refused()
    {
        // Stock 10 of A and 4 of B in W1; confirmed sales orders await 8 of A. An issue of 2 + 1 of A and 4 of B.
        List<StockLineValues> lines = [new(A, 2m), new(B, 4m), new(A, 1m)];
        var movements = lines.Select(l => new StockMovement(l.ArticleId, W1, -l.Quantity)).ToList();
        var onHand = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 10m, [(B, W1)] = 4m };
        var reserved = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 8m };

        var refused = StockLineChecks.Reservation(lines, movements, onHand, reserved, reserved, reservedStockProtected: true, reversal: false);

        Assert.Equal(ErrorCodes.StockReserved, refused!.Code);
        Assert.Equal(["lines[0].quantity", "lines[2].quantity"], refused.Errors!.Keys);
        var rule = Assert.Single(refused.Rules!);
        Assert.Equal(("sales.reservedStockProtected", true), (rule.Key, rule.Value));
        Assert.Equal(refused.Errors.Keys, rule.Fields);
        Assert.Null(StockLineChecks.Reservation(lines, movements, onHand, reserved, reserved, reservedStockProtected: false, reversal: false));
        // Up to what is available the issue passes; and so does a document that only brings goods in.
        Assert.Null(StockLineChecks.Reservation(
            [new(A, 2m)], [new(A, W1, -2m)], onHand, reserved, reserved, reservedStockProtected: true, reversal: false));
        Assert.Null(StockLineChecks.Reservation(
            [new(A, 2m)], [new(A, W1, 2m)], onHand, reserved, reserved, reservedStockProtected: true, reversal: false));
        // Stock and orders of another warehouse play no part (AC-72).
        Assert.Null(StockLineChecks.Reservation(
            [new(A, 3m)], [new(A, W2, -3m)], new Dictionary<(Guid, Guid), decimal> { [(A, W2)] = 3m }, Nothing, Nothing,
            reservedStockProtected: true, reversal: false));
    }

    [Fact]
    public void R28_A_delivery_up_to_what_its_order_awaits_releases_what_it_takes_and_is_not_refused()
    {
        // Stock 10, SO1 and SO2 for 10 each (E7): the delivery of SO2 lowers stock and what is reserved alike.
        var outstanding = new Dictionary<int, decimal> { [1] = 10m };
        var released = OrderProgress.Released([new LineFulfilment(1, 6m), new LineFulfilment(1, 4m)], outstanding);
        Assert.Equal(10m, released[1]);

        var onHand = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 10m };
        var before = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 20m };
        var after = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 20m - released[1] };
        Assert.Empty(StockMovements.ReservedPairs([new(A, W1, -10m)], onHand, before, after, reservedStockProtected: true));

        // The part above the outstanding quantity releases nothing (R28) and is judged.
        Assert.Equal(10m, OrderProgress.Released([new LineFulfilment(1, 11m)], outstanding)[1]);
        Assert.Equal(0m, OrderProgress.Released([new LineFulfilment(2, 3m)], outstanding)[2]);
        var twenty = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 20m };
        Assert.Equal([(A, W1)], StockMovements.ReservedPairs([new(A, W1, -11m)], twenty, before, after, reservedStockProtected: true));
        Assert.Empty(StockMovements.ReservedPairs([new(A, W1, -11m)], twenty, before, after, reservedStockProtected: false));
    }

    // ---- AC-03, R22: over-receipt and over-delivery as a function of received, document, ordered and the boolean

    [Theory]
    // ordered, fulfilled, document, refused where more than ordered is not allowed
    [InlineData("36", "0", "36", false)]
    [InlineData("36", "0", "36.000001", true)]
    [InlineData("36", "36", "0.000001", true)]
    [InlineData("36", "30", "6", false)]
    [InlineData("36", "30", "7", true)]
    // R24: a line that is already above its ordered quantity takes nothing more.
    [InlineData("36", "136", "1", true)]
    [InlineData("36", "0", "500", true)]
    public void AC03_R22_A_document_exceeds_an_order_line_when_received_plus_document_is_above_ordered(
        string ordered, string fulfilled, string document, bool refused)
    {
        var (o, f, d) = (decimal.Parse(ordered), decimal.Parse(fulfilled), decimal.Parse(document));

        Assert.Equal(refused, OrderProgress.Exceeds(o, f, d, overFulfilmentAllowed: false));
        // With true there is no check and no upper limit.
        Assert.False(OrderProgress.Exceeds(o, f, d, overFulfilmentAllowed: true));
    }

    [Fact]
    public void R22_R38_Exceeding_lines_follow_the_rule_and_the_refusal_names_the_rule_of_the_orders_kind()
    {
        var outstanding = new Dictionary<int, decimal> { [1] = 36m, [2] = 5m };
        LineFulfilment[] document = [new(1, 30m), new(2, 5m), new(1, 70m)];

        Assert.Equal([0, 2], OrderProgress.ExceedingLines(document, outstanding, overFulfilmentAllowed: false));
        Assert.Empty(OrderProgress.ExceedingLines(document, outstanding, overFulfilmentAllowed: true));

        foreach (var kind in OrderKind.All)
        {
            var refused = OrderLinkChecks.WithinOrder(document, outstanding, kind.OverFulfilmentAllowed, overFulfilmentAllowed: false);
            Assert.Equal(ErrorCodes.QuantityExceedsOrder, refused!.Code);
            Assert.Equal(["lines[0].quantity", "lines[2].quantity"], refused.Errors!.Keys);
            var rule = Assert.Single(refused.Rules!);
            Assert.Equal((kind.OverFulfilmentAllowed.Key, false), (rule.Key, rule.Value));
            Assert.Equal(refused.Errors.Keys, rule.Fields);
            Assert.Null(OrderLinkChecks.WithinOrder(document, outstanding, kind.OverFulfilmentAllowed, overFulfilmentAllowed: true));
        }
    }

    private static PurchaseOrder Confirmed(params decimal[] quantities)
    {
        var order = PurchaseOrder.Create(
            new DateOnly(2026, 10, 10), null, Guid.CreateVersion7(), W1, null, null,
            quantities.Select(q => new OrderLineEntry(A, Pcs, q, 1m)).ToList(), Now, Actor);
        order.Confirm("PO-000001", quantities.Select(_ => 1m).ToList(), Now, Actor);
        return order;
    }

    [Fact]
    public void R22_R23_E5_An_order_received_above_its_quantity_has_nothing_outstanding_and_is_full()
    {
        var order = Confirmed(36m);

        order.Fulfil([new LineFulfilment(1, 36m)], overFulfilmentAllowed: true);
        order.Fulfil([new LineFulfilment(1, 100m)], overFulfilmentAllowed: true);

        Assert.Equal(136m, order.Lines[0].FulfilledBaseQuantity);
        Assert.Equal(0m, order.Outstanding[1]);
        Assert.Equal(FulfilmentStatus.Full, order.FulfilmentStatus);
        // R24: the rule is off again - what was received stays, and nothing more is taken.
        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(1, 1m)], overFulfilmentAllowed: false));
        Assert.Equal(136m, order.Lines[0].FulfilledBaseQuantity);
        // A reversal is not judged by the rule: taking the 100 back leaves 36 of 36.
        order.TakeBack([new LineFulfilment(1, 100m)]);
        Assert.Equal((36m, 0m, FulfilmentStatus.Full), (order.Lines[0].FulfilledBaseQuantity, order.Outstanding[1], order.FulfilmentStatus));
        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(1, 1m)], overFulfilmentAllowed: false));
    }

    [Fact]
    public void R22_Also_where_more_than_ordered_is_allowed_a_document_names_lines_of_the_order_only()
    {
        var order = Confirmed(10m);

        Assert.Throws<InvalidOperationException>(() => order.Fulfil([new LineFulfilment(2, 1m)], overFulfilmentAllowed: true));
        Assert.Throws<ArgumentException>(() => order.Fulfil([new LineFulfilment(1, 0m)], overFulfilmentAllowed: true));
        Assert.Equal(0m, order.Lines[0].FulfilledBaseQuantity);
    }

    [Fact]
    public void R23_Outstanding_is_never_negative_and_full_means_at_or_above_the_ordered_quantity()
    {
        Assert.Equal(0m, OrderProgress.Outstanding(OrderStatus.Confirmed, 100m, 110m));
        Assert.Equal(50m, OrderProgress.Outstanding(OrderStatus.Confirmed, 100m, 50m));
        Assert.Equal(0m, OrderProgress.Outstanding(OrderStatus.Closed, 100m, 50m));

        Assert.Equal(FulfilmentStatus.Partial, OrderProgress.Status([(100m, 50m)]));
        Assert.Equal(FulfilmentStatus.Full, OrderProgress.Status([(100m, 110m)]));
        Assert.Equal(FulfilmentStatus.Full, OrderProgress.Status([(100m, 110m), (5m, 5m)]));
        // An excess on one line does not make up for another line.
        Assert.Equal(FulfilmentStatus.Partial, OrderProgress.Status([(100m, 110m), (5m, 4m)]));
        // What is reserved (and incoming) sums the outstanding quantities, so an excess never lowers it.
        Assert.Equal(1m, StockAvailability.Reserved([(OrderStatus.Confirmed, 100m, 110m), (OrderStatus.Confirmed, 5m, 4m)]));
    }

    // ---- R20: negative stock is read as it is

    [Fact]
    public void R20_A_count_takes_a_negative_book_quantity_and_brings_the_pair_to_what_was_counted()
    {
        Assert.True(CountRules.IsValidBookQuantity(-5m));
        Assert.False(CountRules.IsValidBookQuantity(-5.0000001m));
        Assert.Equal(7m, CountRules.Difference(2m, -5m));
        Assert.Equal([new StockMovement(A, W1, 7m)], StockMovements.OfCountLine(W1, new StockLineValues(A, 2m), -5m));
        // E10: a count of 0 on a pair at -5.
        Assert.Equal([new StockMovement(A, W1, 5m)], StockMovements.OfCountLine(W1, new StockLineValues(A, 0m), -5m));
        Assert.Equal(-5m, StockAvailability.Available(-5m, 0m));

        var count = StockDocument.Create(
            StockDocumentType.Count, new DateOnly(2026, 10, 10), W1, null, null, null, [new StockLineEntry(A, Pcs, 2m)], Now, Actor, [-5m]);
        Assert.Equal(-5m, count.Lines[0].BookQuantity);
    }

    // ---- R32, R33, R39: the partner of an order in validation

    private static Result<OrderValues> Order(OrderKind kind, bool partnerRequired, string? partnerId, bool partnerGiven = true, string orderDate = "2026-10-10", decimal quantity = 1m) =>
        OrderValidation.Values(
            kind, partnerRequired, orderDate, null, partnerId, W1.ToString(), null, null, [new OrderLineInput(A.ToString(), quantity, 1m)],
            partnerGiven: partnerGiven);

    [Fact]
    public void AC03_The_partner_rule_is_a_function_of_the_partner_and_the_boolean()
    {
        Assert.True(OrderPartnerRules.MayBeSavedWith(A, partnerRequired: true));
        Assert.True(OrderPartnerRules.MayBeSavedWith(A, partnerRequired: false));
        Assert.True(OrderPartnerRules.MayBeSavedWith(null, partnerRequired: false));
        Assert.False(OrderPartnerRules.MayBeSavedWith(null, partnerRequired: true));
    }

    [Fact]
    public void R33_R38_With_the_rule_true_an_order_without_a_partner_is_refused_naming_the_rule_of_its_kind()
    {
        foreach (var kind in OrderKind.All)
        {
            var refused = Order(kind, partnerRequired: true, partnerId: null).Error!;

            Assert.Equal(ErrorCodes.ValidationFailed, refused.Code);
            Assert.Equal([kind.PartnerField], refused.Errors!.Keys);
            var rule = Assert.Single(refused.Rules!);
            Assert.Equal((kind.PartnerRequired.Key, true), (rule.Key, rule.Value));
            Assert.Equal([kind.PartnerField], rule.Fields);
        }
    }

    [Fact]
    public void R34_With_the_rule_false_an_order_without_a_partner_is_accepted_and_one_with_a_partner_keeps_it()
    {
        foreach (var kind in OrderKind.All)
        {
            Assert.Null(Order(kind, partnerRequired: false, partnerId: null).Value!.PartnerId);
            Assert.Equal(B, Order(kind, partnerRequired: false, partnerId: B.ToString()).Value!.PartnerId);
            Assert.Equal(B, Order(kind, partnerRequired: true, partnerId: B.ToString()).Value!.PartnerId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R32_The_shape_of_the_partner_is_the_same_whatever_the_rule_and_its_refusal_names_no_rule(bool partnerRequired)
    {
        foreach (var kind in OrderKind.All)
        {
            // Neither a uuid nor null.
            foreach (var malformed in new[] { "abc", "", " ", "SUP" })
            {
                var refused = Order(kind, partnerRequired, malformed).Error!;
                Assert.Equal([kind.PartnerField], refused.Errors!.Keys);
                Assert.Null(refused.Rules);
            }
            // A replace that does not carry the property.
            var omitted = Order(kind, partnerRequired, partnerId: null, partnerGiven: false).Error!;
            Assert.Equal([kind.PartnerField], omitted.Errors!.Keys);
            Assert.Null(omitted.Rules);
        }
    }

    [Fact]
    public void R39_E12_A_mixed_refusal_reports_every_field_and_the_rule_names_only_its_own()
    {
        var refused = Order(OrderKind.Purchase, partnerRequired: true, partnerId: null, orderDate: "10.10.2026", quantity: -1m).Error!;

        Assert.Equal(["lines[0].quantity", "orderDate", "supplierId"], refused.Errors!.Keys.Order(StringComparer.Ordinal));
        var rule = Assert.Single(refused.Rules!);
        Assert.Equal(("purchase.partnerRequired", true), (rule.Key, rule.Value));
        Assert.Equal(["supplierId"], rule.Fields);

        // The same body where the partner is not required: the two invariants remain, and no rule is named.
        var invariants = Order(OrderKind.Purchase, partnerRequired: false, partnerId: null, orderDate: "10.10.2026", quantity: -1m).Error!;
        Assert.Equal(["lines[0].quantity", "orderDate"], invariants.Errors!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(invariants.Rules);
    }

    [Fact]
    public void R35_R36_An_order_without_a_partner_is_confirmed_and_a_document_linked_to_it_has_none()
    {
        var order = PurchaseOrder.Create(new DateOnly(2026, 10, 10), null, null, W1, null, null, [new OrderLineEntry(A, Pcs, 10m, 1m)], Now, Actor);
        Assert.Null(order.PartnerId);
        order.Confirm("PO-000001", [1m], Now, Actor);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        // R34: a replace may give a draft a partner and take it away again.
        var draft = SalesOrder.Create(new DateOnly(2026, 10, 10), null, null, W1, null, null, [new OrderLineEntry(A, Pcs, 1m, 1m)], Now, Actor);
        draft.Replace(draft.OrderDate, null, B, W1, null, null, [new OrderLineEntry(A, Pcs, 1m, 1m)], Now, Actor);
        Assert.Equal(B, draft.PartnerId);
        draft.Replace(draft.OrderDate, null, null, W1, null, null, [new OrderLineEntry(A, Pcs, 1m, 1m)], Now, Actor);
        Assert.Null(draft.PartnerId);

        var facts = new LinkedOrderFacts(order.Id, OrderStatus.Confirmed, W1, new Dictionary<int, Guid> { [1] = A }, PartnerId: null);
        List<StockLineEntry> lines = [new(A, Pcs, 10m, 1)];
        Assert.Null(OrderLinkChecks.Agreement(W1, lines, facts, partnerId: null));
        var mismatch = OrderLinkChecks.Agreement(W1, lines, facts, partnerId: B);
        Assert.Equal((ErrorCodes.OrderMismatch, "partnerId"), (mismatch!.Code, mismatch.Errors!.Keys.Single()));
        Assert.Null(mismatch.Rules);
    }

    // ---- R38, R39: how an error names a rule

    [Fact]
    public void R39_An_error_no_rule_caused_names_none_and_naming_one_keeps_the_error_as_it_is()
    {
        var error = AppError.Validation(new Dictionary<string, string[]> { ["b"] = ["x"], ["a"] = ["y"] });
        Assert.Null(error.Rules);
        Assert.Null(AppError.NotFound("x").Rules);
        Assert.Null(AppError.InvalidState("x").Rules);
        Assert.Null(AppError.ReferenceNotFound("supplierId", A).Rules);

        var named = error.RefusedBy(RuleRegistry.NegativeStockAllowed, false);

        Assert.Null(error.Rules);
        Assert.Equal((error.Code, error.Detail, error.Errors), (named.Code, named.Detail, named.Errors));
        var rule = Assert.Single(named.Rules!);
        // The fields are the keys of errors in their order there.
        Assert.Equal(("stock.negativeStockAllowed", false), (rule.Key, rule.Value));
        Assert.Equal(["b", "a"], rule.Fields);
        Assert.Equal(["a"], error.RefusedBy(RuleRegistry.PurchasePartnerRequired, true, ["a"]).Rules!.Single().Fields);
    }

    // ---- AC-04: Api reads no rule

    [Fact]
    public void AC04_Nothing_in_the_Api_asks_for_the_rules_port()
    {
        var api = typeof(Xerp.Api.Mcp.ToolCatalog).Assembly;
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        bool IsRules(Type type) => type == typeof(IRules) || type == typeof(TenantRules);

        foreach (var type in api.GetTypes())
        {
            Assert.DoesNotContain(type.GetFields(flags), f => IsRules(f.FieldType));
            Assert.DoesNotContain(type.GetConstructors(flags).SelectMany(c => c.GetParameters()), p => IsRules(p.ParameterType));
            Assert.DoesNotContain(type.GetMethods(flags).SelectMany(m => m.GetParameters()), p => IsRules(p.ParameterType));
        }
    }
}
