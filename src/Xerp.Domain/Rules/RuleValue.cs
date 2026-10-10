using Xerp.Domain.Common;

namespace Xerp.Domain.Rules;

/// <summary>
/// The value a tenant has set for a rule (ADR-0020, decision 3). A row exists only for a rule the tenant has
/// set - no row means the default - and is removed by a reset. Setting the value the default has is a set like
/// any other: the tenant then has its own value (spec 012, R7).
/// </summary>
public sealed class RuleValue : ITenantOwned
{
    private RuleValue() { }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Key { get; private set; } = "";
    public bool Value { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static RuleValue Set(RuleDefinition rule, bool value, DateTime now, Guid actorKeyId) =>
        new() { Key = rule.Key, Value = value, UpdatedAt = now, UpdatedBy = actorKeyId };

    public void Change(bool value, DateTime now, Guid actorKeyId)
    {
        Value = value;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}

public enum RuleChangeAction
{
    /// <summary>The tenant gave the rule a value of its own.</summary>
    Set,

    /// <summary>The tenant's value was removed: the rule is at its default again.</summary>
    Reset,
}

/// <summary>The contract names of <see cref="RuleChangeAction"/>, as stored and as sent to clients.</summary>
public static class RuleChangeActionNames
{
    public const string Set = "set";
    public const string Reset = "reset";

    public static string ToName(this RuleChangeAction action) => action switch
    {
        RuleChangeAction.Set => Set,
        RuleChangeAction.Reset => Reset,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static RuleChangeAction Parse(string name) => name switch
    {
        Set => RuleChangeAction.Set,
        Reset => RuleChangeAction.Reset,
        _ => throw new ArgumentException($"Unknown rule change action '{name}'.", nameof(name)),
    };
}

/// <summary>
/// One change of a rule by a tenant (ADR-0020, decisions 3 and 8; spec 012, R10): who, when, and the value
/// in force before and after. Append-only: a change is never altered or deleted.
/// </summary>
public sealed class RuleChange : ITenantOwned
{
    private RuleChange() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Key { get; private set; } = "";
    public RuleChangeAction Action { get; private set; }

    /// <summary>The value in force before the change - the tenant's own, or the default.</summary>
    public bool OldValue { get; private set; }

    /// <summary>The value in force after it.</summary>
    public bool NewValue { get; private set; }

    public DateTime ChangedAt { get; private set; }
    public Guid ChangedBy { get; private set; }

    public static RuleChange Record(RuleDefinition rule, RuleChangeAction action, bool oldValue, bool newValue, DateTime now, Guid actorKeyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Key = rule.Key,
            Action = action,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedAt = now,
            ChangedBy = actorKeyId,
        };
}
