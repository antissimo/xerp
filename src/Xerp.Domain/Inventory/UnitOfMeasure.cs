using Xerp.Domain.Common;

namespace Xerp.Domain.Inventory;

public sealed class UnitOfMeasure : ITenantOwned
{
    private UnitOfMeasure() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static UnitOfMeasure Create(string code, string name, bool isActive, DateTime now, Guid actorKeyId) =>
        throw new NotImplementedException();

    public void Replace(string code, string name, bool isActive, DateTime now, Guid actorKeyId) =>
        throw new NotImplementedException();
}
