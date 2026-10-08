using Xerp.Domain.Common;

namespace Xerp.Domain.Tenancy;

public enum ActorType
{
    Human,
    Agent,
}

/// <summary>The contract names of <see cref="ActorType"/> (<c>human</c>, <c>agent</c>), as stored and as sent to clients.</summary>
public static class ActorTypeNames
{
    public const string Human = "human";
    public const string Agent = "agent";

    public static string ToName(this ActorType actorType) => actorType switch
    {
        ActorType.Human => Human,
        ActorType.Agent => Agent,
        _ => throw new ArgumentOutOfRangeException(nameof(actorType)),
    };

    public static ActorType Parse(string name) => name switch
    {
        Human => ActorType.Human,
        Agent => ActorType.Agent,
        _ => throw new ArgumentException($"Unknown actor type '{name}'.", nameof(name)),
    };
}

/// <summary>A credential of one tenant. Only the hash of the secret is kept.</summary>
public sealed class ApiKey : ITenantOwned
{
    public const int NameMaxLength = 100;

    private ApiKey() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = "";
    public ActorType ActorType { get; private set; }
    public string KeyHash { get; private set; } = "";
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    public static ApiKey Create(Guid tenantId, string name, ActorType actorType, string keyHash, DateTime now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = NameRules.Normalize(name, nameof(name), NameMaxLength),
            ActorType = actorType,
            KeyHash = keyHash,
            IsActive = true,
            CreatedAt = now,
        };
}
