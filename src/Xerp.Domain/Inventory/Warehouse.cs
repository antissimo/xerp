using Xerp.Domain.Common;

namespace Xerp.Domain.Inventory;

/// <summary>A place where stock is kept. Until the stock ledger exists it is a named record with an address.</summary>
public sealed class Warehouse : ITenantOwned
{
    private Warehouse() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Address Address { get; private set; } = Address.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static Warehouse Create(string code, string name, Address address, bool isActive, DateTime now, Guid actorKeyId)
    {
        var warehouse = new Warehouse { Id = Guid.CreateVersion7(), CreatedAt = now, CreatedBy = actorKeyId };
        warehouse.Replace(code, name, address, isActive, now, actorKeyId);
        return warehouse;
    }

    public void Replace(string code, string name, Address address, bool isActive, DateTime now, Guid actorKeyId)
    {
        // Validate both before assigning anything, so a rejected replace leaves the warehouse untouched.
        var newCode = CodeRules.Normalize(code, nameof(code));
        var newName = NameRules.Normalize(name, nameof(name));
        Code = newCode;
        Name = newName;
        Address = address;
        IsActive = isActive;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}
