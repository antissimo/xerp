using Xerp.Domain.Inventory;
using Xerp.Domain.Tenancy;

namespace Xerp.UnitTests;

public class DomainEntityTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void R7_R11_Created_unit_has_v7_id_equal_timestamps_and_actor_in_both_audit_fields()
    {
        var actor = Guid.CreateVersion7();

        var unit = UnitOfMeasure.Create("  kg ", " Kilogram ", true, T0, actor);

        Assert.Equal(7, unit.Id.Version);
        Assert.Equal("kg", unit.Code);
        Assert.Equal("Kilogram", unit.Name);
        Assert.True(unit.IsActive);
        Assert.Equal(T0, unit.CreatedAt);
        Assert.Equal(T0, unit.UpdatedAt);
        Assert.Equal(actor, unit.CreatedBy);
        Assert.Equal(actor, unit.UpdatedBy);
        Assert.Equal(Guid.Empty, unit.TenantId); // stamped by the DbContext, not by callers
    }

    [Fact]
    public void R7_Replace_changes_values_and_update_audit_but_never_creation_audit()
    {
        var creator = Guid.CreateVersion7();
        var editor = Guid.CreateVersion7();
        var unit = UnitOfMeasure.Create("kg", "Kilogram", true, T0, creator);
        var id = unit.Id;
        var later = T0.AddMinutes(5);

        unit.Replace("kgm", "Kilogramme", false, later, editor);

        Assert.Equal(id, unit.Id);
        Assert.Equal("kgm", unit.Code);
        Assert.Equal("Kilogramme", unit.Name);
        Assert.False(unit.IsActive);
        Assert.Equal(T0, unit.CreatedAt);
        Assert.Equal(creator, unit.CreatedBy);
        Assert.Equal(later, unit.UpdatedAt);
        Assert.Equal(editor, unit.UpdatedBy);
    }

    [Fact]
    public void Unit_cannot_be_created_or_replaced_with_an_invalid_code_or_name()
    {
        var actor = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(() => UnitOfMeasure.Create("a b", "Name", true, T0, actor));
        Assert.Throws<ArgumentException>(() => UnitOfMeasure.Create("kg", "  ", true, T0, actor));

        var unit = UnitOfMeasure.Create("kg", "Kilogram", true, T0, actor);
        Assert.Throws<ArgumentException>(() => unit.Replace("a/b", "Name", true, T0, actor));
        Assert.Equal("kg", unit.Code);
    }

    [Fact]
    public void Tenant_is_created_active_with_v7_id_and_trimmed_values()
    {
        var tenant = Tenant.Create(" acme ", " Acme d.o.o. ", T0);

        Assert.Equal(7, tenant.Id.Version);
        Assert.Equal("acme", tenant.Code);
        Assert.Equal("Acme d.o.o.", tenant.Name);
        Assert.True(tenant.IsActive);
        Assert.Equal(T0, tenant.CreatedAt);
        Assert.Throws<ArgumentException>(() => Tenant.Create("a b", "Acme", T0));
    }

    [Fact]
    public void R12_Api_key_belongs_to_its_tenant_and_is_active()
    {
        var tenantId = Guid.CreateVersion7();

        var key = ApiKey.Create(tenantId, "initial", ActorType.Human, new string('a', 64), T0);

        Assert.Equal(7, key.Id.Version);
        Assert.Equal(tenantId, key.TenantId);
        Assert.Equal("initial", key.Name);
        Assert.Equal(ActorType.Human, key.ActorType);
        Assert.True(key.IsActive);
        Assert.Throws<ArgumentException>(() => ApiKey.Create(tenantId, " ", ActorType.Human, new string('a', 64), T0));
        Assert.Throws<ArgumentException>(() => ApiKey.Create(tenantId, new string('n', 101), ActorType.Agent, new string('a', 64), T0));
    }
}
