using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

// Spec 010: the criteria "mirrored from spec 009" are the abstract classes of spec 009 run for sales orders
// (substitution table of spec 010: customer for supplier, requestedDate, SO-, an issue with salesOrderId,
// deliveredBaseQuantity / deliveryStatus, reservedQuantity). What only a delivery has — stock that must be
// there, the reserved and the available quantity — is in SalesDeliveryTests and SalesOrderProgressTests.

/// <summary>Spec 010, AC-10, AC-11, AC-20 to AC-28 (009/AC-10, AC-11, AC-20 to AC-28 mirrored).</summary>
[Collection(XerpCollection.Name)]
public class SalesOrderDraftTests(XerpFixture app) : OrderDraftTests(app, OrderApi.Sales);

/// <summary>Spec 010, AC-30 to AC-35, AC-63, AC-64 (009/AC-30 to AC-35, AC-63, AC-65 mirrored).</summary>
[Collection(XerpCollection.Name)]
public class SalesOrderLifecycleTests(XerpFixture app) : OrderLifecycleTests(app, OrderApi.Sales);

/// <summary>Spec 010, AC-49 (009/AC-48 to AC-50 and AC-54 mirrored).</summary>
[Collection(XerpCollection.Name)]
public class SalesOrderLinkTests(XerpFixture app) : OrderLinkTests(app, OrderApi.Sales);

/// <summary>Spec 010, AC-80 to AC-82 (009/AC-80 to AC-82 mirrored).</summary>
[Collection(XerpCollection.Name)]
public class SalesOrderMasterEffectsTests(XerpFixture app) : OrderMasterEffectsTests(app, OrderApi.Sales);

/// <summary>Spec 010, AC-90 to AC-92 (009/AC-90 to AC-92 mirrored) and the mirrored part of AC-93.</summary>
[Collection(XerpCollection.Name)]
public class SalesOrderIsolationTests(XerpFixture app) : OrderIsolationTests(app, OrderApi.Sales);

/// <summary>Spec 010, AC-86 to AC-88, as far as they mirror 009/AC-86 to AC-88.</summary>
[Collection(XerpCollection.Name)]
public class McpSalesOrderToolTests(XerpFixture app) : McpOrderToolTests(app, OrderApi.Sales);
