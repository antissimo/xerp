using Microsoft.EntityFrameworkCore;
using Xerp.Api.Entities;

namespace Xerp.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<UnitOfMeasure> UnitOfMeasure => Set<UnitOfMeasure>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Partner> Partners => Set<Partner>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Article>().ToTable("Articles");
        b.Entity<UnitOfMeasure>().ToTable("UnitOfMeasure");
        b.Entity<Warehouse>().ToTable("Warehouses");
        b.Entity<Partner>().ToTable("Partners");

        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            b.Entity(type.ClrType).HasIndex(nameof(Entity.Code)).IsUnique();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
}
