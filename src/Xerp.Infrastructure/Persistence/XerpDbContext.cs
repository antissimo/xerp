using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Partners;
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
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();
    public DbSet<StockDocumentLine> StockDocumentLines => Set<StockDocumentLine>();
    public DbSet<StockLedgerEntry> StockLedgerEntries => Set<StockLedgerEntry>();
    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();

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
            // Provenance of a key (spec 003, section 3): audit columns like any other, so they include TenantId.
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(k => new { k.TenantId, k.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(k => new { k.TenantId, k.RevokedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<Article>(e =>
        {
            e.ToTable("Articles");
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Code).HasMaxLength(CodeRules.MaxLength);
            e.Property(a => a.Name).HasMaxLength(NameRules.MaxLength);
            e.Property(a => a.Description).HasMaxLength(ArticleRules.DescriptionMaxLength);
            e.Property(a => a.Type).HasMaxLength(10).HasConversion(v => v.ToName(), v => ArticleTypeNames.Parse(v));
            e.Property<string>(DbNames.CodeLower).HasComputedColumnSql("lower(\"Code\")", stored: true);
            e.HasIndex(nameof(Article.TenantId), DbNames.CodeLower).IsUnique().HasDatabaseName(DbNames.ArticleCodeIndex);
            // ADR-0008: the reference includes TenantId on both sides and restricts the delete of its target.
            // Its index (TenantId, BaseUnitId) also serves "articles by base unit".
            e.HasOne<UnitOfMeasure>().WithMany()
                .HasForeignKey(a => new { a.TenantId, a.BaseUnitId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName(DbNames.ArticleBaseUnitForeignKey);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(a => new { a.TenantId, a.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(a => new { a.TenantId, a.UpdatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Partner>(e =>
        {
            e.ToTable("Partners");
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Code).HasMaxLength(CodeRules.MaxLength);
            e.Property(p => p.Name).HasMaxLength(NameRules.MaxLength);
            e.Property(p => p.TaxId).HasMaxLength(PartnerRules.TaxIdMaxLength);
            e.ComplexProperty(p => p.Address, MapAddress);
            e.Property<string>(DbNames.CodeLower).HasComputedColumnSql("lower(\"Code\")", stored: true);
            e.HasIndex(nameof(Partner.TenantId), DbNames.CodeLower).IsUnique();
            // Documents of later specs reference a partner together with its tenant (ADR-0008, ADR-0011).
            e.HasAlternateKey(p => new { p.TenantId, p.Id });
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(p => new { p.TenantId, p.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(p => new { p.TenantId, p.UpdatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Warehouse>(e =>
        {
            e.ToTable("Warehouses");
            e.Property(w => w.Id).ValueGeneratedNever();
            e.Property(w => w.Code).HasMaxLength(CodeRules.MaxLength);
            e.Property(w => w.Name).HasMaxLength(NameRules.MaxLength);
            e.ComplexProperty(w => w.Address, MapAddress);
            e.Property<string>(DbNames.CodeLower).HasComputedColumnSql("lower(\"Code\")", stored: true);
            e.HasIndex(nameof(Warehouse.TenantId), DbNames.CodeLower).IsUnique();
            e.HasAlternateKey(w => new { w.TenantId, w.Id });
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(w => new { w.TenantId, w.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(w => new { w.TenantId, w.UpdatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Spec 005 / ADR-0012. Every key between these tables and to the masters includes TenantId and
        // restricts deletes: a used master cannot be deleted, and lines go only with their draft.
        modelBuilder.Entity<StockDocument>(e =>
        {
            e.ToTable("StockDocuments");
            e.Property(d => d.Id).ValueGeneratedNever();
            e.Property(d => d.Type).HasMaxLength(20).HasConversion(v => v.ToName(), v => StockDocumentTypeNames.Parse(v));
            e.Property(d => d.Status).HasMaxLength(20).HasConversion(v => v.ToName(), v => StockDocumentStatusNames.Parse(v));
            e.Property(d => d.Number).HasMaxLength(StockDocument.NumberMaxLength);
            e.Property(d => d.Reference).HasMaxLength(StockDocument.ReferenceMaxLength);
            e.Property(d => d.Note).HasMaxLength(StockDocument.NoteMaxLength);
            // Drafts have no number; PostgreSQL does not compare NULLs, so only posted documents are constrained.
            e.HasIndex(d => new { d.TenantId, d.Number }).IsUnique();
            e.HasAlternateKey(d => new { d.TenantId, d.Id });
            e.HasMany(d => d.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.DocumentId })
                .HasPrincipalKey(d => new { d.TenantId, d.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            // Its index (TenantId, WarehouseId) also serves "documents by warehouse".
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.UpdatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.PostedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockDocumentLine>(e =>
        {
            e.ToTable("StockDocumentLines");
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.Quantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            e.Ignore(l => l.Values);
            e.HasIndex(l => new { l.TenantId, l.DocumentId, l.LineNo }).IsUnique();
            // Its index (TenantId, ArticleId) also serves "is this article used".
            e.HasOne<Article>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.ArticleId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockLedgerEntry>(e =>
        {
            e.ToTable("StockLedgerEntries");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Quantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            // Stock on hand is a sum over this index.
            e.HasIndex(x => new { x.TenantId, x.ArticleId, x.WarehouseId });
            e.HasIndex(x => new { x.TenantId, x.DocumentId, x.LineNo });
            e.HasOne<Article>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ArticleId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockDocument>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.DocumentId })
                .HasPrincipalKey(d => new { d.TenantId, d.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PostedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentCounter>(e =>
        {
            e.ToTable("DocumentCounters");
            e.HasKey(c => new { c.TenantId, c.DocumentType });
            e.Property(c => c.DocumentType).HasMaxLength(20);
        });

        // Applied by type, not by hand, so a new tenant-owned entity cannot be forgotten.
        var configure = typeof(XerpDbContext).GetMethod(nameof(ConfigureTenantOwned), BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)).ToList())
            configure.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
    }

    /// <summary>An address is six plain columns of the owning table (ADR-0011, decision 3).</summary>
    private static void MapAddress(ComplexPropertyBuilder<Address> address)
    {
        address.Property(a => a.Line1).HasColumnName("AddressLine1").HasMaxLength(Address.LineMaxLength);
        address.Property(a => a.Line2).HasColumnName("AddressLine2").HasMaxLength(Address.LineMaxLength);
        address.Property(a => a.PostalCode).HasColumnName("PostalCode").HasMaxLength(Address.PostalCodeMaxLength);
        address.Property(a => a.City).HasColumnName("City").HasMaxLength(Address.CityMaxLength);
        address.Property(a => a.Region).HasColumnName("Region").HasMaxLength(Address.RegionMaxLength);
        address.Property(a => a.CountryCode).HasColumnName("CountryCode").HasMaxLength(CountryCodeRules.Length);
    }

    private void ConfigureTenantOwned<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned
    {
        var entity = modelBuilder.Entity<T>();
        entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        entity.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public async Task<T> SerializedPerTenantAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId ?? throw new InvalidOperationException("There is no current tenant to serialise work for.");
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        // A row lock on the tenant: it conflicts only with itself, not with the key-share locks that
        // foreign-key checks of ordinary writes take on the same row.
        await Database.ExecuteSqlAsync($"""SELECT 1 FROM "Tenants" WHERE "Id" = {tenantId} FOR NO KEY UPDATE""", cancellationToken);
        var result = await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        if (EnforcePostedIsImmutable() is { Count: > 0 } documentIds
            && StockDocuments.Any(d => documentIds.Contains(d.Id) && d.Status == StockDocumentStatus.Posted))
            throw PostedDocumentChanged();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenant();
        if (EnforcePostedIsImmutable() is { Count: > 0 } documentIds
            && await StockDocuments.AnyAsync(d => documentIds.Contains(d.Id) && d.Status == StockDocumentStatus.Posted, cancellationToken))
            throw PostedDocumentChanged();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException ex) when (Translate(ex) is { } translated)
        {
            throw translated;
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

    /// <summary>
    /// Spec 005, S3: the ledger is append-only and a posted document is immutable, whatever code asks for the
    /// save. Throws for a modified or deleted ledger entry and for a change to a posted document or its lines
    /// when the document is tracked; returns the ids of documents that are not tracked but whose lines are
    /// being changed, for the caller to look up.
    /// </summary>
    private List<Guid> EnforcePostedIsImmutable()
    {
        if (ChangeTracker.Entries<StockLedgerEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Stock ledger entries are append-only: they cannot be changed or deleted.");

        var documents = ChangeTracker.Entries<StockDocument>().ToDictionary(d => d.Entity.Id);
        if (documents.Values.Any(d => d.State is EntityState.Modified or EntityState.Deleted
                && d.Property(x => x.Status).OriginalValue == StockDocumentStatus.Posted))
            throw PostedDocumentChanged();

        var untracked = new List<Guid>();
        foreach (var line in ChangeTracker.Entries<StockDocumentLine>())
        {
            if (line.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            foreach (var documentId in new[] { line.Property(l => l.DocumentId).OriginalValue, line.Property(l => l.DocumentId).CurrentValue }.Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document))
                    untracked.Add(documentId);
                else if (document.State != EntityState.Added && document.Property(x => x.Status).OriginalValue == StockDocumentStatus.Posted)
                    throw PostedDocumentChanged();
            }
        }
        return untracked;
    }

    private static InvalidOperationException PostedDocumentChanged() =>
        new("A posted stock document and its lines are immutable.");

    /// <summary>
    /// Constraint violations that Application has an answer for (CODE_TAKEN, REFERENCE_NOT_FOUND, IN_USE),
    /// as provider-independent exceptions; null for anything else.
    /// </summary>
    private Exception? Translate(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres =>
            UniqueViolation(postgres.ConstraintName, exception),
        // ON DELETE RESTRICT has its own code: the row is still referenced.
        PostgresException { SqlState: PostgresErrorCodes.RestrictViolation } postgres =>
            new ForeignKeyViolationException(postgres.ConstraintName, blockedDelete: true, exception),
        // A missing target, and (for NO ACTION keys) a still-referenced row, share this code and the
        // constraint; which one it was follows from what was being saved: a delete can only be blocked.
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } postgres =>
            new ForeignKeyViolationException(
                postgres.ConstraintName,
                blockedDelete: exception.Entries.Count > 0 && exception.Entries.All(e => e.State == EntityState.Deleted),
                exception),
        _ => null,
    };

    /// <summary>Says what the violated index means in the model, so Application does not match index names.</summary>
    private UniqueConstraintViolationException UniqueViolation(string? constraintName, Exception inner)
    {
        var index = constraintName is null
            ? null
            : Model.GetEntityTypes().SelectMany(t => t.GetIndexes()).FirstOrDefault(i => i.GetDatabaseName() == constraintName);
        return new UniqueConstraintViolationException(
            constraintName, index?.DeclaringEntityType.ClrType, index?.Properties.Select(p => p.Name).ToList() ?? [], inner);
    }
}
