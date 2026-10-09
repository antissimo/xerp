using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

// Spec 009: the acceptance criteria about the order itself are written once, against OrderApi, in the abstract
// classes named below (ADR-0016: an order is one mechanism with two users). These classes run them for
// purchase orders. What only a goods receipt has is in PurchaseReceiptTests and PurchaseOrderProgressTests.

/// <summary>Spec 009, AC-10, AC-11, AC-20 to AC-28.</summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderDraftTests(XerpFixture app) : OrderDraftTests(app, OrderApi.Purchase);

/// <summary>Spec 009, AC-30 to AC-35, AC-63, AC-65.</summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderLifecycleTests(XerpFixture app) : OrderLifecycleTests(app, OrderApi.Purchase);

/// <summary>Spec 009, AC-48 to AC-50, AC-54.</summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderLinkTests(XerpFixture app) : OrderLinkTests(app, OrderApi.Purchase);

/// <summary>Spec 009, AC-80 to AC-82.</summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderMasterEffectsTests(XerpFixture app) : OrderMasterEffectsTests(app, OrderApi.Purchase);

/// <summary>Spec 009, AC-90 to AC-93.</summary>
[Collection(XerpCollection.Name)]
public class PurchaseOrderIsolationTests(XerpFixture app) : OrderIsolationTests(app, OrderApi.Purchase);

/// <summary>Spec 009, AC-86 to AC-88.</summary>
[Collection(XerpCollection.Name)]
public class McpPurchaseOrderToolTests(XerpFixture app) : McpOrderToolTests(app, OrderApi.Purchase);
