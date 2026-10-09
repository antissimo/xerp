using Xerp.Application.ApiKeys;
using Xerp.Application.Articles;
using Xerp.Application.ArticleUnits;
using Xerp.Application.Common;
using Xerp.Application.Identity;
using Xerp.Application.Orders;
using Xerp.Application.Partners;
using Xerp.Application.Stock;
using Xerp.Application.UnitsOfMeasure;
using Xerp.Application.Warehouses;
using static Xerp.Api.Mcp.ToolSchemas;

namespace Xerp.Api.Mcp;

/// <summary>A tool that takes no arguments binds to this: any argument is then an unknown one.</summary>
public sealed record NoArguments;

/// <summary>
/// The complete list of MCP tools (spec 003, 5.3; spec 004, 5.1; specs 005 to 009, 5). Adding an operation means adding its tool here; the
/// tool-list test holds the expected names literally.
/// </summary>
public static class ToolCatalog
{
    private const string Validation = "`VALIDATION_FAILED`: an argument is missing, unknown or invalid; `errors` names each offending argument - correct them and call again.";
    private const string CodeTaken = "`CODE_TAKEN`: another record already has this code (codes are case-insensitive) - choose another code.";

    private static readonly (string, System.Text.Json.Nodes.JsonObject)[] Paging =
    [
        ("limit", Integer("Maximum number of items to return; default 50.", 1, ListRules.MaxLimit)),
        ("offset", Integer("Number of matching items to skip; default 0.", 0)),
    ];

    private static (string, System.Text.Json.Nodes.JsonObject) Search(string fields) =>
        ("search", Text($"Case-insensitive text that {fields} must contain; at most {ListRules.MaxSearchLength} characters."));

    private static (string, System.Text.Json.Nodes.JsonObject) IsActiveFilter(string what) =>
        ("isActive", Flag($"Return only active (true) or only inactive (false) {what}; omit for both."));

    private static (string, System.Text.Json.Nodes.JsonObject)[] UnitFields(bool isActiveRequired) =>
    [
        ("code", Text("Unique code of the unit within the tenant, 1-50 characters: letters, digits, '.', '_' or '-'.")),
        ("name", Text("Name of the unit, 1-200 characters.")),
        ("isActive", Flag(isActiveRequired
            ? "Whether the unit can be newly assigned to records. Required: an update never changes it silently."
            : "Whether the unit can be newly assigned to records; default true.")),
    ];

    private static (string, System.Text.Json.Nodes.JsonObject)[] ArticleFields(bool update) =>
    [
        ("code", Text("Unique code of the article within the tenant, 1-50 characters: letters, digits, '.', '_' or '-'.")),
        ("name", Text("Name of the article, 1-200 characters.")),
        ("description", update
            ? NullableText("Free text, at most 2000 characters. Required: pass null to clear it.")
            : Text("Optional free text, at most 2000 characters.")),
        ("type", Text("`stock`: a physical item whose quantity is tracked. `service`: not tracked in stock.", "stock", "service")),
        ("baseUnitId", Uuid("The `id` of the unit of measure the article is counted in (see `uom_list`). Not the unit's code.")),
        ("isActive", Flag(update
            ? "Whether the article can be newly used on records. Required: an update never changes it silently."
            : "Whether the article can be newly used on records; default true.")),
    ];

    /// <summary>The six address arguments; on an update each is required but may be null (spec 004, R7).</summary>
    private static (string, System.Text.Json.Nodes.JsonObject)[] AddressFields(bool update)
    {
        var rule = update ? " Required: pass null for no value." : " Optional; null or empty means no value.";
        return
        [
            ("addressLine1", NullableText("First line of the postal address (street and number), at most 200 characters." + rule)),
            ("addressLine2", NullableText("Second line of the postal address, at most 200 characters." + rule)),
            ("postalCode", NullableText("Postal code, at most 20 characters; kept as entered." + rule)),
            ("city", NullableText("City or town, at most 100 characters." + rule)),
            ("region", NullableText("Region, county or state, at most 100 characters." + rule)),
            ("countryCode", NullableText("Country as exactly two upper-case letters (ISO 3166-1 alpha-2), for example \"HR\" or \"DE\"; not a country name, not lower case." + rule)),
        ];
    }

    private static (string, System.Text.Json.Nodes.JsonObject)[] PartnerFields(bool update) =>
    [
        ("code", Text("Unique code of the partner within the tenant, 1-50 characters: letters, digits, '.', '_' or '-'.")),
        ("name", Text("Name of the company or person, 1-200 characters.")),
        ("isCustomer", Flag(update
            ? "Whether the tenant sells to this partner. Required. At least one of `isCustomer`, `isSupplier` must be true."
            : "Whether the tenant sells to this partner; default false. At least one of `isCustomer`, `isSupplier` must be true.")),
        ("isSupplier", Flag(update
            ? "Whether the tenant buys from this partner. Required. At least one of `isCustomer`, `isSupplier` must be true."
            : "Whether the tenant buys from this partner; default false. At least one of `isCustomer`, `isSupplier` must be true.")),
        ("taxId", NullableText("Tax or VAT number as free text, at most 50 characters; no format is checked and it is not unique."
            + (update ? " Required: pass null for no value." : " Optional; null or empty means no value."))),
        .. AddressFields(update),
        ("isActive", Flag(update
            ? "Whether the partner can be newly used on documents. Required: an update never changes it silently."
            : "Whether the partner can be newly used on documents; default true.")),
    ];

    private static (string, System.Text.Json.Nodes.JsonObject)[] WarehouseFields(bool update) =>
    [
        ("code", Text("Unique code of the warehouse within the tenant, 1-50 characters: letters, digits, '.', '_' or '-'.")),
        ("name", Text("Name of the warehouse, 1-200 characters.")),
        .. AddressFields(update),
        ("isActive", Flag(update
            ? "Whether the warehouse can be newly used on documents. Required: an update never changes it silently."
            : "Whether the warehouse can be newly used on documents; default true.")),
    ];

    private const string StockReferences =
        "`REFERENCE_NOT_FOUND`: `warehouseId`, `toWarehouseId` or a line's `articleId` or `unitId` names no record of this tenant (`errors` says which; find ids with `warehouse_list`, `article_list`, `uom_list`). "
        + "`REFERENCE_INACTIVE`: that warehouse, article or alternative unit is inactive and would be newly used. "
        + "`ARTICLE_NOT_STOCKED`: a line names a service article; only articles of type `stock` have stock. "
        + "`UNIT_NOT_ON_ARTICLE`: a line's `unitId` is neither the base unit nor an alternative unit of its article; see `article_unit_list`, or leave `unitId` out. "
        + "`QUANTITY_NOT_CONVERTIBLE`: a line's quantity, converted to the article's base unit with the current factor, rounds to zero or exceeds 999999999.999999 (`errors` names `lines[i].quantity`). "
        + "Errors about a line are keyed by position, for example `lines[0].articleId`.";

    private const string OrderLink =
        "`REFERENCE_NOT_FOUND` also: `purchaseOrderId` names no purchase order, or `salesOrderId` no sales order, of this tenant, or a line's `orderLineNo` is not a line of that order (`errors` names `lines[i].orderLineNo`). "
        + "`ORDER_NOT_OPEN`: the order is a draft or closed (`errors` names `purchaseOrderId` or `salesOrderId`); confirm it with `purchase_order_confirm` / `sales_order_confirm` or reopen it with `purchase_order_reopen` / `sales_order_reopen`. "
        + "`ORDER_MISMATCH`: the document disagrees with its order - `warehouseId` is not the order's warehouse, or a line's `articleId` is not the article of the order line it names (`errors` says which); read the order with `purchase_order_get` or `sales_order_get`.";

    private const string DocumentPosted = "`INVALID_STATE`: the document is already posted (or reversed); a posted document is permanent and cannot be changed, deleted or posted again. "
        + "To correct one, use `stock_document_reverse` and create a new document.";

    private static (string, System.Text.Json.Nodes.JsonObject)[] StockDocumentFields(bool update) =>
    [
        .. StockDocumentHeader(update),
        ("lines", ArrayOf(
            "The lines, 1 to 200; they are numbered 1…n in the order given" + (update ? " and replace all stored lines." : ".")
            + " The same article may appear on several lines, also in different units - except on a count, where each article appears at most once."
            + " Stock is always kept in the article's base unit: a line in another unit is converted with the article's factor for that unit,"
            + " and the document shows both what was entered (`unit`, `quantity`) and what it is in base units (`factor`, `baseUnit`, `baseQuantity`).",
            1, 200, ["unitId", "orderLineNo"],
            ("articleId", Uuid("The `id` of a stock article (see `article_list`). Not the code.")),
            ("quantity", Number("Quantity in the line's unit (`unitId`; the article's base unit when that is left out), as a JSON number (not a string): greater than 0, at most 999999999.999999, at most 6 decimal places. "
                + "On a count it is the counted quantity and 0 is allowed: none found.")),
            ("unitId", NullableUuid("The `id` of the unit of measure the quantity is in: the article's base unit or one of its alternative units (see `article_unit_list`). "
                + "Optional; left out or null it defaults to the article's base unit. Not the unit's code.")),
            ("orderLineNo", NullableInteger("Only on a receipt linked to a purchase order (`purchaseOrderId`) or an issue linked to a sales order (`salesOrderId`), where it is required on every line: the `lineNo` (1…n) of the order line this line receives or delivers. "
                + "The line's article must be that order line's article; its unit may be any unit of the article, and several lines may name the same order line. "
                + "On a document that is not linked leave it out or pass null.", 1)))),
    ];

    private static (string, System.Text.Json.Nodes.JsonObject)[] StockDocumentHeader(bool update) =>
    [
        ("documentDate", Text("The business date of the document as YYYY-MM-DD, for example 2026-10-09. Any date, past or future; it does not affect the stock check.")),
        ("warehouseId", Uuid("The `id` of the warehouse the goods come into (receipt) or leave (issue); for a transfer the source, which the goods leave; for a count the warehouse that was counted. See `warehouse_list`. Not the code.")),
        ("toWarehouseId", NullableUuid("Only for a transfer, where it is required: the `id` of the destination warehouse the goods arrive in. "
            + "It must differ from `warehouseId`, which is the source. For a receipt, an issue or a count leave it out or pass null.")),
        ("reference", NullableText("Your own reference, for example a delivery-note number; one line, at most 100 characters."
            + (update ? " Required: pass null for no value." : " Optional."))),
        ("note", NullableText("Free text, may have several lines, at most 2000 characters." + (update ? " Required: pass null for no value." : " Optional."))),
    ];

    /// <summary>The words in which the tools of one kind of order differ from those of the other (spec 010: the tools of spec 009 mirrored).</summary>
    private sealed record OrderWords(
        string Tool, string Partner, string PartnerId, string Role, string DueDate, string DueDateIs, string WarehouseIs, string ReferenceIs,
        string LinkedDocument, string QuantityIs, string PriceIs);

    private static readonly OrderWords Purchase = new(
        "purchase_order", "supplier", "supplierId", "isSupplier", "expectedDate", "When the goods are expected",
        "the goods are expected in; receipts against the order go into this warehouse", "The supplier's own number for the order, or your reference",
        "A receipt", "Ordered quantity", "The price");

    private static readonly OrderWords Sales = new(
        "sales_order", "customer", "customerId", "isCustomer", "requestedDate", "When the customer wants the goods",
        "the goods ship from; deliveries against the order leave this warehouse", "The customer's own number for the order, or your reference",
        "A delivery (an issue)", "Ordered quantity", "The selling price");

    private static string OrderReferences(OrderWords o) =>
        $"`REFERENCE_NOT_FOUND`: `{o.PartnerId}`, `warehouseId` or a line's `articleId` or `unitId` names no record of this tenant (`errors` says which; find ids with `partner_list`, `warehouse_list`, `article_list`, `uom_list`). "
        + $"`REFERENCE_INACTIVE`: that {o.Partner}, warehouse, article or unit is inactive and would be newly used. "
        + $"`PARTNER_ROLE_MISSING`: the partner named by `{o.PartnerId}` does not have `{o.Role}`; choose a {o.Partner} (`partner_list` with `{o.Role}` true) or give the partner the role with `partner_update`. "
        + "`ARTICLE_NOT_STOCKED`: a line names a service article; only articles of type `stock` can be ordered. "
        + "`UNIT_NOT_ON_ARTICLE`: a line's `unitId` is neither the base unit nor an alternative unit of its article; see `article_unit_list`, or leave `unitId` out. "
        + "`QUANTITY_NOT_CONVERTIBLE`: a line's quantity, converted to the article's base unit with the current factor, rounds to zero or exceeds 999999999.999999 (`errors` names `lines[i].quantity`). "
        + "Errors about a line are keyed by position, for example `lines[0].articleId`.";

    private static string OrderConfirmed(OrderWords o) =>
        "`INVALID_STATE`: the order is already confirmed (or closed); a confirmed order is permanent and cannot be changed, deleted or confirmed again. "
        + $"To change what was ordered, close it with `{o.Tool}_close` and create a new order.";

    private static (string, System.Text.Json.Nodes.JsonObject)[] OrderFields(OrderWords o, bool update) =>
    [
        ("orderDate", Text("The date of the order as YYYY-MM-DD, for example 2026-10-09.")),
        (o.DueDate, NullableText($"{o.DueDateIs}, as YYYY-MM-DD; not earlier than `orderDate`."
            + (update ? " Required: pass null for no value." : " Optional."))),
        (o.PartnerId, Uuid($"The `id` of the partner that is the {o.Partner} of the order; it must have `{o.Role}` = true (see `partner_list`). Not the code.")),
        ("warehouseId", Uuid($"The `id` of the warehouse {o.WarehouseIs} (see `warehouse_list`). Not the code.")),
        ("reference", NullableText($"{o.ReferenceIs}; one line, at most 100 characters."
            + (update ? " Required: pass null for no value." : " Optional."))),
        ("note", NullableText("Free text, may have several lines, at most 2000 characters." + (update ? " Required: pass null for no value." : " Optional."))),
        ("lines", ArrayOf(
            "The lines, 1 to 200; they are numbered 1…n in the order given" + (update ? " and replace all stored lines." : ".")
            + $" The same article may appear on several lines. {o.LinkedDocument} names an order line by this number (`orderLineNo`).",
            1, 200, ["unitId"],
            ("articleId", Uuid("The `id` of a stock article (see `article_list`). Not the code.")),
            ("quantity", Number("Ordered quantity in the line's unit (`unitId`; the article's base unit when that is left out), as a JSON number (not a string): greater than 0, at most 999999999.999999, at most 6 decimal places.")),
            ("unitPrice", Number($"{o.PriceIs} of one unit of the line (of one `unitId`: per box when the line is in boxes), in the tenant's currency, without tax, as a JSON number (not a string): "
                + "0 or greater (0 = free of charge), at most 999999999.999999, at most 6 decimal places. `quantity` × `unitPrice`, rounded to 2 decimals, may be at most 9999999999.99.")),
            ("unitId", NullableUuid("The `id` of the unit of measure the quantity and the price are in: the article's base unit or one of its alternative units (see `article_unit_list`). "
                + "Optional; left out or null it defaults to the article's base unit. Not the unit's code.")))),
    ];

    private static readonly string[] AddressNames = ["addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    private static readonly (string, System.Text.Json.Nodes.JsonObject)[] IdOrCode =
    [
        ("id", Uuid("The record's `id`. Give either `id` or `code`, not both.")),
        ("code", Text("The record's code, in any letter case. Give either `id` or `code`, not both.")),
    ];

    private static (string, System.Text.Json.Nodes.JsonObject) IdOf(string what) => ("id", Uuid($"The `id` of the {what}."));

    private static (string, System.Text.Json.Nodes.JsonObject) ArticleIdOf(string what) =>
        ("articleId", Uuid($"The `id` of the article {what} (see `article_list`). Not the code."));

    /// <summary>The two ids that address one conversion (spec 007, section 5).</summary>
    private static (string, System.Text.Json.Nodes.JsonObject)[] ArticleUnitAddress(string unit) =>
    [
        ArticleIdOf("the conversion belongs to"),
        ("unitId", Uuid($"The `id` of the unit of measure, {unit} (see `uom_list`). Not the code.")),
    ];

    public static IReadOnlyList<XerpTool> All { get; } =
    [
        XerpTool.For<NoArguments, WhoAmIDto>("whoami", ToolKind.Read,
            "Returns the tenant all data belongs to and the actor (API key) these calls are attributed to. Takes no arguments.",
            Input([]),
            (services, _, ct) => services.GetRequiredService<WhoAmIOperation>().ExecuteAsync(ct)),

        // ---- units of measure
        XerpTool.For<ListUnitsOfMeasureInput, PagedResult<UnitOfMeasureDto>>("uom_list", ToolKind.Read,
            "Lists the tenant's units of measure ordered by code, with paging; `total` counts all matches. " + Validation,
            Input([], [Search("the code or the name"), IsActiveFilter("units"), .. Paging]),
            (services, input, ct) => services.GetRequiredService<UnitOfMeasureOperations>().ListAsync(input, ct)),

        XerpTool.For<RecordAddressInput, UnitOfMeasureDto>("uom_get", ToolKind.Read,
            "Returns one unit of measure, addressed by `id` or by `code` (exactly one of the two). "
            + "`VALIDATION_FAILED`: both or neither were given. `NOT_FOUND`: no such unit in this tenant.",
            Input([], IdOrCode),
            (services, input, ct) => services.GetRequiredService<UnitOfMeasureOperations>().FindAsync(input, ct)),

        XerpTool.For<CreateUnitOfMeasureInput, UnitOfMeasureDto>("uom_create", ToolKind.Create,
            "Creates a unit of measure (for example `kg`, `pcs`) and returns it with its `id`. " + Validation + " " + CodeTaken,
            Input(["code", "name"], UnitFields(isActiveRequired: false)),
            (services, input, ct) => services.GetRequiredService<UnitOfMeasureOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<ReplaceUnitOfMeasureInput, UnitOfMeasureDto>("uom_update", ToolKind.Update,
            "Replaces all fields of a unit of measure; every argument is required. Set `isActive` to false to retire a unit that is in use. "
            + Validation + " `NOT_FOUND`: no such unit in this tenant. " + CodeTaken,
            Input(["id", "code", "name", "isActive"], [IdOf("unit of measure to replace"), .. UnitFields(isActiveRequired: true)]),
            (services, id, input, ct) => services.GetRequiredService<UnitOfMeasureOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, UnitOfMeasureDeleted>("uom_delete", ToolKind.Delete,
            "Deletes a unit of measure that nothing uses; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such unit in this tenant. "
            + "`IN_USE`: the unit is the base unit of articles (list them with `article_list` `baseUnitId`), an alternative unit of articles "
            + "(a conversion names it; list them with `article_list` `alternativeUnitId` and remove the conversions with `article_unit_delete`), "
            + "or the unit of a stock document line, draft or posted, or of a purchase order or sales order line, whatever the order's status. Deactivate the unit with `uom_update` instead.",
            Input(["id"], IdOf("unit of measure to delete")),
            (services, input, ct) => services.GetRequiredService<UnitOfMeasureOperations>().DeleteAsync(input.Id, ct)),

        // ---- articles
        XerpTool.For<ListArticlesInput, PagedResult<ArticleDto>>("article_list", ToolKind.Read,
            "Lists the tenant's articles ordered by code, with paging; `total` counts all matches. Filters combine with AND. " + Validation,
            Input([],
            [
                Search("the code or the name"),
                ("type", Text("Return only articles of this type.", "stock", "service")),
                ("baseUnitId", Uuid("Return only articles whose base unit is the unit of measure with this `id`.")),
                ("alternativeUnitId", Uuid("Return only articles that have a conversion for the unit of measure with this `id` (see `article_unit_list`); an article whose base unit it is does not match.")),
                IsActiveFilter("articles"),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<ArticleOperations>().ListAsync(input, ct)),

        XerpTool.For<RecordAddressInput, ArticleDto>("article_get", ToolKind.Read,
            "Returns one article with its base unit, addressed by `id` or by `code` (exactly one of the two). "
            + "`VALIDATION_FAILED`: both or neither were given. `NOT_FOUND`: no such article in this tenant.",
            Input([], IdOrCode),
            (services, input, ct) => services.GetRequiredService<ArticleOperations>().FindAsync(input, ct)),

        XerpTool.For<CreateArticleInput, ArticleDto>("article_create", ToolKind.Create,
            "Creates an article (an item or a service) and returns it with its `id`. " + Validation + " "
            + "`REFERENCE_NOT_FOUND`: `baseUnitId` names no unit of this tenant; find one with `uom_list`. "
            + "`REFERENCE_INACTIVE`: that unit is inactive; choose an active unit. " + CodeTaken,
            Input(["code", "name", "type", "baseUnitId"], ArticleFields(update: false)),
            (services, input, ct) => services.GetRequiredService<ArticleOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<ReplaceArticleInput, ArticleDto>("article_update", ToolKind.Update,
            "Replaces all fields of an article; every argument is required (`description` may be null). " + Validation + " "
            + "`NOT_FOUND`: no such article in this tenant. `REFERENCE_NOT_FOUND`: `baseUnitId` names no unit of this tenant. "
            + "`REFERENCE_INACTIVE`: the unit is inactive and the article did not already use it. "
            + "`IN_USE`: stock documents, purchase orders or sales orders use the article, so `type` and `baseUnitId` cannot change, or the article has unit conversions, "
            + "so `baseUnitId` cannot change (`errors` names which); keep the values. To change the base unit of an article no stock document uses, "
            + "delete its conversions with `article_unit_delete` first and set them again afterwards. " + CodeTaken,
            Input(["id", "code", "name", "description", "type", "baseUnitId", "isActive"],
                [IdOf("article to replace"), .. ArticleFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<ArticleOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, ArticleDeleted>("article_delete", ToolKind.Delete,
            "Deletes an article that no stock document and no order uses, together with its unit conversions; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such article in this tenant. "
            + "`IN_USE`: a stock document (draft or posted) or a purchase order or a sales order (draft, confirmed or closed) has a line with this article; deactivate it with `article_update` instead.",
            Input(["id"], IdOf("article to delete")),
            (services, input, ct) => services.GetRequiredService<ArticleOperations>().DeleteAsync(input.Id, ct)),

        // ---- article units (conversions; spec 007, section 5)
        XerpTool.For<ListArticleUnitsArguments, PagedResult<ArticleUnitDto>>("article_unit_list", ToolKind.Read,
            "Lists the unit conversions of one article - the alternative units it is also handled in, each with its `factor` to the article's base unit "
            + "(1 `unit` = `factor` × `baseUnit`) - ordered by unit code, with paging. The base unit itself is not listed: it is always a unit of the article, with factor 1. "
            + Validation + " `NOT_FOUND`: no such article in this tenant.",
            Input(["articleId"], [ArticleIdOf("whose conversions to list"), .. Paging]),
            (services, input, ct) => services.GetRequiredService<ArticleUnitOperations>().ListAsync(input, ct)),

        XerpTool.For<ArticleUnitAddressInput, ArticleUnitDto>("article_unit_get", ToolKind.Read,
            "Returns one unit conversion of an article: 1 `unit` = `factor` × `baseUnit`. `VALIDATION_FAILED`: `articleId` or `unitId` is missing. "
            + "`NOT_FOUND`: no such article in this tenant, or the article has no conversion for this unit (its base unit has none: its factor is always 1).",
            Input(["articleId", "unitId"], ArticleUnitAddress("the alternative unit")),
            (services, input, ct) => services.GetRequiredService<ArticleUnitOperations>().GetAsync(input, ct)),

        XerpTool.For<SetArticleUnitArguments, ArticleUnitDto>("article_unit_set", ToolKind.Update,
            "Says that an article is also handled in another unit: one `unitId` of this article equals `factor` of the article's base unit. "
            + "Example: 1 box = 12 pcs of an article whose base unit is pcs -> `unitId` = the id of box, `factor` = 12 (not 1/12). "
            + "The call creates the conversion or, if the article already has one for this unit, replaces its factor; repeating it is harmless. "
            + "A changed factor affects drafts and future stock documents only: posted documents, the stock ledger and stock on hand keep what they were posted with. "
            + "A ratio that is not an exact decimal (1/12 = 0.083333…) is stored rounded; avoid it by making the smaller unit the article's base unit, so the factor is a whole number. "
            + "A conversion belongs to this article only: the same unit may have another factor on another article. "
            + Validation + " `NOT_FOUND`: no such article in this tenant. "
            + "`REFERENCE_NOT_FOUND`: `unitId` names no unit of measure of this tenant; find one with `uom_list` or create it with `uom_create`. "
            + "`REFERENCE_INACTIVE`: the unit is inactive and the article has no conversion for it yet. "
            + "`UNIT_IS_BASE_UNIT`: `unitId` is the article's own base unit, which needs no conversion (its factor is always 1).",
            Input(["articleId", "unitId", "factor"],
            [
                .. ArticleUnitAddress("the alternative unit: the unit that is `factor` base units of the article"),
                ("factor", Number("How many base units of the article one of `unitId` is, as a JSON number (not a string): greater than 0, at most 999999.999999, at most 6 decimal places.")),
            ]),
            async (services, input, ct) =>
            {
                var result = await services.GetRequiredService<ArticleUnitOperations>().SetAsync(input, ct);
                return result.IsSuccess ? result.Value.ArticleUnit : result.Error;
            }),

        XerpTool.For<ArticleUnitAddressInput, ArticleUnitDeleted>("article_unit_delete", ToolKind.Delete,
            "Deletes a unit conversion of an article; returns `{ \"deleted\": true }`. New lines can then no longer be entered in that unit for this article. "
            + "Posted stock documents are unaffected: their lines keep the factor they were posted with. `VALIDATION_FAILED`: `articleId` or `unitId` is missing. "
            + "`NOT_FOUND`: no such article in this tenant, or it has no conversion for this unit. "
            + "`IN_USE`: a line of a draft stock document, of a draft purchase order or of a draft sales order is in this unit of this article; post, confirm or delete the draft, "
            + "or change the line's unit with `stock_document_update`, `purchase_order_update` or `sales_order_update`, then delete again. Lines of confirmed and closed orders keep the factor they were confirmed with and do not block it.",
            Input(["articleId", "unitId"], ArticleUnitAddress("the alternative unit")),
            (services, input, ct) => services.GetRequiredService<ArticleUnitOperations>().DeleteAsync(input, ct)),

        // ---- partners
        XerpTool.For<ListPartnersInput, PagedResult<PartnerDto>>("partner_list", ToolKind.Read,
            "Lists the tenant's partners (customers and suppliers) ordered by code, with paging; `total` counts all matches. "
            + "Filters combine with AND; a partner that is both customer and supplier matches either role filter. " + Validation,
            Input([],
            [
                Search("the code, the name or the tax id"),
                ("isCustomer", Flag("Return only partners that are (true) or are not (false) customers; omit for both.")),
                ("isSupplier", Flag("Return only partners that are (true) or are not (false) suppliers; omit for both.")),
                IsActiveFilter("partners"),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<PartnerOperations>().ListAsync(input, ct)),

        XerpTool.For<RecordAddressInput, PartnerDto>("partner_get", ToolKind.Read,
            "Returns one partner, addressed by `id` or by `code` (exactly one of the two). "
            + "`VALIDATION_FAILED`: both or neither were given. `NOT_FOUND`: no such partner in this tenant.",
            Input([], IdOrCode),
            (services, input, ct) => services.GetRequiredService<PartnerOperations>().FindAsync(input, ct)),

        XerpTool.For<PartnerInput, PartnerDto>("partner_create", ToolKind.Create,
            "Creates a partner (a company or person the tenant sells to, buys from, or both) and returns it with its `id`. "
            + "At least one of `isCustomer`, `isSupplier` must be true. `taxId` is not unique: two partners may have the same one, "
            + "so search with `partner_list` first (by name or tax id) to avoid creating a duplicate partner. "
            + Validation + " " + CodeTaken,
            Input(["code", "name"], PartnerFields(update: false)),
            (services, input, ct) => services.GetRequiredService<PartnerOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<PartnerInput, PartnerDto>("partner_update", ToolKind.Update,
            "Replaces all fields of a partner; every argument is required (`taxId` and the address arguments may be null, which clears them). "
            + "At least one of `isCustomer`, `isSupplier` must stay true. Set `isActive` to false to retire a partner. "
            + Validation + " `NOT_FOUND`: no such partner in this tenant. " + CodeTaken,
            Input(["id", "code", "name", "isCustomer", "isSupplier", "taxId", .. AddressNames, "isActive"],
                [IdOf("partner to replace"), .. PartnerFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<PartnerOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, PartnerDeleted>("partner_delete", ToolKind.Delete,
            "Deletes a partner that no order names; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such partner in this tenant. "
            + "`IN_USE`: a purchase order names this partner as its supplier, or a sales order names it as its customer (draft, confirmed or closed); deactivate it with `partner_update` instead.",
            Input(["id"], IdOf("partner to delete")),
            (services, input, ct) => services.GetRequiredService<PartnerOperations>().DeleteAsync(input.Id, ct)),

        // ---- warehouses
        XerpTool.For<ListWarehousesInput, PagedResult<WarehouseDto>>("warehouse_list", ToolKind.Read,
            "Lists the tenant's warehouses ordered by code, with paging; `total` counts all matches. " + Validation,
            Input([], [Search("the code or the name"), IsActiveFilter("warehouses"), .. Paging]),
            (services, input, ct) => services.GetRequiredService<WarehouseOperations>().ListAsync(input, ct)),

        XerpTool.For<RecordAddressInput, WarehouseDto>("warehouse_get", ToolKind.Read,
            "Returns one warehouse, addressed by `id` or by `code` (exactly one of the two). "
            + "`VALIDATION_FAILED`: both or neither were given. `NOT_FOUND`: no such warehouse in this tenant.",
            Input([], IdOrCode),
            (services, input, ct) => services.GetRequiredService<WarehouseOperations>().FindAsync(input, ct)),

        XerpTool.For<WarehouseInput, WarehouseDto>("warehouse_create", ToolKind.Create,
            "Creates a warehouse (a place where stock is kept) and returns it with its `id`. " + Validation + " " + CodeTaken,
            Input(["code", "name"], WarehouseFields(update: false)),
            (services, input, ct) => services.GetRequiredService<WarehouseOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<WarehouseInput, WarehouseDto>("warehouse_update", ToolKind.Update,
            "Replaces all fields of a warehouse; every argument is required (the address arguments may be null, which clears them). "
            + "Set `isActive` to false to retire a warehouse. " + Validation + " `NOT_FOUND`: no such warehouse in this tenant. " + CodeTaken,
            Input(["id", "code", "name", .. AddressNames, "isActive"], [IdOf("warehouse to replace"), .. WarehouseFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<WarehouseOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, WarehouseDeleted>("warehouse_delete", ToolKind.Delete,
            "Deletes a warehouse that no stock document and no order uses; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such warehouse in this tenant. "
            + "`IN_USE`: a stock document (draft or posted) or a purchase order or a sales order (draft, confirmed or closed) names this warehouse; deactivate it with `warehouse_update` instead.",
            Input(["id"], IdOf("warehouse to delete")),
            (services, input, ct) => services.GetRequiredService<WarehouseOperations>().DeleteAsync(input.Id, ct)),

        // ---- stock documents, stock on hand, stock ledger
        XerpTool.For<ListStockDocumentsInput, PagedResult<StockDocumentSummaryDto>>("stock_document_list", ToolKind.Read,
            "Lists the tenant's stock documents (receipts, issues, transfers and counts), newest first, with paging; each item has `lineCount` instead of the lines. "
            + "A reversed document has status `reversed` and `reversedBy`; the document that reversed it has status `posted` and `reversalOf`. "
            + "Filters combine with AND. " + Validation,
            Input([],
            [
                ("type", Text("Return only documents of this type.", "receipt", "issue", "transfer", "count")),
                ("status", Text("Return only documents in this status. `posted` includes reversing documents; `reversed` are the originals that were reversed.", "draft", "posted", "reversed")),
                ("warehouseId", Uuid("Return only documents of the warehouse with this `id`: as the warehouse of the document or as the destination of a transfer.")),
                ("purchaseOrderId", Uuid("Return only the documents linked to the purchase order with this `id`: its receipts, drafts and posted, and their reversals.")),
                ("salesOrderId", Uuid("Return only the documents linked to the sales order with this `id`: its deliveries (issues), drafts and posted, and their reversals.")),
                Search("the document number or the reference"),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().ListAsync(input, ct)),

        XerpTool.For<StockDocumentAddressInput, StockDocumentDto>("stock_document_get", ToolKind.Read,
            "Returns one stock document with its lines, addressed by `id` or by `number` (exactly one of the two). "
            + "`VALIDATION_FAILED`: both or neither were given. `NOT_FOUND`: no such document in this tenant; a draft has no number yet.",
            Input([],
            [
                ("id", Uuid("The document's `id`. Give either `id` or `number`, not both.")),
                ("number", Text("The number of a posted document, for example `SR-000001`, in any letter case. Give either `id` or `number`, not both.")),
            ]),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().FindAsync(input, ct)),

        XerpTool.For<CreateStockDocumentInput, StockDocumentDto>("stock_document_create", ToolKind.Create,
            "Creates a stock document as a draft and returns it with its `id`. A `receipt` brings goods into the warehouse, an `issue` takes them out, "
            + "a `transfer` moves them from `warehouseId` (source) to `toWarehouseId` (destination) in one posting. "
            + "A `count` makes stock equal to what was counted in `warehouseId`: a line's `quantity` is what was counted of the article, in the line's unit, and `0` means none found; "
            + "each article appears once; articles not listed are not changed. The draft shows per line `bookQuantity` (stock on hand when the draft was saved) and "
            + "`differenceQuantity` (counted minus book, in base units), and posting writes exactly that difference. Use a count for opening balances (a count on empty stock) "
            + "and when you know what is there but not the difference; for a known difference use a receipt or an issue. "
            + "A line's `unitId` defaults to the article's base unit; stock is always kept in base units. "
            + "A draft changes no stock and reserves nothing; it takes effect only when posted with `stock_document_post`. "
            + "To receive goods of a purchase order, create a `receipt` with `purchaseOrderId`, the order's warehouse and `orderLineNo` on every line; "
            + "to deliver goods of a sales order, create an `issue` with `salesOrderId`, the order's warehouse and `orderLineNo` on every line. "
            + "The draft reserves nothing of the order either - quantities are compared with it when the document is posted. A document has at most one of the two links. "
            + Validation + " " + StockReferences + " " + OrderLink,
            Input(["type", "documentDate", "warehouseId", "lines"],
            [
                ("type", Text("`receipt`: goods come into the warehouse. `issue`: goods leave it. `transfer`: goods move from `warehouseId` to `toWarehouseId`. `count`: stock in `warehouseId` is set to the counted quantities. Cannot be changed later.", "receipt", "issue", "transfer", "count")),
                .. StockDocumentHeader(update: false),
                ("purchaseOrderId", NullableUuid("Only for a receipt: the `id` of the confirmed purchase order whose goods it receives (see `purchase_order_list`); not the order's number. "
                    + "Every line then needs `orderLineNo`. The link cannot be changed later. Left out or null: a document that is not linked to an order.")),
                ("salesOrderId", NullableUuid("Only for an issue: the `id` of the confirmed sales order whose goods it delivers (see `sales_order_list`); not the order's number. "
                    + "Every line then needs `orderLineNo`, and `warehouseId` must be the order's warehouse. The link cannot be changed later. Left out or null: a document that is not linked to an order.")),
                StockDocumentFields(update: false)[^1],
            ]),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<ReplaceStockDocumentInput, StockDocumentDto>("stock_document_update", ToolKind.Update,
            "Replaces the date, warehouse, reference, note and all lines of a draft; every argument is required (`reference` and `note` may be null), "
            + "except `toWarehouseId`, which is required for a transfer only (the destination; `warehouseId` is the source). "
            + "The type and the link to a purchase order or a sales order cannot change; on a linked document every line needs `orderLineNo`. Saving a count takes the current stock on hand as `bookQuantity` of every line anew. A line's `unitId` defaults to the article's base unit; stock is always kept in base units. "
            + Validation + " `NOT_FOUND`: no such document in this tenant. " + DocumentPosted + " " + StockReferences + " " + OrderLink,
            Input(["id", "documentDate", "warehouseId", "reference", "note", "lines"],
                [IdOf("draft stock document to replace"), .. StockDocumentFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<StockDocumentOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, StockDocumentDeleted>("stock_document_delete", ToolKind.Delete,
            "Deletes a draft stock document with its lines; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such document in this tenant. " + DocumentPosted,
            Input(["id"], IdOf("draft stock document to delete")),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().DeleteAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, StockDocumentDto>("stock_document_post", ToolKind.Post,
            "Posts a draft stock document. Posting is permanent: the document gets its number (`SR-…` receipt, `SI-…` issue, `ST-…` transfer, `SC-…` count), can no longer be changed or deleted, "
            + "and stock on hand changes through ledger entries - a receipt adds its quantities to the warehouse, an issue subtracts them, "
            + "a transfer subtracts them from the source and adds them to the destination together, so total stock does not change, "
            + "a count writes the `differenceQuantity` of every line (nothing for a line without a difference), so stock of every counted article equals what was counted. "
            + "It either does all of this or nothing. `NOT_FOUND`: no such document in this tenant. " + DocumentPosted + " "
            + "`REFERENCE_INACTIVE`: a warehouse or an article of the document is inactive (`errors` says which); reactivate it or change the draft. "
            + "Lines in another unit are converted to the article's base unit with the factor as it is at this moment; the posted line keeps that `factor` and `baseQuantity` for good. "
            + "`QUANTITY_NOT_CONVERTIBLE`: with the current factor a line converts to zero or to more than 999999999.999999 (`errors` names `lines[i].quantity`); correct the draft or the factor. "
            + "`INSUFFICIENT_STOCK`: an issue or a transfer would take more than is on hand in its (source) warehouse, counted in base units; `errors` names the short lines (`lines[0].quantity`). "
            + "Nothing was posted and the draft is unchanged: check `stock_on_hand_list`, then correct the draft with `stock_document_update` "
            + "or receive stock first, and post again. A count never returns it. "
            + "`COUNT_OUTDATED`: stock of a counted article changed since the count was saved, so it no longer equals the line's `bookQuantity` (`errors` names those lines, `lines[0].quantity`). "
            + "Nothing was posted: read the document with `stock_document_get`, check the count, save it again with `stock_document_update` "
            + "(this takes the current book quantity) and post again. "
            + "An unlinked issue or a transfer is judged against stock on hand only: it can take stock that is reserved for sales orders (`reservedQuantity`), and nothing warns about it. "
            + "A receipt linked to a purchase order raises the received quantity, and an issue linked to a sales order (a delivery) the delivered quantity, of the order lines it names. "
            + "A linked document is checked against its order before stock: "
            + "`ORDER_NOT_OPEN`: the order is closed (or a draft), `errors` names `purchaseOrderId` or `salesOrderId`; reopen it with `purchase_order_reopen` / `sales_order_reopen` or delete the draft document. "
            + "`QUANTITY_EXCEEDS_ORDER`: the document would take an order line above what was ordered, counted in base units over all its lines naming that order line "
            + "(`errors` names those lines, `lines[0].quantity`). Nothing was posted: read the order with `purchase_order_get` or `sales_order_get` - `outstandingBaseQuantity` of a line is what can still be received or delivered - "
            + "lower the quantities with `stock_document_update` and post again. "
            + "A delivery can fail with `INSUFFICIENT_STOCK` although its sales order is confirmed: confirming reserves nothing physically, and the goods must be on hand in the order's warehouse when the delivery is posted.",
            Input(["id"], IdOf("draft stock document to post")),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().PostAsync(input.Id, ct)),

        XerpTool.WithId<ReverseStockDocumentInput, StockDocumentDto>("stock_document_reverse", ToolKind.Reverse,
            "Reverses a posted stock document (receipt, issue, transfer or count) and returns the new reversing document. This is the only way to correct a posted document. "
            + "A reversal is permanent and cannot itself be reversed. It cancels the whole document: the reversing document has the same type, warehouses and lines, "
            + "its own number from the same series, and ledger entries with the opposite sign, so stock is as if the original had never been posted; "
            + "the original gets status `reversed`. To correct a mistake, reverse and then create a new document with `stock_document_create`. "
            + "It either does all of this or nothing. Inactive warehouses or articles do not prevent a reversal. "
            + "Reversing a receipt linked to a purchase order or a delivery linked to a sales order gives its quantities back to the order lines, whatever the order's status, and does not change that status; "
            + "a reversed delivery brings the goods back into stock, and while the order is confirmed they are reserved again. "
            + Validation + " `documentDate` earlier than the original's is one of them. `NOT_FOUND`: no such document in this tenant. "
            + "`INVALID_STATE`: the document is a draft, is already reversed, or is itself a reversing document. "
            + "`INSUFFICIENT_STOCK`: the goods the original brought in have already left, so the reversal would make stock negative; `errors` names the original's lines (`lines[0].quantity`). "
            + "For a count this concerns lines that added stock (a surplus); reversing a count undoes its ledger entries and restores neither the counted nor the book quantity. "
            + "Nothing was reversed: reverse the later documents that took the goods out first (see `stock_ledger_entry_list`), then reverse this one again.",
            Input(["id", "documentDate"],
            [
                IdOf("posted stock document to reverse"),
                ("documentDate", Text("The business date of the reversal as YYYY-MM-DD; not earlier than the `documentDate` of the document being reversed.")),
                ("note", NullableText("Why the document is reversed; free text, may have several lines, at most 2000 characters. Optional.")),
            ]),
            (services, id, input, ct) => services.GetRequiredService<StockDocumentOperations>().ReverseAsync(id, input, ct)),

        XerpTool.For<ListStockOnHandInput, PagedResult<StockOnHandDto>>("stock_on_hand_list", ToolKind.Read,
            "Lists stock on hand: one item per article and warehouse whose `quantity`, `incomingQuantity` or `reservedQuantity` is not zero, with four quantities in the article's base unit, "
            + "ordered by article code then warehouse code. `quantity` is what is in the warehouse now; `incomingQuantity` is what confirmed purchase orders for that warehouse "
            + "still expect (ordered minus received) - it is not stock and cannot be issued; `reservedQuantity` is what confirmed sales orders shipping from that warehouse still owe "
            + "(ordered minus delivered); `availableQuantity` is `quantity` minus `reservedQuantity` and is negative when more is promised than is there - `incomingQuantity` is not part of it. "
            + "A reservation informs and blocks nothing: any issue or transfer may still take the stock. A pair that is not listed has none of them. Drafts and closed orders do not count. " + Validation,
            Input([],
            [
                ("articleId", Uuid("Return only the stock of the article with this `id`.")),
                ("warehouseId", Uuid("Return only the stock in the warehouse with this `id`.")),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<StockQueries>().OnHandAsync(input, ct)),

        XerpTool.For<ListStockLedgerEntriesInput, PagedResult<StockLedgerEntryDto>>("stock_ledger_entry_list", ToolKind.Read,
            "Lists stock ledger entries, oldest first: the permanent history of every posted movement (positive quantity in, negative out). "
            + "Stock on hand is the sum of these entries; entries are never changed or deleted. A transfer has two entries per line, out of the source and into the destination. "
            + "A reversal adds entries with the opposite sign, marked `document.isReversal`. Filters combine with AND. " + Validation,
            Input([],
            [
                ("articleId", Uuid("Return only entries of the article with this `id`.")),
                ("warehouseId", Uuid("Return only entries of the warehouse with this `id`.")),
                ("documentId", Uuid("Return only the entries the stock document with this `id` produced.")),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<StockQueries>().LedgerAsync(input, ct)),

        // ---- purchase orders (spec 009, section 5)
        XerpTool.For<ListPurchaseOrdersInput, PagedResult<PurchaseOrderSummaryDto>>("purchase_order_list", ToolKind.Read,
            "Lists the tenant's purchase orders, newest first, with paging; each item has `lineCount` instead of the lines, with `totalAmount` and `receiptStatus`. "
            + "Filters combine with AND. " + Validation,
            Input([],
            [
                ("status", Text("Return only orders in this status. `draft`: not yet ordered. `confirmed`: ordered, can be received against. `closed`: nothing more will come.", "draft", "confirmed", "closed")),
                ("receiptStatus", Text("Return only orders of which nothing (`none`), a part (`partial`) or everything (`full`) was received. A draft is `none`.", "none", "partial", "full")),
                ("supplierId", Uuid("Return only orders for the supplier (partner) with this `id`.")),
                ("warehouseId", Uuid("Return only orders whose goods are expected in the warehouse with this `id`.")),
                Search("the order number or the reference"),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().ListAsync(input, ct)),

        XerpTool.For<OrderAddressInput, PurchaseOrderDto>("purchase_order_get", ToolKind.Read,
            "Returns one purchase order with its lines, addressed by `id` or by `number` (exactly one of the two). Every line shows what was ordered (`unit`, `quantity`, `unitPrice`, `lineAmount`), "
            + "what that is in the article's base unit (`factor`, `baseQuantity`), and the progress: `receivedBaseQuantity` and `outstandingBaseQuantity`, both in base units. "
            + "How to receive goods of a confirmed order: create a stock document of type `receipt` with `stock_document_create`, giving `purchaseOrderId` (this order's `id`), "
            + "the order's warehouse as `warehouseId`, and one line per delivery position with `articleId`, `quantity` and `orderLineNo` (the `lineNo` of the order line); then post it with `stock_document_post`. "
            + "`outstandingBaseQuantity` is what can still be received; it is 0 on a draft and on a closed order. "
            + "`VALIDATION_FAILED`: both or neither of `id` and `number` were given. `NOT_FOUND`: no such order in this tenant; a draft has no number yet.",
            Input([],
            [
                ("id", Uuid("The order's `id`. Give either `id` or `number`, not both.")),
                ("number", Text("The number of a confirmed order, for example `PO-000001`, in any letter case. Give either `id` or `number`, not both.")),
            ]),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().FindAsync(input, ct)),

        XerpTool.For<CreatePurchaseOrderInput, PurchaseOrderDto>("purchase_order_create", ToolKind.Create,
            "Creates a purchase order as a draft and returns it with its `id`: goods ordered from one supplier into one warehouse. "
            + "A line's `unitPrice` is the price of one unit of the line (per box when the line is in boxes), in the tenant's currency, without tax; `lineAmount` is `quantity` × `unitPrice` rounded to 2 decimals. "
            + "Only stock articles can be ordered. The supplier must be a partner with `isSupplier` = true. "
            + "A draft orders nothing: it has no number, no incoming quantity and cannot be received against until confirmed with `purchase_order_confirm`. "
            + Validation + " " + OrderReferences(Purchase),
            Input(["orderDate", "supplierId", "warehouseId", "lines"], OrderFields(Purchase, update: false)),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<ReplacePurchaseOrderInput, PurchaseOrderDto>("purchase_order_update", ToolKind.Update,
            "Replaces the dates, supplier, warehouse, reference, note and all lines of a draft purchase order; every argument is required (`expectedDate`, `reference` and `note` may be null). "
            + Validation + " `NOT_FOUND`: no such order in this tenant. " + OrderConfirmed(Purchase) + " " + OrderReferences(Purchase),
            Input(["id", "orderDate", "expectedDate", "supplierId", "warehouseId", "reference", "note", "lines"],
                [IdOf("draft purchase order to replace"), .. OrderFields(Purchase, update: true)]),
            (services, id, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, OrderDeleted>("purchase_order_delete", ToolKind.Delete,
            "Deletes a draft purchase order with its lines; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such order in this tenant. " + OrderConfirmed(Purchase),
            Input(["id"], IdOf("draft purchase order to delete")),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().DeleteAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, PurchaseOrderDto>("purchase_order_confirm", ToolKind.Post,
            "Confirms a draft purchase order. Confirmation is permanent: the order gets its number (`PO-…`) and can no longer be edited or deleted; "
            + "every line keeps the `factor` and `baseQuantity` of this moment for good, and the order's outstanding quantities count as `incomingQuantity` in `stock_on_hand_list`. "
            + "To change a confirmed order, close it with `purchase_order_close` and create a new one. It either does all of this or nothing. "
            + "`NOT_FOUND`: no such order in this tenant. " + OrderConfirmed(Purchase) + " "
            + "`REFERENCE_INACTIVE`: the supplier, the warehouse or an article of the order is inactive (`errors` says which: `supplierId`, `warehouseId`, `lines[0].articleId`); reactivate it or change the draft. "
            + "`PARTNER_ROLE_MISSING`: the supplier no longer has `isSupplier` (`errors` names `supplierId`). "
            + "`QUANTITY_NOT_CONVERTIBLE`: with the current factor a line converts to zero or to more than 999999999.999999 (`errors` names `lines[i].quantity`); correct the draft or the factor.",
            Input(["id"], IdOf("draft purchase order to confirm")),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().ConfirmAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, PurchaseOrderDto>("purchase_order_close", ToolKind.Create,
            "Closes a confirmed purchase order: nothing more will be received against it, and its outstanding quantities become 0 and stop counting as incoming. "
            + "Use it when the rest will not be delivered, or to cancel an order nothing was received against. What was received stays received. "
            + "Draft receipts against a closed order cannot be posted; delete them, or reopen the order with `purchase_order_reopen`. "
            + "`NOT_FOUND`: no such order in this tenant. `INVALID_STATE`: the order is a draft (delete it instead) or already closed.",
            Input(["id"], IdOf("confirmed purchase order to close")),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().CloseAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, PurchaseOrderDto>("purchase_order_reopen", ToolKind.Create,
            "Reopens a closed purchase order: it is `confirmed` again, with the same number, lines and received quantities, and what was not received is outstanding again. "
            + "`NOT_FOUND`: no such order in this tenant. `INVALID_STATE`: the order is not closed (a draft, or already confirmed).",
            Input(["id"], IdOf("closed purchase order to reopen")),
            (services, input, ct) => services.GetRequiredService<PurchaseOrderOperations>().ReopenAsync(input.Id, ct)),

        // ---- sales orders (spec 010, section 5): the purchase order tools mirrored
        XerpTool.For<ListSalesOrdersInput, PagedResult<SalesOrderSummaryDto>>("sales_order_list", ToolKind.Read,
            "Lists the tenant's sales orders, newest first, with paging; each item has `lineCount` instead of the lines, with `totalAmount` and `deliveryStatus`. "
            + "Filters combine with AND. " + Validation,
            Input([],
            [
                ("status", Text("Return only orders in this status. `draft`: an offer, not yet accepted. `confirmed`: accepted, can be delivered. `closed`: nothing more will be delivered.", "draft", "confirmed", "closed")),
                ("deliveryStatus", Text("Return only orders of which nothing (`none`), a part (`partial`) or everything (`full`) was delivered. A draft is `none`.", "none", "partial", "full")),
                ("customerId", Uuid("Return only orders of the customer (partner) with this `id`.")),
                ("warehouseId", Uuid("Return only orders shipping from the warehouse with this `id`.")),
                Search("the order number or the reference"),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().ListAsync(input, ct)),

        XerpTool.For<OrderAddressInput, SalesOrderDto>("sales_order_get", ToolKind.Read,
            "Returns one sales order with its lines, addressed by `id` or by `number` (exactly one of the two). Every line shows what was ordered (`unit`, `quantity`, `unitPrice`, `lineAmount`), "
            + "what that is in the article's base unit (`factor`, `baseQuantity`), and the progress: `deliveredBaseQuantity` and `outstandingBaseQuantity`, both in base units. "
            + "How to deliver goods of a confirmed order: create a stock document of type `issue` with `stock_document_create`, giving `salesOrderId` (this order's `id`), "
            + "the order's warehouse as `warehouseId`, and one line per delivery position with `articleId`, `quantity` and `orderLineNo` (the `lineNo` of the order line); then post it with `stock_document_post`. "
            + "An order can be delivered in several documents; each must be covered by stock on hand in the order's warehouse. "
            + "`outstandingBaseQuantity` is what can still be delivered, and what the order reserves; it is 0 on a draft and on a closed order. "
            + "`VALIDATION_FAILED`: both or neither of `id` and `number` were given. `NOT_FOUND`: no such order in this tenant; a draft has no number yet.",
            Input([],
            [
                ("id", Uuid("The order's `id`. Give either `id` or `number`, not both.")),
                ("number", Text("The number of a confirmed order, for example `SO-000001`, in any letter case. Give either `id` or `number`, not both.")),
            ]),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().FindAsync(input, ct)),

        XerpTool.For<CreateSalesOrderInput, SalesOrderDto>("sales_order_create", ToolKind.Create,
            "Creates a sales order as a draft and returns it with its `id`: goods one customer orders, shipped from one warehouse. "
            + "A line's `unitPrice` is the selling price of one unit of the line (per box when the line is in boxes), in the tenant's currency, without tax; `lineAmount` is `quantity` × `unitPrice` rounded to 2 decimals. "
            + "Only stock articles can be ordered. The customer must be a partner with `isCustomer` = true. "
            + "A draft is an offer: it has prices and a total, no number, reserves nothing and cannot be delivered until confirmed with `sales_order_confirm`. "
            + Validation + " " + OrderReferences(Sales),
            Input(["orderDate", "customerId", "warehouseId", "lines"], OrderFields(Sales, update: false)),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<ReplaceSalesOrderInput, SalesOrderDto>("sales_order_update", ToolKind.Update,
            "Replaces the dates, customer, warehouse, reference, note and all lines of a draft sales order; every argument is required (`requestedDate`, `reference` and `note` may be null). "
            + Validation + " `NOT_FOUND`: no such order in this tenant. " + OrderConfirmed(Sales) + " " + OrderReferences(Sales),
            Input(["id", "orderDate", "requestedDate", "customerId", "warehouseId", "reference", "note", "lines"],
                [IdOf("draft sales order to replace"), .. OrderFields(Sales, update: true)]),
            (services, id, input, ct) => services.GetRequiredService<SalesOrderOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, OrderDeleted>("sales_order_delete", ToolKind.Delete,
            "Deletes a draft sales order with its lines; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such order in this tenant. " + OrderConfirmed(Sales),
            Input(["id"], IdOf("draft sales order to delete")),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().DeleteAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, SalesOrderDto>("sales_order_confirm", ToolKind.Post,
            "Confirms a draft sales order: the offer is accepted. Confirmation is permanent: the order gets its number (`SO-…`) and can no longer be edited or deleted; "
            + "every line keeps the `factor` and `baseQuantity` of this moment for good, and from then on the order's outstanding quantities are reserved in the order's warehouse "
            + "(`reservedQuantity` in `stock_on_hand_list`). Confirmation is not refused when stock is short, and it takes nothing out of stock: "
            + "check `availableQuantity` with `stock_on_hand_list` before or after - a negative value means more is promised than is there. "
            + "To change a confirmed order, close it with `sales_order_close` and create a new one. It either does all of this or nothing. "
            + "`NOT_FOUND`: no such order in this tenant. " + OrderConfirmed(Sales) + " "
            + "`REFERENCE_INACTIVE`: the customer, the warehouse or an article of the order is inactive (`errors` says which: `customerId`, `warehouseId`, `lines[0].articleId`); reactivate it or change the draft. "
            + "`PARTNER_ROLE_MISSING`: the customer no longer has `isCustomer` (`errors` names `customerId`). "
            + "`QUANTITY_NOT_CONVERTIBLE`: with the current factor a line converts to zero or to more than 999999999.999999 (`errors` names `lines[i].quantity`); correct the draft or the factor.",
            Input(["id"], IdOf("draft sales order to confirm")),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().ConfirmAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, SalesOrderDto>("sales_order_close", ToolKind.Create,
            "Closes a confirmed sales order: nothing more will be delivered against it, and its outstanding quantities become 0 and are no longer reserved. "
            + "Use it when the rest will not be delivered, or to cancel an order nothing was delivered against. What was delivered stays delivered. "
            + "Draft deliveries against a closed order cannot be posted; delete them, or reopen the order with `sales_order_reopen`. "
            + "`NOT_FOUND`: no such order in this tenant. `INVALID_STATE`: the order is a draft (delete it instead) or already closed.",
            Input(["id"], IdOf("confirmed sales order to close")),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().CloseAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, SalesOrderDto>("sales_order_reopen", ToolKind.Create,
            "Reopens a closed sales order: it is `confirmed` again, with the same number, lines and delivered quantities, and what was not delivered is outstanding and reserved again. "
            + "`NOT_FOUND`: no such order in this tenant. `INVALID_STATE`: the order is not closed (a draft, or already confirmed).",
            Input(["id"], IdOf("closed sales order to reopen")),
            (services, input, ct) => services.GetRequiredService<SalesOrderOperations>().ReopenAsync(input.Id, ct)),

        // ---- API keys (creating a key is HTTP only: a secret never travels through a tool result, ADR-0010)
        XerpTool.For<ListApiKeysInput, PagedResult<ApiKeyDto>>("api_key_list", ToolKind.Read,
            "Lists the tenant's API keys (the actors that can act for it) in order of creation, with paging. "
            + "Never returns a key's secret. " + Validation,
            Input([],
            [
                Search("the name"),
                ("actorType", Text("Return only keys of this kind of actor.", "human", "agent")),
                IsActiveFilter("keys (inactive = revoked)"),
                .. Paging,
            ]),
            (services, input, ct) => services.GetRequiredService<ApiKeyOperations>().ListAsync(input, ct)),

        XerpTool.For<RecordIdInput, ApiKeyDto>("api_key_get", ToolKind.Read,
            "Returns one API key (name, actor type, state, who created and revoked it), never its secret. "
            + "`NOT_FOUND`: no such key in this tenant.",
            Input(["id"], IdOf("API key")),
            (services, input, ct) => services.GetRequiredService<ApiKeyOperations>().GetAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, ApiKeyDto>("api_key_revoke", ToolKind.Update,
            "Permanently revokes another API key of the tenant: it stops working at once and cannot be reactivated. "
            + "Revoking an already revoked key changes nothing. `NOT_FOUND`: no such key in this tenant. "
            + "`CANNOT_REVOKE_SELF`: this is the key making the call; a key can only be revoked by another key.",
            Input(["id"], IdOf("API key to revoke")),
            (services, input, ct) => services.GetRequiredService<ApiKeyOperations>().RevokeAsync(input.Id, ct)),
    ];
}
