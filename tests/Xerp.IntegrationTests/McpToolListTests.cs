using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 003, AC-40 to AC-45 (tool list and metadata) and AC-03 (MCP tools hold no data access).
/// Spec 004, AC-90 to AC-93: the list grows to 24 tools; the ten new ones have the same metadata rules.
/// Spec 005, AC-80: the list grows to 32 tools; the eight stock tools have the same metadata rules.
/// Spec 006, AC-80: 33 tools. Spec 007, AC-80: 37 tools, a <c>lines</c> item gains <c>unitId</c>.
/// Spec 008, AC-80: still 37 tools; <c>type</c> allows <c>count</c>.
/// Spec 009, AC-85: 45 tools; the stock tools gain the order link (<c>purchaseOrderId</c>, <c>orderLineNo</c>).
/// Spec 010, AC-85: 53 tools; <c>stock_document_create</c> and <c>stock_document_list</c> gain <c>salesOrderId</c>.
/// Spec 011, AC-70: 57 tools; <c>warehouse_list</c> gains <c>isDefault</c>; <c>warehouseId</c> leaves <c>required</c> of the
/// three create tools of documents.
/// </summary>
[Collection(XerpCollection.Name)]
public class McpToolListTests(XerpFixture app)
{
    private sealed record Expected(string Name, string[] Properties, string[] Required, string Kind);

    private static readonly string[] Address =
        ["addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    // Spec 003, section 5.3, with the argument spec 007, section 5, adds to article_list (alternativeUnitId).
    private static readonly Expected[] Spec003Tools =
    [
        new("whoami", [], [], "read"),
        new("uom_list", ["search", "isActive", "limit", "offset"], [], "read"),
        new("uom_get", ["id", "code"], [], "read"),
        new("uom_create", ["code", "name", "isActive"], ["code", "name"], "create"),
        new("uom_update", ["id", "code", "name", "isActive"], ["id", "code", "name", "isActive"], "update"),
        new("uom_delete", ["id"], ["id"], "delete"),
        new("article_list", ["search", "type", "baseUnitId", "alternativeUnitId", "isActive", "limit", "offset"], [], "read"),
        new("article_get", ["id", "code"], [], "read"),
        new("article_create", ["code", "name", "type", "baseUnitId", "description", "isActive"],
            ["code", "name", "type", "baseUnitId"], "create"),
        new("article_update", ["id", "code", "name", "description", "type", "baseUnitId", "isActive"],
            ["id", "code", "name", "description", "type", "baseUnitId", "isActive"], "update"),
        new("article_delete", ["id"], ["id"], "delete"),
        new("api_key_list", ["search", "actorType", "isActive", "limit", "offset"], [], "read"),
        new("api_key_get", ["id"], ["id"], "read"),
        new("api_key_revoke", ["id"], ["id"], "update"),
    ];

    // Spec 004, section 5.1.
    private static readonly Expected[] Spec004Tools =
    [
        new("partner_list", ["search", "isCustomer", "isSupplier", "isActive", "limit", "offset"], [], "read"),
        new("partner_get", ["id", "code"], [], "read"),
        new("partner_create", ["code", "name", "isCustomer", "isSupplier", "taxId", .. Address, "isActive"],
            ["code", "name"], "create"),
        new("partner_update", ["id", "code", "name", "isCustomer", "isSupplier", "taxId", .. Address, "isActive"],
            ["id", "code", "name", "isCustomer", "isSupplier", "taxId", .. Address, "isActive"], "update"),
        new("partner_delete", ["id"], ["id"], "delete"),
        new("warehouse_list", ["search", "isActive", "isDefault", "limit", "offset"], [], "read"),
        new("warehouse_get", ["id", "code"], [], "read"),
        new("warehouse_create", ["code", "name", .. Address, "isActive"], ["code", "name"], "create"),
        new("warehouse_update", ["id", "code", "name", .. Address, "isActive"],
            ["id", "code", "name", .. Address, "isActive"], "update"),
        new("warehouse_delete", ["id"], ["id"], "delete"),
    ];

    // Spec 005, section 5, with the arguments spec 006, section 5, adds (toWarehouseId) and spec 009, section 5
    // (purchaseOrderId on create and list) and spec 010, section 5 (salesOrderId on create and list) and spec 011a,
    // section 5 (partnerId on create, update and list).
    private static readonly Expected[] Spec005Tools =
    [
        new("stock_document_list", ["type", "status", "warehouseId", "partnerId", "purchaseOrderId", "salesOrderId", "search", "limit", "offset"], [], "read"),
        new("stock_document_get", ["id", "number"], [], "read"),
        new("stock_document_create", ["type", "documentDate", "warehouseId", "toWarehouseId", "partnerId", "purchaseOrderId", "salesOrderId", "lines", "reference", "note"],
            ["type", "documentDate", "lines"], "create"),
        new("stock_document_update", ["id", "documentDate", "warehouseId", "toWarehouseId", "partnerId", "reference", "note", "lines"],
            ["id", "documentDate", "warehouseId", "reference", "note", "lines"], "update"),
        new("stock_document_delete", ["id"], ["id"], "delete"),
        new("stock_document_post", ["id"], ["id"], "post"),
        new("stock_on_hand_list", ["articleId", "warehouseId", "limit", "offset"], [], "read"),
        new("stock_ledger_entry_list", ["articleId", "warehouseId", "documentId", "limit", "offset"], [], "read"),
    ];

    // Spec 006, section 5.
    private static readonly Expected[] Spec006Tools =
    [
        new("stock_document_reverse", ["id", "documentDate", "note"], ["id", "documentDate"], "post"),
    ];

    // Spec 007, section 5. article_unit_set creates or replaces: annotated as an update.
    private static readonly Expected[] Spec007Tools =
    [
        new("article_unit_list", ["articleId", "limit", "offset"], ["articleId"], "read"),
        new("article_unit_get", ["articleId", "unitId"], ["articleId", "unitId"], "read"),
        new("article_unit_set", ["articleId", "unitId", "factor"], ["articleId", "unitId", "factor"], "update"),
        new("article_unit_delete", ["articleId", "unitId"], ["articleId", "unitId"], "delete"),
    ];

    // Spec 009, section 5. Confirmation is permanent (the annotations of stock_document_post); close and reopen
    // undo each other (the annotations of a create).
    private static readonly Expected[] Spec009Tools =
    [
        new("purchase_order_list", ["status", "receiptStatus", "supplierId", "warehouseId", "search", "limit", "offset"], [], "read"),
        new("purchase_order_get", ["id", "number"], [], "read"),
        new("purchase_order_create", ["orderDate", "supplierId", "warehouseId", "lines", "expectedDate", "reference", "note"],
            ["orderDate", "supplierId", "lines"], "create"),
        new("purchase_order_update", ["id", "orderDate", "expectedDate", "supplierId", "warehouseId", "reference", "note", "lines"],
            ["id", "orderDate", "expectedDate", "supplierId", "warehouseId", "reference", "note", "lines"], "update"),
        new("purchase_order_delete", ["id"], ["id"], "delete"),
        new("purchase_order_confirm", ["id"], ["id"], "post"),
        new("purchase_order_close", ["id"], ["id"], "create"),
        new("purchase_order_reopen", ["id"], ["id"], "create"),
    ];

    // Spec 010, section 5: the eight tools of spec 009 mirrored, with the same arguments and annotations.
    private static readonly Expected[] Spec010Tools =
    [
        new("sales_order_list", ["status", "deliveryStatus", "customerId", "warehouseId", "search", "limit", "offset"], [], "read"),
        new("sales_order_get", ["id", "number"], [], "read"),
        new("sales_order_create", ["orderDate", "customerId", "warehouseId", "lines", "requestedDate", "reference", "note"],
            ["orderDate", "customerId", "lines"], "create"),
        new("sales_order_update", ["id", "orderDate", "requestedDate", "customerId", "warehouseId", "reference", "note", "lines"],
            ["id", "orderDate", "requestedDate", "customerId", "warehouseId", "reference", "note", "lines"], "update"),
        new("sales_order_delete", ["id"], ["id"], "delete"),
        new("sales_order_confirm", ["id"], ["id"], "post"),
        new("sales_order_close", ["id"], ["id"], "create"),
        new("sales_order_reopen", ["id"], ["id"], "create"),
    ];

    // Spec 011, section 5. Set-default and rebuild change state, destroy nothing and may be repeated.
    private static readonly Expected[] Spec011Tools =
    [
        new("warehouse_set_default", ["id"], ["id"], "repeatable"),
        new("warehouse_stock_list", ["id", "search", "isActive", "hasStock", "limit", "offset"], ["id"], "read"),
        new("stock_balance_difference_list", ["limit", "offset"], [], "read"),
        new("stock_balance_rebuild", [], [], "repeatable"),
    ];

    // The complete list (spec 004, section 5.3; specs 005 to 011, section 5): a new tool must be added here by its spec.
    private static readonly Expected[] Tools =
        [.. Spec003Tools, .. Spec004Tools, .. Spec005Tools, .. Spec006Tools, .. Spec007Tools, .. Spec009Tools, .. Spec010Tools,
            .. Spec011Tools];

    private async Task<Dictionary<string, Tool>> ListToolsAsync(string? key = null)
    {
        key ??= (await app.NewTenantAsync()).Key;
        await using var mcp = await app.McpAsync(key);
        var tools = await mcp.Client.ListToolsAsync();
        return tools.ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);
    }

    private static string[] Sorted(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task AC40_S004_AC90_S005_AC80_S006_AC80_S007_AC80_S009_AC85_S010_AC85_S011_AC70_Tool_list_is_exactly_the_57_tools_of_the_specs()
    {
        var tools = await ListToolsAsync();

        Assert.Equal(14, Spec003Tools.Length);
        Assert.Equal(10, Spec004Tools.Length);
        Assert.Equal(8, Spec005Tools.Length);
        Assert.Single(Spec006Tools);
        Assert.Equal(4, Spec007Tools.Length);
        Assert.Equal(8, Spec009Tools.Length);
        Assert.Equal(8, Spec010Tools.Length);
        Assert.Equal(4, Spec011Tools.Length);
        Assert.Equal(57, Tools.Length);
        Assert.Equal(Sorted(Tools.Select(t => t.Name)), Sorted(tools.Keys));
        Assert.DoesNotContain("api_key_create", tools.Keys);
        Assert.DoesNotContain(tools.Keys, name => name.StartsWith("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AC41_Every_tool_has_description_closed_input_schema_and_output_schema() =>
        AssertMetadata(await ListToolsAsync(), Spec003Tools);

    [Fact]
    public async Task S004_AC91_Partner_and_warehouse_tools_have_description_closed_input_schema_and_output_schema()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec004Tools);
        Assert.Equal(13, tools["partner_update"].InputSchema.GetProperty("required").GetArrayLength());
        Assert.Equal(10, tools["warehouse_update"].InputSchema.GetProperty("required").GetArrayLength());
    }

    [Theory]
    [InlineData("partner_create")]
    [InlineData("partner_update")]
    [InlineData("warehouse_create")]
    [InlineData("warehouse_update")]
    public async Task S004_AC91_Nullable_text_arguments_accept_string_or_null(string toolName)
    {
        // Section 5.1: each address argument (and taxId) is "string | null"; section 5.2 for the schema type.
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        var properties = tool!.InputSchema.GetProperty("properties");
        string[] nullable = toolName.StartsWith("partner", StringComparison.Ordinal) ? ["taxId", .. Address] : Address;
        foreach (var name in nullable)
        {
            Assert.True(properties.TryGetProperty(name, out var schema), $"{toolName} has no argument '{name}'.");
            var types = SchemaTypes(schema);
            Assert.True(types.Contains("string") && types.Contains("null"),
                $"{toolName}.{name}: schema must allow string and null, is {schema}");
        }
    }

    [Fact]
    public async Task AC42_Every_tool_has_the_annotations_of_the_spec() =>
        AssertAnnotations(await ListToolsAsync(), Spec003Tools);

    [Fact]
    public async Task S004_AC92_Partner_and_warehouse_tools_have_the_annotations_of_their_pattern() =>
        AssertAnnotations(await ListToolsAsync(), Spec004Tools);

    [Fact]
    public async Task S005_AC80_Stock_tools_have_description_closed_input_schema_output_schema_and_annotations()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec005Tools);
        AssertAnnotations(tools, Spec005Tools);
        var post = tools["stock_document_post"].Annotations!;
        Assert.True(post.DestructiveHint);
        Assert.False(post.ReadOnlyHint);
    }

    [Fact]
    public async Task S006_AC80_Stock_document_reverse_is_described_closed_and_destructive()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec006Tools);
        AssertAnnotations(tools, Spec006Tools);
        var reverse = tools["stock_document_reverse"];
        Assert.Equal(["documentDate", "id"],
            Sorted(reverse.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!)));
        Assert.True(reverse.Annotations!.DestructiveHint);
        Assert.False(reverse.Annotations.ReadOnlyHint);
        Assert.False(reverse.Annotations.IdempotentHint);
    }

    [Theory]
    [InlineData("stock_document_create")]
    [InlineData("stock_document_update")]
    public async Task S006_AC80_Stock_document_write_tools_have_a_described_optional_toWarehouseId(string toolName)
    {
        // Section 5: "new optional argument toWarehouseId (uuid or null)".
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        var schema = tool!.InputSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("toWarehouseId", out var property),
            $"{toolName} has no toWarehouseId property.");
        Assert.True(property.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
            $"{toolName}.toWarehouseId has no description.");
        var types = SchemaTypes(property);
        Assert.True(types.Contains("string") && types.Contains("null"), $"{toolName}.toWarehouseId must allow a string and null: {property}");
        Assert.DoesNotContain("toWarehouseId", schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Theory]
    [InlineData("stock_document_create")]
    [InlineData("stock_document_update")]
    public async Task S005_AC80_S007_AC80_S009_AC85_Lines_is_an_array_of_closed_article_quantity_unit_and_order_line_objects(string toolName)
    {
        // Spec 005, section 5: "lines is an array of { articleId: uuid, quantity: number } objects (closed schema)".
        // Spec 007, section 5: "a lines item is { articleId, quantity, unitId? } (unitId: uuid or null)".
        // Spec 009, section 5: the lines item of create and update "gains orderLineNo? (integer or null)".
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        var lines = tool!.InputSchema.GetProperty("properties").GetProperty("lines");
        Assert.Contains("array", SchemaTypes(lines));
        Assert.True(lines.TryGetProperty("items", out var items), $"{toolName}.lines has no items schema: {lines}");
        items = Resolve(tool.InputSchema, items);
        Assert.True(items.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.False, $"{toolName}.lines: the line schema is not closed: {items}");
        var properties = items.GetProperty("properties");
        Assert.Equal(["articleId", "orderLineNo", "quantity", "unitId"], Sorted(properties.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(["articleId", "quantity"], Sorted(items.GetProperty("required").EnumerateArray().Select(r => r.GetString()!)));
        var quantity = SchemaTypes(properties.GetProperty("quantity"));
        Assert.True(quantity.Contains("number") && !quantity.Contains("string"),
            $"{toolName}.lines[].quantity must be a JSON number: {properties.GetProperty("quantity")}");
        var unitId = properties.GetProperty("unitId");
        Assert.True(unitId.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
            $"{toolName}.lines[].unitId has no description: {unitId}");
        var unitTypes = SchemaTypes(unitId);
        Assert.True(unitTypes.Contains("string") && unitTypes.Contains("null"),
            $"{toolName}.lines[].unitId must allow a string and null: {unitId}");
        var orderLineNo = properties.GetProperty("orderLineNo");
        Assert.True(orderLineNo.TryGetProperty("description", out var lineNoDescription) && !string.IsNullOrWhiteSpace(lineNoDescription.GetString()),
            $"{toolName}.lines[].orderLineNo has no description: {orderLineNo}");
        var lineNoTypes = SchemaTypes(orderLineNo);
        Assert.True(lineNoTypes.Contains("integer") && lineNoTypes.Contains("null") && !lineNoTypes.Contains("string"),
            $"{toolName}.lines[].orderLineNo must allow an integer and null: {orderLineNo}");
    }

    [Fact]
    public async Task S007_AC80_Article_unit_tools_have_description_closed_input_schema_output_schema_and_annotations()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec007Tools);
        AssertAnnotations(tools, Spec007Tools);
        var set = tools["article_unit_set"];
        var factor = SchemaTypes(set.InputSchema.GetProperty("properties").GetProperty("factor"));
        Assert.True(factor.Contains("number") && !factor.Contains("string"), "article_unit_set.factor must be a JSON number.");
        Assert.True(set.Annotations!.DestructiveHint);
        Assert.True(set.Annotations.IdempotentHint);
    }

    [Fact]
    public async Task S008_AC80_The_tool_list_is_unchanged_and_the_descriptions_mention_counts_and_COUNT_OUTDATED()
    {
        var tools = await ListToolsAsync();

        // "No new tool; tools/list still returns exactly the 37 tools of spec 007" — 45 since spec 009, 53 since spec 010, 57 since spec 011.
        Assert.Equal(57, Tools.Length);
        Assert.Equal(Sorted(Tools.Select(t => t.Name)), Sorted(tools.Keys));
        Assert.DoesNotContain(tools.Keys, name => name.Contains("count", StringComparison.OrdinalIgnoreCase));
        // The one place where a criterion asks for words in a description (AC-80).
        Assert.Contains("count", tools["stock_document_create"].Description ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT_OUTDATED", tools["stock_document_post"].Description ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task S007_AC80_Article_list_has_a_described_optional_alternativeUnitId()
    {
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue("article_list", out var tool), "Tool 'article_list' is not listed.");
        Assert.True(tool!.InputSchema.GetProperty("properties").TryGetProperty("alternativeUnitId", out var property),
            "article_list has no alternativeUnitId property.");
        Assert.True(property.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
            "article_list.alternativeUnitId has no description.");
        if (tool.InputSchema.TryGetProperty("required", out var required))
            Assert.DoesNotContain("alternativeUnitId", required.EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task S009_AC85_Purchase_order_tools_have_description_closed_input_schema_output_schema_and_annotations()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec009Tools);
        AssertAnnotations(tools, Spec009Tools);
        var confirm = tools["purchase_order_confirm"].Annotations!;
        Assert.True(confirm.DestructiveHint);
        Assert.False(confirm.ReadOnlyHint);
        Assert.False(confirm.IdempotentHint);
        foreach (var name in new[] { "purchase_order_close", "purchase_order_reopen" })
        {
            Assert.False(tools[name].Annotations!.DestructiveHint);
            Assert.False(tools[name].Annotations!.ReadOnlyHint);
        }
    }

    [Theory]
    [InlineData("purchase_order_create")]
    [InlineData("purchase_order_update")]
    [InlineData("sales_order_create")]
    [InlineData("sales_order_update")]
    public async Task S009_AC85_S010_AC85_Order_lines_is_an_array_of_closed_article_quantity_price_and_unit_objects(string toolName)
    {
        // Section 5: "lines is an array of { articleId: uuid, quantity: number, unitPrice: number, unitId?: uuid | null }
        // (closed schema)".
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        var lines = tool!.InputSchema.GetProperty("properties").GetProperty("lines");
        Assert.Contains("array", SchemaTypes(lines));
        Assert.True(lines.TryGetProperty("items", out var items), $"{toolName}.lines has no items schema: {lines}");
        items = Resolve(tool.InputSchema, items);
        Assert.True(items.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.False, $"{toolName}.lines: the line schema is not closed: {items}");
        var properties = items.GetProperty("properties");
        Assert.Equal(["articleId", "quantity", "unitId", "unitPrice"], Sorted(properties.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(["articleId", "quantity", "unitPrice"], Sorted(items.GetProperty("required").EnumerateArray().Select(r => r.GetString()!)));
        foreach (var number in new[] { "quantity", "unitPrice" })
        {
            var types = SchemaTypes(properties.GetProperty(number));
            Assert.True(types.Contains("number") && !types.Contains("string"),
                $"{toolName}.lines[].{number} must be a JSON number: {properties.GetProperty(number)}");
        }
        foreach (var property in properties.EnumerateObject())
            Assert.True(property.Value.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
                $"{toolName}.lines[].{property.Name} has no description.");
        var unitTypes = SchemaTypes(properties.GetProperty("unitId"));
        Assert.True(unitTypes.Contains("string") && unitTypes.Contains("null"), $"{toolName}.lines[].unitId must allow a string and null.");
    }

    [Theory]
    [InlineData("purchase_order_create", "expectedDate,reference,note")]
    [InlineData("purchase_order_update", "expectedDate,reference,note")]
    [InlineData("stock_document_create", "purchaseOrderId")]
    [InlineData("sales_order_create", "requestedDate,reference,note")]
    [InlineData("sales_order_update", "requestedDate,reference,note")]
    [InlineData("stock_document_create", "salesOrderId")]
    public async Task S009_AC85_S010_AC85_Nullable_arguments_accept_a_string_or_null(string toolName, string arguments)
    {
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        foreach (var name in arguments.Split(','))
        {
            Assert.True(tool!.InputSchema.GetProperty("properties").TryGetProperty(name, out var schema), $"{toolName} has no argument '{name}'.");
            var types = SchemaTypes(schema);
            Assert.True(types.Contains("string") && types.Contains("null"), $"{toolName}.{name}: schema must allow string and null, is {schema}");
        }
    }

    [Fact]
    public async Task S009_AC85_The_stock_tools_carry_the_order_link_and_the_descriptions_say_what_the_spec_asks()
    {
        var tools = await ListToolsAsync();

        // stock_document_update does not take the link (R19); its lines item takes orderLineNo (pinned above).
        Assert.False(tools["stock_document_update"].InputSchema.GetProperty("properties").TryGetProperty("purchaseOrderId", out _));
        foreach (var name in new[] { "stock_document_create", "stock_document_list" })
        {
            Assert.True(tools[name].InputSchema.GetProperty("properties").TryGetProperty("purchaseOrderId", out _), $"{name} has no purchaseOrderId.");
            if (tools[name].InputSchema.TryGetProperty("required", out var required))
                Assert.DoesNotContain("purchaseOrderId", required.EnumerateArray().Select(r => r.GetString()));
        }
        // Section 5, "Descriptions say": the error codes and field names an agent has to act on.
        var post = tools["stock_document_post"].Description ?? "";
        Assert.Contains("ORDER_NOT_OPEN", post, StringComparison.Ordinal);
        Assert.Contains("QUANTITY_EXCEEDS_ORDER", post, StringComparison.Ordinal);
        Assert.Contains("outstandingBaseQuantity", post, StringComparison.Ordinal);
        var get = tools["purchase_order_get"].Description ?? "";
        foreach (var word in new[] { "receipt", "purchaseOrderId", "orderLineNo", "outstandingBaseQuantity" })
            Assert.Contains(word, get, StringComparison.Ordinal);
        Assert.Contains("isSupplier", tools["purchase_order_create"].Description ?? "", StringComparison.Ordinal);
        Assert.Contains("unitPrice", tools["purchase_order_create"].Description ?? "", StringComparison.Ordinal);
        Assert.Contains("PARTNER_ROLE_MISSING", tools["purchase_order_create"].Description ?? "", StringComparison.Ordinal);
        Assert.Contains("INVALID_STATE", tools["purchase_order_confirm"].Description ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task S010_AC85_Sales_order_tools_have_description_closed_input_schema_output_schema_and_annotations()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec010Tools);
        AssertAnnotations(tools, Spec010Tools);
        var confirm = tools["sales_order_confirm"].Annotations!;
        Assert.True(confirm.DestructiveHint);
        Assert.False(confirm.ReadOnlyHint);
        Assert.False(confirm.IdempotentHint);
        // "required as their mirrors in spec 009".
        foreach (var (sales, purchase) in Spec010Tools.Zip(Spec009Tools))
        {
            Assert.Equal(purchase.Name.Replace("purchase_order", "sales_order"), sales.Name);
            Assert.Equal(purchase.Required.Length, tools[sales.Name].InputSchema.TryGetProperty("required", out var required) ? required.GetArrayLength() : 0);
        }
    }

    [Fact]
    public async Task S010_AC85_The_stock_tools_carry_the_sales_order_link_and_the_descriptions_say_what_the_spec_asks()
    {
        var tools = await ListToolsAsync();

        Assert.False(tools["stock_document_update"].InputSchema.GetProperty("properties").TryGetProperty("salesOrderId", out _));
        foreach (var name in new[] { "stock_document_create", "stock_document_list" })
        {
            Assert.True(tools[name].InputSchema.GetProperty("properties").TryGetProperty("salesOrderId", out _), $"{name} has no salesOrderId.");
            if (tools[name].InputSchema.TryGetProperty("required", out var required))
                Assert.DoesNotContain("salesOrderId", required.EnumerateArray().Select(r => r.GetString()));
        }
        // Section 5, "Descriptions say": the error codes and field names an agent has to act on.
        Assert.Contains("INSUFFICIENT_STOCK", tools["stock_document_post"].Description ?? "", StringComparison.Ordinal);
        var get = tools["sales_order_get"].Description ?? "";
        foreach (var word in new[] { "issue", "salesOrderId", "orderLineNo" })
            Assert.Contains(word, get, StringComparison.Ordinal);
        Assert.Contains("isCustomer", tools["sales_order_create"].Description ?? "", StringComparison.Ordinal);
        Assert.Contains("availableQuantity", tools["sales_order_confirm"].Description ?? "", StringComparison.Ordinal);
        var onHand = tools["stock_on_hand_list"].Description ?? "";
        foreach (var quantity in new[] { "incomingQuantity", "reservedQuantity", "availableQuantity" })
            Assert.Contains(quantity, onHand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S011_AC70_The_four_tools_of_spec_011_have_description_closed_input_schema_output_schema_and_annotations()
    {
        var tools = await ListToolsAsync();

        AssertMetadata(tools, Spec011Tools);
        AssertAnnotations(tools, Spec011Tools);
        foreach (var name in new[] { "warehouse_set_default", "stock_balance_rebuild" })
        {
            var a = tools[name].Annotations!;
            Assert.True(a.ReadOnlyHint == false && a.DestructiveHint == false && a.IdempotentHint == true && a.OpenWorldHint == false,
                $"{name}: readOnly/destructive/idempotent/openWorld = {(a.ReadOnlyHint, a.DestructiveHint, a.IdempotentHint, a.OpenWorldHint)}.");
        }
        foreach (var name in new[] { "warehouse_stock_list", "stock_balance_difference_list" })
            Assert.True(tools[name].Annotations!.ReadOnlyHint == true && tools[name].Annotations!.OpenWorldHint == false, $"{name} is not read-only.");
        var hasStock = SchemaTypes(tools["warehouse_stock_list"].InputSchema.GetProperty("properties").GetProperty("hasStock"));
        Assert.True(hasStock.Contains("boolean") && !hasStock.Contains("string"), "warehouse_stock_list.hasStock must be a JSON boolean.");
    }

    [Fact]
    public async Task S011_AC70_Warehouse_list_has_a_described_optional_boolean_isDefault()
    {
        var tools = await ListToolsAsync();

        var schema = tools["warehouse_list"].InputSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("isDefault", out var property), "warehouse_list has no isDefault property.");
        Assert.True(property.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
            "warehouse_list.isDefault has no description.");
        var types = SchemaTypes(property);
        Assert.True(types.Contains("boolean") && !types.Contains("string"), $"warehouse_list.isDefault must be a JSON boolean: {property}");
        if (schema.TryGetProperty("required", out var required))
            Assert.DoesNotContain("isDefault", required.EnumerateArray().Select(r => r.GetString()));
    }

    [Theory]
    [InlineData("stock_document_create")]
    [InlineData("purchase_order_create")]
    [InlineData("sales_order_create")]
    public async Task S011_AC70_WarehouseId_is_optional_on_create_and_allows_a_uuid_or_null(string toolName)
    {
        // Section 5: "warehouseId leaves required and allows a uuid or null".
        var tools = await ListToolsAsync();

        var schema = tools[toolName].InputSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("warehouseId", out var property), $"{toolName} has no warehouseId.");
        Assert.DoesNotContain("warehouseId", schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        var types = SchemaTypes(property);
        Assert.True(types.Contains("string") && types.Contains("null"), $"{toolName}.warehouseId must allow a string and null: {property}");
        // The update tools still require it (R9).
        var update = tools[toolName.Replace("_create", "_update")].InputSchema;
        Assert.Contains("warehouseId", update.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task S011_AC70_Every_tool_that_returns_a_warehouse_has_isDefault_in_its_output_schema()
    {
        var tools = await ListToolsAsync();

        foreach (var name in new[] { "warehouse_get", "warehouse_create", "warehouse_update", "warehouse_set_default" })
        {
            Assert.True(tools.TryGetValue(name, out var tool), $"Tool '{name}' is not listed.");
            Assert.True(tool!.OutputSchema is { } output && output.GetProperty("properties").TryGetProperty("isDefault", out _),
                $"{name}: the outputSchema has no isDefault: {tool.OutputSchema}");
        }
    }

    [Fact]
    public async Task S011_Section5_The_descriptions_say_what_the_spec_asks()
    {
        var tools = await ListToolsAsync();
        string Description(string name) => tools.TryGetValue(name, out var tool) ? tool.Description ?? "" : "";

        // Section 5, "Descriptions say": the names an agent has to act on.
        Assert.Contains("isDefault", Description("warehouse_list"), StringComparison.Ordinal);
        foreach (var name in new[] { "stock_document_create", "purchase_order_create", "sales_order_create" })
            Assert.Contains("default warehouse", Description(name), StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { "warehouse_update", "warehouse_delete" })
        {
            Assert.Contains("DEFAULT_WAREHOUSE", Description(name), StringComparison.Ordinal);
            Assert.Contains("warehouse_set_default", Description(name), StringComparison.Ordinal);
        }
        Assert.Contains("stock_on_hand_list", Description("warehouse_stock_list"), StringComparison.Ordinal);
        Assert.Contains("stock_balance_rebuild", Description("stock_balance_difference_list"), StringComparison.Ordinal);
        Assert.Contains("ledger", Description("stock_balance_rebuild"), StringComparison.OrdinalIgnoreCase);
    }

    // ---- spec 011a, AC-11 and section 5 ----

    [Theory]
    [InlineData("stock_document_create", true)]
    [InlineData("stock_document_update", true)]
    [InlineData("stock_document_list", false)]
    public async Task S011a_AC11_The_stock_document_tools_have_a_described_optional_partnerId(string toolName, bool nullable)
    {
        // Section 5: create and update gain "partnerId? (uuid or null)", list gains "partnerId? (uuid)"; the tool list is unchanged.
        var tools = await ListToolsAsync();

        Assert.Equal(57, tools.Count);
        var schema = tools[toolName].InputSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("partnerId", out var property), $"{toolName} has no partnerId property.");
        Assert.True(property.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
            $"{toolName}.partnerId has no description.");
        var types = SchemaTypes(property);
        Assert.True(types.Contains("string"), $"{toolName}.partnerId must allow a string: {property}");
        if (nullable)
            Assert.True(types.Contains("null"), $"{toolName}.partnerId must allow null: {property}");
        if (schema.TryGetProperty("required", out var required))
            Assert.DoesNotContain("partnerId", required.EnumerateArray().Select(r => r.GetString()));
        // R4: the partner is a header field — the line schema of spec 009 is pinned above and has no partnerId.
    }

    [Theory]
    [InlineData("stock_document_get")]
    [InlineData("stock_document_create")]
    [InlineData("stock_document_update")]
    [InlineData("stock_document_post")]
    [InlineData("stock_document_reverse")]
    [InlineData("stock_document_list")]
    public async Task S011a_Section5_Every_tool_that_returns_a_stock_document_has_partner_in_its_output_schema(string toolName)
    {
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        Assert.True(tool!.OutputSchema is { ValueKind: JsonValueKind.Object }, $"{toolName}: no outputSchema.");
        var output = tool.OutputSchema!.Value;
        if (toolName == "stock_document_list")
            // The summary is the item of the list: wherever its schema is declared, it names the property.
            Assert.Contains("\"partner\"", output.GetRawText(), StringComparison.Ordinal);
        else
            Assert.True(output.GetProperty("properties").TryGetProperty("partner", out _), $"{toolName}: the outputSchema has no partner: {output}");
    }

    [Fact]
    public async Task S011a_Section5_The_descriptions_say_what_the_spec_asks()
    {
        var tools = await ListToolsAsync();
        string Description(string name) => tools.TryGetValue(name, out var tool) ? tool.Description ?? "" : "";

        // Section 5, "Descriptions say": the names an agent has to act on.
        foreach (var name in new[] { "stock_document_create", "stock_document_update", "stock_document_list" })
            Assert.Contains("partnerId", Description(name), StringComparison.Ordinal);
        Assert.Contains("isSupplier", Description("stock_document_create"), StringComparison.Ordinal);
        Assert.Contains("isCustomer", Description("stock_document_create"), StringComparison.Ordinal);
        // "stock_document_create and stock_document_update add PARTNER_ROLE_MISSING, stock_document_post adds PARTNER_ROLE_MISSING".
        foreach (var name in new[] { "stock_document_create", "stock_document_update", "stock_document_post" })
            Assert.Contains("PARTNER_ROLE_MISSING", Description(name), StringComparison.Ordinal);
        Assert.Contains("IN_USE", Description("partner_delete"), StringComparison.Ordinal);
        Assert.Contains("stock document", Description("partner_delete"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Follows a local <c>$ref</c> (<c>#/$defs/Name</c>) inside the tool's input schema.</summary>
    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
            return schema;
        var target = root;
        foreach (var segment in reference.GetString()!.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            target = target.GetProperty(segment);
        return target;
    }

    private static void AssertMetadata(Dictionary<string, Tool> tools, Expected[] expectedTools)
    {
        foreach (var expected in expectedTools)
        {
            Assert.True(tools.TryGetValue(expected.Name, out var tool), $"Tool '{expected.Name}' is not listed.");
            Assert.False(string.IsNullOrWhiteSpace(tool!.Description), $"{expected.Name}: description is empty.");

            var schema = tool.InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False, $"{expected.Name}: inputSchema is not closed.");

            var properties = schema.TryGetProperty("properties", out var declared)
                ? declared.EnumerateObject().ToArray()
                : [];
            Assert.True(Sorted(expected.Properties).SequenceEqual(Sorted(properties.Select(p => p.Name))),
                $"{expected.Name}: properties are [{string.Join(", ", properties.Select(p => p.Name))}], expected [{string.Join(", ", expected.Properties)}].");
            foreach (var property in properties)
                Assert.True(property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString()),
                    $"{expected.Name}.{property.Name}: input property has no description.");

            var required = schema.TryGetProperty("required", out var requiredNames)
                ? requiredNames.EnumerateArray().Select(r => r.GetString()!).ToArray()
                : [];
            Assert.True(Sorted(expected.Required).SequenceEqual(Sorted(required)),
                $"{expected.Name}: required is [{string.Join(", ", required)}], expected [{string.Join(", ", expected.Required)}].");

            Assert.True(tool.OutputSchema is { ValueKind: JsonValueKind.Object }, $"{expected.Name}: no outputSchema.");
        }
    }

    private static void AssertAnnotations(Dictionary<string, Tool> tools, Expected[] expectedTools)
    {
        foreach (var expected in expectedTools)
        {
            Assert.True(tools.TryGetValue(expected.Name, out var tool), $"Tool '{expected.Name}' is not listed.");
            var a = tool!.Annotations;
            Assert.True(a is not null, $"{expected.Name}: no annotations.");
            var actual = (a!.ReadOnlyHint, a.DestructiveHint, a.IdempotentHint, a.OpenWorldHint);
            var ok = expected.Kind switch
            {
                // destructiveHint and idempotentHint are not specified for read-only tools.
                "read" => a.ReadOnlyHint == true && a.OpenWorldHint == false,
                "create" => actual == (false, false, false, false),
                "update" => actual == (false, true, true, false),
                "delete" => actual == (false, true, false, false),
                // Spec 005, section 5: posting is permanent.
                "post" => actual == (false, true, false, false),
                // Spec 011, section 5: warehouse_set_default and stock_balance_rebuild.
                "repeatable" => actual == (false, false, true, false),
                _ => false,
            };
            Assert.True(ok, $"{expected.Name} ({expected.Kind}): readOnly/destructive/idempotent/openWorld = {actual}.");
        }
    }

    /// <summary>The JSON types a property schema allows: <c>type</c> as a string or an array, or through anyOf / oneOf.</summary>
    private static HashSet<string> SchemaTypes(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object)
            return types;
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
                types.Add(type.GetString()!);
            else if (type.ValueKind == JsonValueKind.Array)
                foreach (var member in type.EnumerateArray())
                    types.Add(member.GetString()!);
        }
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(keyword, out var alternatives) && alternatives.ValueKind == JsonValueKind.Array)
                foreach (var alternative in alternatives.EnumerateArray())
                    types.UnionWith(SchemaTypes(alternative));
        return types;
    }

    [Theory]
    [InlineData("article_create", "type", "service,stock")]
    [InlineData("article_list", "type", "service,stock")]
    [InlineData("api_key_list", "actorType", "agent,human")]
    [InlineData("stock_document_create", "type", "count,issue,receipt,transfer")]
    [InlineData("stock_document_list", "type", "count,issue,receipt,transfer")]
    [InlineData("stock_document_list", "status", "draft,posted,reversed")]
    [InlineData("purchase_order_list", "status", "closed,confirmed,draft")]
    [InlineData("purchase_order_list", "receiptStatus", "full,none,partial")]
    [InlineData("sales_order_list", "status", "closed,confirmed,draft")]
    [InlineData("sales_order_list", "deliveryStatus", "full,none,partial")]
    public async Task AC43_S005_AC80_S006_AC80_S008_AC80_S009_AC85_Enumerated_arguments_are_enums_in_the_input_schema(string toolName, string property, string values)
    {
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        var schema = tool!.InputSchema.GetProperty("properties").GetProperty(property);
        Assert.True(schema.TryGetProperty("enum", out var members), $"{toolName}.{property} has no enum: {schema}");
        Assert.Equal(values.Split(','), Sorted(members.EnumerateArray().Select(m => m.GetString()!)));
    }

    [Fact]
    public async Task AC44_S004_AC93_Tool_list_and_instructions_are_the_same_for_every_tenant()
    {
        var a = await app.NewTenantAsync("Tenant Alpha");
        var b = await app.NewTenantAsync("Tenant Beta");
        await using var mcpA = await app.McpAsync(a.Key);
        await using var mcpB = await app.McpAsync(b.Key);

        var toolsA = JsonSerializer.SerializeToElement(
            (await mcpA.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToArray(), McpJsonUtilities.DefaultOptions);
        var toolsB = JsonSerializer.SerializeToElement(
            (await mcpB.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToArray(), McpJsonUtilities.DefaultOptions);

        Assert.NotEqual(0, toolsA.GetArrayLength());
        McpAssert.JsonEqual(toolsA, toolsB, "tools/list differs between tenants");
        Assert.Equal(mcpA.Client.ServerInstructions, mcpB.Client.ServerInstructions);
        // T6: static metadata contains no tenant data.
        foreach (var tenant in new[] { a, b })
            foreach (var secret in new[] { tenant.Id.ToString(), tenant.Code, tenant.Name, tenant.Key })
            {
                Assert.DoesNotContain(secret, toolsA.ToString());
                Assert.DoesNotContain(secret, mcpA.Client.ServerInstructions ?? "");
            }
    }

    [Theory]
    [InlineData("api_key_create")]
    [InlineData("tenant_create")]
    [InlineData("nope")]
    public async Task AC45_Unknown_tool_name_is_a_protocol_error_not_a_tool_result(string toolName)
    {
        var tenant = await app.NewTenantAsync();
        await using var mcp = await app.McpAsync(tenant.Key);

        CallToolResult? result = null;
        var failure = await Record.ExceptionAsync(async () =>
            result = await mcp.CallAsync(toolName, new { name = "bot", actorType = "agent" }));

        Assert.True(failure is not null, $"Expected a JSON-RPC error, got a tool result: {(result is null ? "" : McpAssert.Describe(result))}");
        Assert.IsAssignableFrom<McpException>(failure);
        // The connection is still usable and no key was created.
        await mcp.OkAsync("whoami");
        Assert.Equal(1, (await Keys.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public void AC03_Mcp_tool_types_hold_no_data_access()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Namespace is "Xerp.Api.Mcp" || (t.Namespace?.StartsWith("Xerp.Api.Mcp.", StringComparison.Ordinal) ?? false))
            .ToList();

        Assert.True(types.Count > 0, "No types found in namespace Xerp.Api.Mcp: the MCP tools must live there (AC-03).");
        var violations = new List<string>();
        foreach (var type in types)
        {
            foreach (var constructor in type.GetConstructors(all))
                violations.AddRange(constructor.GetParameters().Where(p => IsDataAccess(p.ParameterType))
                    .Select(p => $"{type.FullName}..ctor({p.ParameterType.Name} {p.Name})"));
            foreach (var method in type.GetMethods(all))
                violations.AddRange(method.GetParameters().Where(p => IsDataAccess(p.ParameterType))
                    .Select(p => $"{type.FullName}.{method.Name}({p.ParameterType.Name} {p.Name})"));
            violations.AddRange(type.GetFields(all).Where(f => IsDataAccess(f.FieldType))
                .Select(f => $"{type.FullName}.{f.Name} : {f.FieldType.Name}"));
            violations.AddRange(type.GetProperties(all).Where(p => IsDataAccess(p.PropertyType))
                .Select(p => $"{type.FullName}.{p.Name} : {p.PropertyType.Name}"));
        }

        Assert.True(violations.Count == 0, "MCP types reference data access:\n" + string.Join("\n", violations));
    }

    private static bool IsDataAccess(Type type)
    {
        if (type.HasElementType)
            return IsDataAccess(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(IsDataAccess))
            return true;
        if (type.Name == "IXerpDb" || type.GetInterfaces().Any(i => i.Name == "IXerpDb"))
            return true;
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false)
                return true;
        return type.GetInterfaces().Any(i => i.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false);
    }
}
