using Xerp.Application.ApiKeys;
using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Application.Identity;
using Xerp.Application.UnitsOfMeasure;
using static Xerp.Api.Mcp.ToolSchemas;

namespace Xerp.Api.Mcp;

/// <summary>A tool that takes no arguments binds to this: any argument is then an unknown one.</summary>
public sealed record NoArguments;

/// <summary>
/// The complete list of MCP tools (spec 003, 5.3). Adding an operation means adding its tool here; the
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
