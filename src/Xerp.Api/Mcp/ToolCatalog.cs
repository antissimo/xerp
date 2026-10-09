using Xerp.Application.ApiKeys;
using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Application.Identity;
using Xerp.Application.Partners;
using Xerp.Application.Stock;
using Xerp.Application.UnitsOfMeasure;
using Xerp.Application.Warehouses;
using static Xerp.Api.Mcp.ToolSchemas;

namespace Xerp.Api.Mcp;

/// <summary>A tool that takes no arguments binds to this: any argument is then an unknown one.</summary>
public sealed record NoArguments;

/// <summary>
/// The complete list of MCP tools (spec 003, 5.3; spec 004, 5.1; spec 005, 5). Adding an operation means adding its tool here; the
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
        "`REFERENCE_NOT_FOUND`: `warehouseId`, `toWarehouseId` or a line's `articleId` names no record of this tenant (`errors` says which; find ids with `warehouse_list`, `article_list`). "
        + "`REFERENCE_INACTIVE`: that warehouse or article is inactive and would be newly used. "
        + "`ARTICLE_NOT_STOCKED`: a line names a service article; only articles of type `stock` have stock. "
        + "Errors about a line are keyed by position, for example `lines[0].articleId`.";

    private const string DocumentPosted = "`INVALID_STATE`: the document is already posted (or reversed); a posted document is permanent and cannot be changed, deleted or posted again. "
        + "To correct one, use `stock_document_reverse` and create a new document.";

    private static (string, System.Text.Json.Nodes.JsonObject)[] StockDocumentFields(bool update) =>
    [
        ("documentDate", Text("The business date of the document as YYYY-MM-DD, for example 2026-10-09. Any date, past or future; it does not affect the stock check.")),
        ("warehouseId", Uuid("The `id` of the warehouse the goods come into (receipt) or leave (issue); for a transfer the source, which the goods leave. See `warehouse_list`. Not the code.")),
        ("toWarehouseId", NullableUuid("Only for a transfer, where it is required: the `id` of the destination warehouse the goods arrive in. "
            + "It must differ from `warehouseId`, which is the source. For a receipt or an issue leave it out or pass null.")),
        ("reference", NullableText("Your own reference, for example a delivery-note number; one line, at most 100 characters."
            + (update ? " Required: pass null for no value." : " Optional."))),
        ("note", NullableText("Free text, may have several lines, at most 2000 characters." + (update ? " Required: pass null for no value." : " Optional."))),
        ("lines", ArrayOf(
            "The lines, 1 to 200; they are numbered 1…n in the order given" + (update ? " and replace all stored lines." : ".")
            + " The same article may appear on several lines.",
            1, 200,
            ("articleId", Uuid("The `id` of a stock article (see `article_list`). Not the code.")),
            ("quantity", Number("Quantity in the article's base unit, as a JSON number (not a string): greater than 0, at most 999999999.999999, at most 6 decimal places.")))),
    ];

    private static readonly string[] AddressNames = ["addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    private static readonly (string, System.Text.Json.Nodes.JsonObject)[] IdOrCode =
    [
        ("id", Uuid("The record's `id`. Give either `id` or `code`, not both.")),
        ("code", Text("The record's code, in any letter case. Give either `id` or `code`, not both.")),
    ];

    private static (string, System.Text.Json.Nodes.JsonObject) IdOf(string what) => ("id", Uuid($"The `id` of the {what}."));

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
            + "`IN_USE`: articles use this unit; list them with `article_list` `baseUnitId`, or deactivate the unit with `uom_update` instead.",
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
            + "`IN_USE`: stock documents use the article, so `type` and `baseUnitId` cannot change (`errors` names which); keep both values. " + CodeTaken,
            Input(["id", "code", "name", "description", "type", "baseUnitId", "isActive"],
                [IdOf("article to replace"), .. ArticleFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<ArticleOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, ArticleDeleted>("article_delete", ToolKind.Delete,
            "Deletes an article that no stock document uses; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such article in this tenant. "
            + "`IN_USE`: a stock document (draft or posted) has a line with this article; deactivate it with `article_update` instead.",
            Input(["id"], IdOf("article to delete")),
            (services, input, ct) => services.GetRequiredService<ArticleOperations>().DeleteAsync(input.Id, ct)),

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
            "Deletes a partner; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such partner in this tenant.",
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
            "Deletes a warehouse that no stock document uses; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such warehouse in this tenant. "
            + "`IN_USE`: a stock document (draft or posted) names this warehouse; deactivate it with `warehouse_update` instead.",
            Input(["id"], IdOf("warehouse to delete")),
            (services, input, ct) => services.GetRequiredService<WarehouseOperations>().DeleteAsync(input.Id, ct)),

        // ---- stock documents, stock on hand, stock ledger
        XerpTool.For<ListStockDocumentsInput, PagedResult<StockDocumentSummaryDto>>("stock_document_list", ToolKind.Read,
            "Lists the tenant's stock documents (receipts, issues and transfers), newest first, with paging; each item has `lineCount` instead of the lines. "
            + "A reversed document has status `reversed` and `reversedBy`; the document that reversed it has status `posted` and `reversalOf`. "
            + "Filters combine with AND. " + Validation,
            Input([],
            [
                ("type", Text("Return only documents of this type.", "receipt", "issue", "transfer")),
                ("status", Text("Return only documents in this status. `posted` includes reversing documents; `reversed` are the originals that were reversed.", "draft", "posted", "reversed")),
                ("warehouseId", Uuid("Return only documents of the warehouse with this `id`: as the warehouse of the document or as the destination of a transfer.")),
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
            + "A draft changes no stock and reserves nothing; it takes effect only when posted with `stock_document_post`. "
            + Validation + " " + StockReferences,
            Input(["type", "documentDate", "warehouseId", "lines"],
            [
                ("type", Text("`receipt`: goods come into the warehouse. `issue`: goods leave it. `transfer`: goods move from `warehouseId` to `toWarehouseId`. Cannot be changed later.", "receipt", "issue", "transfer")),
                .. StockDocumentFields(update: false),
            ]),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().CreateAsync(input, ct)),

        XerpTool.WithId<ReplaceStockDocumentInput, StockDocumentDto>("stock_document_update", ToolKind.Update,
            "Replaces the date, warehouse, reference, note and all lines of a draft; every argument is required (`reference` and `note` may be null), "
            + "except `toWarehouseId`, which is required for a transfer only (the destination; `warehouseId` is the source). "
            + "The type cannot change. " + Validation + " `NOT_FOUND`: no such document in this tenant. " + DocumentPosted + " " + StockReferences,
            Input(["id", "documentDate", "warehouseId", "reference", "note", "lines"],
                [IdOf("draft stock document to replace"), .. StockDocumentFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<StockDocumentOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, StockDocumentDeleted>("stock_document_delete", ToolKind.Delete,
            "Deletes a draft stock document with its lines; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such document in this tenant. " + DocumentPosted,
            Input(["id"], IdOf("draft stock document to delete")),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().DeleteAsync(input.Id, ct)),

        XerpTool.For<RecordIdInput, StockDocumentDto>("stock_document_post", ToolKind.Post,
            "Posts a draft stock document. Posting is permanent: the document gets its number (`SR-…` receipt, `SI-…` issue, `ST-…` transfer), can no longer be changed or deleted, "
            + "and stock on hand changes through ledger entries - a receipt adds its quantities to the warehouse, an issue subtracts them, "
            + "a transfer subtracts them from the source and adds them to the destination together, so total stock does not change. "
            + "It either does all of this or nothing. `NOT_FOUND`: no such document in this tenant. " + DocumentPosted + " "
            + "`REFERENCE_INACTIVE`: a warehouse or an article of the document is inactive (`errors` says which); reactivate it or change the draft. "
            + "`INSUFFICIENT_STOCK`: an issue or a transfer would take more than is on hand in its (source) warehouse; `errors` names the short lines (`lines[0].quantity`). "
            + "Nothing was posted and the draft is unchanged: check `stock_on_hand_list`, then correct the draft with `stock_document_update` "
            + "or receive stock first, and post again.",
            Input(["id"], IdOf("draft stock document to post")),
            (services, input, ct) => services.GetRequiredService<StockDocumentOperations>().PostAsync(input.Id, ct)),

        XerpTool.WithId<ReverseStockDocumentInput, StockDocumentDto>("stock_document_reverse", ToolKind.Reverse,
            "Reverses a posted stock document (receipt, issue or transfer) and returns the new reversing document. This is the only way to correct a posted document. "
            + "A reversal is permanent and cannot itself be reversed. It cancels the whole document: the reversing document has the same type, warehouses and lines, "
            + "its own number from the same series, and ledger entries with the opposite sign, so stock is as if the original had never been posted; "
            + "the original gets status `reversed`. To correct a mistake, reverse and then create a new document with `stock_document_create`. "
            + "It either does all of this or nothing. Inactive warehouses or articles do not prevent a reversal. "
            + Validation + " `documentDate` earlier than the original's is one of them. `NOT_FOUND`: no such document in this tenant. "
            + "`INVALID_STATE`: the document is a draft, is already reversed, or is itself a reversing document. "
            + "`INSUFFICIENT_STOCK`: the goods the original brought in have already left, so the reversal would make stock negative; `errors` names the original's lines (`lines[0].quantity`). "
            + "Nothing was reversed: reverse the later documents that took the goods out first (see `stock_ledger_entry_list`), then reverse this one again.",
            Input(["id", "documentDate"],
            [
                IdOf("posted stock document to reverse"),
                ("documentDate", Text("The business date of the reversal as YYYY-MM-DD; not earlier than the `documentDate` of the document being reversed.")),
                ("note", NullableText("Why the document is reversed; free text, may have several lines, at most 2000 characters. Optional.")),
            ]),
            (services, id, input, ct) => services.GetRequiredService<StockDocumentOperations>().ReverseAsync(id, input, ct)),

        XerpTool.For<ListStockOnHandInput, PagedResult<StockOnHandDto>>("stock_on_hand_list", ToolKind.Read,
            "Lists stock on hand: one item per article and warehouse with a quantity other than zero, in the article's base unit, "
            + "ordered by article code then warehouse code. A pair that is not listed has zero stock. Drafts do not count. " + Validation,
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
