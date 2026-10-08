namespace Xerp.Domain.Tenancy;

/// <summary>One legal entity. The only entity that is not tenant-owned.</summary>
public sealed class Tenant
{
    private Tenant() { }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    public static Tenant Create(string code, string name, DateTime now) => throw new NotImplementedException();
}
