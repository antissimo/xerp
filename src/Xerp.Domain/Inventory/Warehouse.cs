using Xerp.Domain.Common;

namespace Xerp.Domain.Inventory;

/// <summary>
/// A place where stock is kept. Exactly one warehouse of a tenant is its default (ADR-0019): the warehouse a
/// document is created on when the caller names none. The default warehouse is always active.
/// </summary>
public sealed class Warehouse : ITenantOwned
{
    /// <summary>What the warehouse a tenant is created with starts as (spec 011, R1); only <see cref="IsDefault"/> marks it afterwards.</summary>
    public const string DefaultCode = "CENTRAL";
    public const string DefaultName = "Central warehouse";

    private Warehouse() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Address Address { get; private set; } = Address.Empty;
    public bool IsActive { get; private set; }

    /// <summary>Whether this is the tenant's default warehouse. Moved only by <see cref="MakeDefault"/> and <see cref="ClearDefault"/>.</summary>
    public bool IsDefault { get; private set; }
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

    /// <summary>The warehouse a new tenant gets (spec 011, R1): active, the default, without an address, written by the tenant's first key.</summary>
    public static Warehouse CreateDefault(DateTime now, Guid actorKeyId)
    {
        var warehouse = Create(DefaultCode, DefaultName, Address.Empty, isActive: true, now, actorKeyId);
        warehouse.IsDefault = true;
        return warehouse;
    }

    /// <exception cref="InvalidOperationException">The default warehouse would be deactivated (spec 011, R4).</exception>
    public void Replace(string code, string name, Address address, bool isActive, DateTime now, Guid actorKeyId)
    {
        if (IsDefault && !isActive)
            throw new InvalidOperationException("The default warehouse cannot be deactivated.");
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

    /// <summary>Makes this warehouse the tenant's default (spec 011, R6). The caller clears the former default in the same transaction.</summary>
    /// <exception cref="InvalidOperationException">The warehouse is inactive.</exception>
    public void MakeDefault(DateTime now, Guid actorKeyId)
    {
        if (!IsActive)
            throw new InvalidOperationException("Only an active warehouse can be the default warehouse.");
        IsDefault = true;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    /// <summary>Makes the former default an ordinary warehouse again (spec 011, R6, R7).</summary>
    public void ClearDefault(DateTime now, Guid actorKeyId)
    {
        IsDefault = false;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}
