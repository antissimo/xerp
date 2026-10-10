using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Stock;
using Xerp.Application.Warehouses;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 011, AC-06: the rules that protect the default warehouse (R1, R4-R6) and the resolution of an omitted
/// <c>warehouseId</c> (R8-R10) - in Domain and Application, without HTTP and without a database.
/// </summary>
public class DefaultWarehouseRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();
    private static readonly string W = Guid.CreateVersion7().ToString();
    private static readonly string Partner = Guid.CreateVersion7().ToString();
    private static readonly Guid A = Guid.CreateVersion7();

    private static void AssertError(AppError? error, string code, params string[] expectedKeys)
    {
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), (error.Errors?.Keys ?? []).Order(StringComparer.Ordinal));
    }

    // ---- R1: the warehouse a tenant is created with ----

    [Fact]
    public void R1_The_default_warehouse_of_a_new_tenant_is_CENTRAL_active_default_and_written_by_the_first_key()
    {
        var warehouse = Warehouse.CreateDefault(T0, Actor);

        Assert.Equal(("CENTRAL", "Central warehouse"), (warehouse.Code, warehouse.Name));
        Assert.Equal(Address.Empty, warehouse.Address);
        Assert.True(warehouse.IsActive);
        Assert.True(warehouse.IsDefault);
        Assert.Equal((T0, T0, Actor, Actor), (warehouse.CreatedAt, warehouse.UpdatedAt, warehouse.CreatedBy, warehouse.UpdatedBy));
        Assert.NotEqual(Guid.Empty, warehouse.Id);
    }

    [Fact]
    public void R3_A_warehouse_created_by_hand_is_not_the_default()
    {
        Assert.False(Warehouse.Create("W1", "Shop", Address.Empty, true, T0, Actor).IsDefault);
    }

    // ---- R4: the default cannot be deactivated ----

    [Fact]
    public void R4_Deactivating_the_default_warehouse_is_DEFAULT_WAREHOUSE_with_key_isActive()
    {
        AssertError(DefaultWarehouseRules.Replace(isDefault: true, isActive: false), ErrorCodes.DefaultWarehouse, "isActive");
        Assert.Null(DefaultWarehouseRules.Replace(isDefault: true, isActive: true));
        Assert.Null(DefaultWarehouseRules.Replace(isDefault: false, isActive: false));
        Assert.Null(DefaultWarehouseRules.Replace(isDefault: false, isActive: true));
    }

    [Fact]
    public void R3_R4_The_default_warehouse_is_renamed_recoded_and_addressed_but_never_deactivated()
    {
        var warehouse = Warehouse.CreateDefault(T0, Actor);
        var later = T0.AddHours(1);
        var address = Address.Create("Street 1", null, "10000", "Zagreb", null, "HR");

        warehouse.Replace("MAIN", "Main warehouse", address, true, later, Other);

        Assert.Equal(("MAIN", "Main warehouse", address), (warehouse.Code, warehouse.Name, warehouse.Address));
        Assert.True(warehouse.IsDefault);
        Assert.Equal((later, Other), (warehouse.UpdatedAt, warehouse.UpdatedBy));

        Assert.Throws<InvalidOperationException>(() => warehouse.Replace("MAIN", "Main warehouse", address, false, later.AddHours(1), Actor));
        Assert.True(warehouse.IsActive);
        Assert.Equal((later, Other), (warehouse.UpdatedAt, warehouse.UpdatedBy));
    }

    // ---- R5: the default cannot be deleted ----

    [Fact]
    public void R5_Deleting_the_default_warehouse_is_DEFAULT_WAREHOUSE_without_keys()
    {
        AssertError(DefaultWarehouseRules.Delete(isDefault: true), ErrorCodes.DefaultWarehouse);
        Assert.Null(DefaultWarehouseRules.Delete(isDefault: false));
    }

    // ---- R6, R7: changing the default ----

    [Fact]
    public void R6_Only_an_active_warehouse_can_become_the_default()
    {
        AssertError(DefaultWarehouseRules.SetDefault(isActive: false), ErrorCodes.DefaultWarehouse, "isActive");
        Assert.Null(DefaultWarehouseRules.SetDefault(isActive: true));

        var inactive = Warehouse.Create("W2", "Closed", Address.Empty, false, T0, Actor);
        Assert.Throws<InvalidOperationException>(() => inactive.MakeDefault(T0.AddHours(1), Actor));
        Assert.False(inactive.IsDefault);
        Assert.Equal(T0, inactive.UpdatedAt);
    }

    [Fact]
    public void R6_R7_Set_default_moves_the_flag_and_stamps_both_warehouses()
    {
        var former = Warehouse.CreateDefault(T0, Actor);
        var target = Warehouse.Create("W1", "Shop", Address.Empty, true, T0, Actor);
        var later = T0.AddHours(1);

        former.ClearDefault(later, Other);
        target.MakeDefault(later, Other);

        Assert.False(former.IsDefault);
        Assert.True(target.IsDefault);
        Assert.Equal((later, Other), (former.UpdatedAt, former.UpdatedBy));
        Assert.Equal((later, Other), (target.UpdatedAt, target.UpdatedBy));
        Assert.Equal((T0, Actor), (target.CreatedAt, target.CreatedBy));

        // R7: the former default is ordinary again and can be deactivated.
        former.Replace(former.Code, former.Name, former.Address, false, later.AddHours(1), Actor);
        Assert.False(former.IsActive);
        Assert.Null(DefaultWarehouseRules.Replace(former.IsDefault, isActive: false));
        Assert.Null(DefaultWarehouseRules.Delete(former.IsDefault));
    }

    // ---- R8-R10: which warehouse a new document is on ----

    [Fact]
    public void R8_A_named_warehouse_is_taken_whatever_the_default_or_the_order_say()
    {
        var (named, order, defaultId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(named, DefaultWarehouseRules.ForNewDocument(named, null, defaultId));
        // 009/R21: a linked document that names another warehouse than its order's is refused later (ORDER_MISMATCH), not corrected here.
        Assert.Equal(named, DefaultWarehouseRules.ForNewDocument(named, order, defaultId));
    }

    [Fact]
    public void R8_Without_a_warehouse_an_unlinked_document_is_on_the_default_warehouse()
    {
        var defaultId = Guid.CreateVersion7();

        Assert.Equal(defaultId, DefaultWarehouseRules.ForNewDocument(null, null, defaultId));
    }

    [Fact]
    public void R10_Without_a_warehouse_a_linked_document_is_on_the_warehouse_of_its_order()
    {
        var (order, defaultId) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(order, DefaultWarehouseRules.ForNewDocument(null, order, defaultId));
    }

    // ---- R8, R9, E8, E9: the form of warehouseId on a stock document ----

    private static CreateStockDocumentInput Document(string type, string? warehouseId, string? toWarehouseId = null) =>
        new(type, "2026-10-10", warehouseId, [new StockLineInput(A.ToString(), 1m)], ToWarehouseId: toWarehouseId);

    [Theory]
    [InlineData("receipt")]
    [InlineData("issue")]
    [InlineData("count")]
    public void R8_Create_of_a_receipt_issue_or_count_may_omit_the_warehouse(string type)
    {
        var omitted = StockDocumentValidation.Create(Document(type, null));
        var named = StockDocumentValidation.Create(Document(type, W));

        Assert.True(omitted.IsSuccess);
        Assert.True(omitted.Value.WarehouseOmitted);
        Assert.True(named.IsSuccess);
        Assert.False(named.Value.WarehouseOmitted);
        Assert.Equal(Guid.Parse(W), named.Value.Values.WarehouseId);
    }

    [Theory]
    [InlineData("receipt", "")]
    [InlineData("receipt", "abc")]
    [InlineData("issue", "W1")]
    [InlineData("count", " ")]
    public void E8_A_malformed_warehouseId_is_not_an_omitted_one(string type, string warehouseId)
    {
        var result = StockDocumentValidation.Create(Document(type, warehouseId));

        Assert.False(result.IsSuccess);
        AssertError(result.Error, ErrorCodes.ValidationFailed, "warehouseId");
    }

    [Fact]
    public void R9_E9_A_transfer_names_its_source_also_when_the_destination_is_given()
    {
        var result = StockDocumentValidation.Create(Document("transfer", null, Guid.CreateVersion7().ToString()));

        Assert.False(result.IsSuccess);
        AssertError(result.Error, ErrorCodes.ValidationFailed, "warehouseId");
    }

    [Fact]
    public void R10_A_linked_receipt_may_omit_the_warehouse()
    {
        var result = StockDocumentValidation.Create(new CreateStockDocumentInput(
            "receipt", "2026-10-10", null, [new StockLineInput(A.ToString(), 1m, null, 1)], PurchaseOrderId: Guid.CreateVersion7().ToString()));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.WarehouseOmitted);
        Assert.NotNull(result.Value.Link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void R9_Replace_of_a_stock_document_must_name_the_warehouse(string? warehouseId)
    {
        var result = StockDocumentValidation.Replace(new ReplaceStockDocumentInput
        {
            DocumentDate = "2026-10-10", WarehouseId = warehouseId, Reference = null, Note = null, Lines = [new StockLineInput(A.ToString(), 1m)],
        });

        Assert.False(result.IsSuccess);
        AssertError(result.Error, ErrorCodes.ValidationFailed, "warehouseId");
    }

    // ---- R8, R9: the form of warehouseId on an order ----

    private static Result<OrderValues> Order(OrderKind kind, string? warehouseId, bool warehouseOptional) =>
        OrderValidation.Values(
            kind, "2026-10-10", null, Partner, warehouseId, null, null, [new OrderLineInput(A.ToString(), 1m, 1m)],
            warehouseOptional: warehouseOptional);

    [Fact]
    public void R8_Create_of_an_order_of_either_kind_may_omit_the_warehouse()
    {
        foreach (var kind in OrderKind.All)
        {
            var omitted = Order(kind, null, warehouseOptional: true);
            var named = Order(kind, W, warehouseOptional: true);

            Assert.True(omitted.IsSuccess);
            Assert.True(omitted.Value.WarehouseOmitted);
            Assert.True(named.IsSuccess);
            Assert.False(named.Value.WarehouseOmitted);
            Assert.Equal(Guid.Parse(W), named.Value.WarehouseId);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void E8_A_malformed_warehouseId_on_an_order_is_not_an_omitted_one(string warehouseId)
    {
        foreach (var kind in OrderKind.All)
            AssertError(Order(kind, warehouseId, warehouseOptional: true).Error, ErrorCodes.ValidationFailed, "warehouseId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void R9_Replace_of_an_order_must_name_the_warehouse(string? warehouseId)
    {
        foreach (var kind in OrderKind.All)
            AssertError(Order(kind, warehouseId, warehouseOptional: false).Error, ErrorCodes.ValidationFailed, "warehouseId");
    }
}

/// <summary>
/// Spec 011, R1: provisioning hands the store the tenant, its first key and its default warehouse together -
/// one call, which the store saves in one transaction - and the warehouse is written by that first key.
/// </summary>
public class TenantProvisioningDefaultWarehouseTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock : Xerp.Application.Ports.IClock
    {
        public DateTime UtcNow => T0;
    }

    private sealed class FixedKeys : Xerp.Application.Ports.IApiKeyGenerator, Xerp.Application.Ports.IApiKeyHasher
    {
        public string Generate() => "xerp_secret";

        public string Hash(string key) => new('a', 64);
    }

    private sealed class RecordingStore(bool codeExists = false) : Xerp.Application.Ports.ITenantProvisioningStore
    {
        public List<(Xerp.Domain.Tenancy.Tenant Tenant, Xerp.Domain.Tenancy.ApiKey Key, Warehouse Warehouse)> Added { get; } = [];

        public Task AddAsync(
            Xerp.Domain.Tenancy.Tenant tenant, Xerp.Domain.Tenancy.ApiKey firstKey, Warehouse defaultWarehouse, CancellationToken cancellationToken = default)
        {
            Added.Add((tenant, firstKey, defaultWarehouse));
            return Task.CompletedTask;
        }

        public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) => Task.FromResult(codeExists);
    }

    private static Xerp.Application.Tenants.TenantProvisioning Provisioning(RecordingStore store)
    {
        var keys = new FixedKeys();
        return new Xerp.Application.Tenants.TenantProvisioning(store, keys, keys, new FixedClock());
    }

    [Fact]
    public async Task R1_A_tenant_is_stored_together_with_its_first_key_and_its_default_warehouse()
    {
        var store = new RecordingStore();

        var result = await Provisioning(store).CreateAsync(new Xerp.Application.Tenants.CreateTenantInput("acme", "Acme"));

        Assert.True(result.IsSuccess);
        var (tenant, key, warehouse) = Assert.Single(store.Added);
        Assert.Equal((result.Value.Tenant.Id, result.Value.ApiKey.Id), (tenant.Id, key.Id));
        Assert.Equal(tenant.Id, key.TenantId);
        Assert.Equal((Warehouse.DefaultCode, Warehouse.DefaultName, true, true), (warehouse.Code, warehouse.Name, warehouse.IsActive, warehouse.IsDefault));
        Assert.Equal(Address.Empty, warehouse.Address);
        Assert.Equal((key.Id, key.Id, T0, T0), (warehouse.CreatedBy, warehouse.UpdatedBy, warehouse.CreatedAt, warehouse.UpdatedAt));
    }

    [Fact]
    public async Task R1_A_refused_tenant_stores_nothing_no_warehouse_either()
    {
        var invalid = new RecordingStore();
        var taken = new RecordingStore(codeExists: true);

        var invalidResult = await Provisioning(invalid).CreateAsync(new Xerp.Application.Tenants.CreateTenantInput("", "Acme"));
        var takenResult = await Provisioning(taken).CreateAsync(new Xerp.Application.Tenants.CreateTenantInput("acme", "Acme"));

        Assert.Equal(ErrorCodes.ValidationFailed, invalidResult.Error!.Code);
        Assert.Equal(ErrorCodes.CodeTaken, takenResult.Error!.Code);
        Assert.Empty(invalid.Added);
        Assert.Empty(taken.Added);
    }
}
