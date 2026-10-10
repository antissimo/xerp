using Xerp.Domain.Rules;

namespace Xerp.Application.Ports;

/// <summary>
/// The configurable rules as an operation sees them (ADR-0020, decision 4): the value of every rule for the
/// current tenant, at one moment. An operation reads them once, at its own start - after it holds the tenant's
/// lock when it takes one (spec 012, R14) - and judges the whole request by that one set. Nothing is cached
/// across requests (decision 10): a change applies to the next operation (R13).
/// </summary>
public interface IRules
{
    Task<TenantRules> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules of one tenant at one moment: the values it has set, and the default for every rule it has not.
/// A stored value whose key is no rule of the registry is never asked for, and so is ignored (spec 012, S4).
/// </summary>
/// <param name="ownValues">By rule key, the values the tenant has set.</param>
public sealed class TenantRules(IReadOnlyDictionary<string, bool> ownValues)
{
    /// <summary>A tenant that has set nothing: every rule at its default.</summary>
    public static readonly TenantRules Defaults = new(new Dictionary<string, bool>());

    /// <summary>The value of the rule that is in force for the tenant.</summary>
    public bool this[RuleDefinition rule] => ownValues.TryGetValue(rule.Key, out var value) ? value : rule.Default;
}
