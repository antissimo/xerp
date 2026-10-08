using Xerp.Domain.Common;

namespace Xerp.Domain.Tenancy;

public enum ActorType
{
    Human,
    Agent,
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
        throw new NotImplementedException();
}
