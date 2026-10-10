using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Rules;

namespace Xerp.Application.Rules;

/// <summary>
/// The rule operations of spec 012, sections 4 and 6.2: list, get, set, reset and the history of changes. One
/// method = one HTTP endpoint = one MCP tool. The definitions come from <see cref="RuleRegistry"/> and are the
/// same for every tenant; the values and the history are the tenant's - <see cref="IXerpDb"/> is already
/// filtered to it.
/// <para>
/// Set and reset run inside <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>, the lock every posting,
/// reversal and save of a document takes (ADR-0020, decision 10): a change of a rule is ordered against them,
/// so each of them is judged entirely by the values before it or entirely by the values after it (R14), and
/// two changes of one rule never overlap. A change writes the value and its history in one save, touches
/// nothing else (R15), and applies to whatever starts after it (R13). Reading takes no lock.
/// </para>
/// </summary>
public sealed class RuleOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    public const string SourceDefault = "default";
    public const string SourceTenant = "tenant";

    private const string KeyField = "key";
    private const string ValueField = "value";
    private const string NotFoundDetail = "Rule not found: the key is not one of the rules of this system. List them with rule_list (GET /rules); keys are case-sensitive.";

    /// <summary>
    /// R1, R2: every rule of the registry, set or not, ordered by key. The filters combine with AND:
    /// <c>group</c> is compared exactly, <c>source</c> is <c>default</c> or <c>tenant</c>, <c>search</c> is a
    /// case-insensitive text the key or the name contains.
    /// </summary>
    public async Task<Result<PagedResult<RuleDto>>> ListAsync(ListRulesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var source = string.IsNullOrEmpty(input.Source) ? null : input.Source;
        if (source is not (null or SourceDefault or SourceTenant))
            errors.Add("source", $"source must be \"{SourceDefault}\" or \"{SourceTenant}\".");
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        var rules = await RulesAsync(RuleRegistry.All, cancellationToken);
        var matching = rules
            .Where(r => string.IsNullOrEmpty(input.Group) || string.Equals(r.Group, input.Group, StringComparison.Ordinal))
            .Where(r => source is null || r.Source == source)
            .Where(r => search is null
                || r.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new PagedResult<RuleDto>(matching.Skip(offset).Take(limit).ToList(), matching.Count, limit, offset);
    }

    /// <summary>R4: the key addresses the rule and is compared exactly; anything that is no rule of the registry is not found.</summary>
    public async Task<Result<RuleDto>> GetAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (Address(key, out var rule) is { } error)
            return error;
        return (await RulesAsync([rule], cancellationToken)).Single();
    }

    /// <summary>
    /// R5-R8, R10. Order of checks: the form of the request (a value was sent) -> the rule exists -> the value
    /// is the JSON literal <c>true</c> or <c>false</c>. Then the tenant has that value as its own - also when
    /// it is the default's (R7). Setting the value the tenant already has changes nothing: no change is
    /// recorded and the attribution stays (R8).
    /// </summary>
    public async Task<Result<RuleDto>> SetAsync(string? key, SetRuleInput input, CancellationToken cancellationToken = default)
    {
        var form = new ValidationErrors();
        if (key is null)
            form.Add(KeyField, "key is required.");
        if (input.Value.ValueKind == JsonValueKind.Undefined)
            form.Add(ValueField, "value is required: true or false.");
        if (form.Any)
            return form.ToError();
        if (Address(key, out var rule) is { } error)
            return error;
        if (input.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return AppError.Validation(ValueField, "value must be the JSON boolean true or false: a rule is a yes/no switch. A string, a number and null are not values.");
        var value = input.Value.GetBoolean();

        return await db.SerializedPerTenantAsync<Result<RuleDto>>(async ct =>
        {
            var stored = await db.RuleValues.SingleOrDefaultAsync(v => v.Key == rule.Key, ct);
            if (stored is null || stored.Value != value)
            {
                var (now, actor) = (clock.UtcNow, ActorKeyId());
                db.RuleChanges.Add(RuleChange.Record(rule, RuleChangeAction.Set, oldValue: stored?.Value ?? rule.Default, newValue: value, now, actor));
                if (stored is null)
                    db.RuleValues.Add(RuleValue.Set(rule, value, now, actor));
                else
                    stored.Change(value, now, actor);
                await db.SaveChangesAsync(ct);
            }
            return (await RulesAsync([rule], ct)).Single();
        }, cancellationToken);
    }

    /// <summary>As <see cref="SetAsync(string?, SetRuleInput, CancellationToken)"/>, for a client without a URL path.</summary>
    public Task<Result<RuleDto>> SetAsync(SetRuleByKeyInput input, CancellationToken cancellationToken = default) =>
        SetAsync(input.Key, new SetRuleInput(input.Value), cancellationToken);

    /// <summary>
    /// R9, R10: removes the tenant's value, so the rule is at its default again; the change is recorded like a
    /// set and is what the rule shows as its last change. Reset of a rule the tenant has no value for changes
    /// nothing and records nothing.
    /// </summary>
    public async Task<Result<RuleDto>> ResetAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (Address(key, out var rule) is { } error)
            return error;

        return await db.SerializedPerTenantAsync<Result<RuleDto>>(async ct =>
        {
            var stored = await db.RuleValues.SingleOrDefaultAsync(v => v.Key == rule.Key, ct);
            if (stored is not null)
            {
                db.RuleChanges.Add(RuleChange.Record(rule, RuleChangeAction.Reset, oldValue: stored.Value, newValue: rule.Default, clock.UtcNow, ActorKeyId()));
                db.RuleValues.Remove(stored);
                await db.SaveChangesAsync(ct);
            }
            return (await RulesAsync([rule], ct)).Single();
        }, cancellationToken);
    }

    /// <summary>R11: the changes of the tenant, newest first; <c>key</c> filters by exact key, and a text that is no rule finds nothing.</summary>
    public async Task<Result<PagedResult<RuleChangeDto>>> ListChangesAsync(ListRuleChangesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        var changes = db.RuleChanges.AsNoTracking();
        if (!string.IsNullOrEmpty(input.Key))
            changes = changes.Where(c => c.Key == input.Key);

        var total = await changes.CountAsync(cancellationToken);
        var page = await changes
            .OrderByDescending(c => c.ChangedAt)
            .ThenByDescending(c => c.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var items = page
            .Select(c => new RuleChangeDto(c.Id, c.Key, c.Action.ToName(), c.OldValue, c.NewValue, c.ChangedAt, c.ChangedBy))
            .ToList();
        return new PagedResult<RuleChangeDto>(items, total, limit, offset);
    }

    /// <summary>
    /// The given definitions with the tenant's values. The value in force is the tenant's own where it has
    /// one and the default otherwise (R3); the attribution is that of the value, or - once a reset removed it -
    /// of that reset, the rule's newest change (R9). A stored value whose key is no rule is never looked at (S4).
    /// </summary>
    private async Task<List<RuleDto>> RulesAsync(IReadOnlyList<RuleDefinition> definitions, CancellationToken cancellationToken)
    {
        var keys = definitions.Select(d => d.Key).ToList();
        var values = await db.RuleValues.AsNoTracking()
            .Where(v => keys.Contains(v.Key))
            .ToDictionaryAsync(v => v.Key, cancellationToken);
        var withoutValue = keys.Where(k => !values.ContainsKey(k)).ToList();
        var lastChanges = withoutValue.Count == 0
            ? []
            : (await db.RuleChanges.AsNoTracking()
                .Where(c => withoutValue.Contains(c.Key))
                .Where(c => !db.RuleChanges.Any(later => later.Key == c.Key && later.ChangedAt > c.ChangedAt))
                .Select(c => new { c.Key, c.Id, c.ChangedAt, c.ChangedBy })
                .ToListAsync(cancellationToken))
                .GroupBy(c => c.Key)
                .ToDictionary(g => g.Key, g => g.MaxBy(c => c.Id)!);

        return definitions.Select(d =>
        {
            if (values.TryGetValue(d.Key, out var own))
                return new RuleDto(d.Key, d.Group, d.Name, d.Description, d.Default, own.Value, SourceTenant, own.UpdatedAt, own.UpdatedBy);
            return lastChanges.TryGetValue(d.Key, out var last)
                ? new RuleDto(d.Key, d.Group, d.Name, d.Description, d.Default, d.Default, SourceDefault, last.ChangedAt, last.ChangedBy)
                : new RuleDto(d.Key, d.Group, d.Name, d.Description, d.Default, d.Default, SourceDefault, null, null);
        }).ToList();
    }

    /// <summary>
    /// Null when <paramref name="key"/> is a rule of the registry. Otherwise the error: a missing key is a
    /// validation error under the argument's name (only a client without a URL path can omit it); any text
    /// that is no rule is <c>NOT_FOUND</c>.
    /// </summary>
    private static AppError? Address(string? key, out RuleDefinition rule)
    {
        rule = RuleRegistry.Find(key)!;
        if (key is null)
            return AppError.Validation(KeyField, "key is required.");
        return rule is null ? AppError.NotFound(NotFoundDetail) : null;
    }

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A rule can only be changed by a tenant API key.");
}
