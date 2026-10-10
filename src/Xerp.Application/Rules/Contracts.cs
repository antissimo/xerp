using System.Text.Json;

namespace Xerp.Application.Rules;

/// <summary>
/// A rule as a tenant sees it (spec 012, section 4): its definition - the same for every tenant - and the value
/// in force for this one. <c>Value</c> equals <c>Default</c> while <c>Source</c> is <c>default</c>;
/// <c>UpdatedAt</c> / <c>UpdatedBy</c> are those of the tenant's last set or reset of the rule, null while it
/// never changed it. Every property is always present.
/// </summary>
public sealed record RuleDto(
    string Key,
    string Group,
    string Name,
    string Description,
    bool Default,
    bool Value,
    string Source,
    DateTime? UpdatedAt,
    Guid? UpdatedBy);

/// <summary>One change of a rule (spec 012, section 4): the values in force before and after, when and by which API key.</summary>
public sealed record RuleChangeDto(Guid Id, string Key, string Action, bool OldValue, bool NewValue, DateTime ChangedAt, Guid ChangedBy);

public sealed record ListRulesInput(string? Search = null, string? Group = null, string? Source = null, int? Limit = null, int? Offset = null);

public sealed record ListRuleChangesInput(string? Key = null, int? Limit = null, int? Offset = null);

/// <summary>How a client without a URL path names one rule: by its <c>key</c>.</summary>
public sealed record RuleKeyInput(string? Key = null);

/// <summary>
/// The body of a set. <c>Value</c> is kept as it was sent, so that the operation - not the transport - says
/// that only the JSON literals <c>true</c> and <c>false</c> are values (spec 012, R5, R6): a missing value is
/// a fault of the form and is answered before the rule is looked up, any other value after it.
/// </summary>
public sealed record SetRuleInput(JsonElement Value = default);

/// <summary>The arguments of a set for a client without a URL path: the rule's <c>key</c> and the body.</summary>
public sealed record SetRuleByKeyInput(string? Key = null, JsonElement Value = default);
