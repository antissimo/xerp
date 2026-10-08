using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Tenancy;

namespace Xerp.Infrastructure.Persistence;

/// <summary>
/// Enforces tenant isolation (docs/architecture.md section 3): every <see cref="ITenantOwned"/> entity is
/// filtered to the current tenant, stamped with it on insert, and refused on save if it belongs to another.
/// </summary>
public sealed class XerpDbContext(DbContextOptions<XerpDbContext> options, ITenantContext tenant)
    : DbContext(options), IXerpDb
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    private Guid? CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(DbText).GetMethod(nameof(DbText.Lower))!).HasName("lower").IsBuiltIn();

        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("Tenants");
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.Code).HasMaxLength(CodeRules.MaxLength);
            e.Property(t => t.Name).HasMaxLength(NameRules.MaxLength);
            e.Property<string>(DbNames.CodeLower).HasComputedColumnSql("lower(\"Code\")", stored: true);
            e.HasIndex(DbNames.CodeLower).IsUnique().HasDatabaseName(DbNames.TenantCodeIndex);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("ApiKeys");
            e.Property(k => k.Id).ValueGeneratedNever();
            e.Property(k => k.Name).HasMaxLength(ApiKey.NameMaxLength);
            e.Property(k => k.ActorType).HasMaxLength(10).HasConversion(v => v.ToName(), v => ActorTypeNames.Parse(v));
            e.Property(k => k.KeyHash).HasMaxLength(64);
            e.HasIndex(k => k.KeyHash).IsUnique();
        });

        modelBuilder.Entity<UnitOfMeasure>(e =>
        {
            e.ToTable("UnitsOfMeasure");
            e.Property(u => u.Id).ValueGeneratedNever();
            e.Property(u => u.Code).HasMaxLength(CodeRules.MaxLength);
            e.Property(u => u.Name).HasMaxLength(NameRules.MaxLength);
            e.Property<string>(DbNames.CodeLower).HasComputedColumnSql("lower(\"Code\")", stored: true);
            e.HasIndex(nameof(UnitOfMeasure.TenantId), DbNames.CodeLower).IsUnique().HasDatabaseName(DbNames.UnitOfMeasureCodeIndex);
            // The audit columns point at keys of the same tenant: the foreign keys include TenantId.
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(u => new { u.TenantId, u.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(u => new { u.TenantId, u.UpdatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Applied by type, not by hand, so a new tenant-owned entity cannot be forgotten.
        var configure = typeof(XerpDbContext).GetMethod(nameof(ConfigureTenantOwned), BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)).ToList())
            configure.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
    }

    private void ConfigureTenantOwned<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned
    {
        var entity = modelBuilder.Entity<T>();
        entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        entity.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException ex) when (AsUniqueViolation(ex) is { } violation)
        {
            throw violation;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenant();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException ex) when (AsUniqueViolation(ex) is { } violation)
        {
            throw violation;
        }
    }

    /// <summary>Stamps the current tenant on inserted rows and refuses any write to a row of another tenant.</summary>
    private void EnforceTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            if (CurrentTenantId is not { } tenantId)
                throw new InvalidOperationException($"Cannot save {entry.Metadata.ClrType.Name}: there is no current tenant.");

            var property = entry.Property(e => e.TenantId);
            if (entry.State == EntityState.Added && property.CurrentValue == Guid.Empty)
                property.CurrentValue = tenantId;
            else if (property.CurrentValue != tenantId || (entry.State != EntityState.Added && property.OriginalValue != tenantId))
                throw new InvalidOperationException($"Cannot save {entry.Metadata.ClrType.Name}: it belongs to another tenant.");
        }
    }

    private static UniqueConstraintViolationException? AsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            ? new UniqueConstraintViolationException(postgres.ConstraintName, exception)
            : null;
}
