using Xerp.Application.ApiKeys;
using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Application.Identity;
using Xerp.Application.Partners;
using Xerp.Application.UnitsOfMeasure;
using Xerp.Application.Warehouses;
using static Xerp.Api.Mcp.ToolSchemas;

namespace Xerp.Api.Mcp;

/// <summary>A tool that takes no arguments binds to this: any argument is then an unknown one.</summary>
public sealed record NoArguments;

/// <summary>
/// The complete list of MCP tools (spec 003, 5.3; spec 004, 5.1). Adding an operation means adding its tool here; the
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
            + "`REFERENCE_INACTIVE`: the unit is inactive and the article did not already use it. " + CodeTaken,
            Input(["id", "code", "name", "description", "type", "baseUnitId", "isActive"],
                [IdOf("article to replace"), .. ArticleFields(update: true)]),
            (services, id, input, ct) => services.GetRequiredService<ArticleOperations>().ReplaceAsync(id, input, ct)),

        XerpTool.For<RecordIdInput, ArticleDeleted>("article_delete", ToolKind.Delete,
            "Deletes an article; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such article in this tenant.",
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
            "Deletes a warehouse; returns `{ \"deleted\": true }`. `NOT_FOUND`: no such warehouse in this tenant.",
            Input(["id"], IdOf("warehouse to delete")),
            (services, input, ct) => services.GetRequiredService<WarehouseOperations>().DeleteAsync(input.Id, ct)),

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
