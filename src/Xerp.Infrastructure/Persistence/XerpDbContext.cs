using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;
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
    public DbSet<ArticleUnit> ArticleUnits => Set<ArticleUnit>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();
    public DbSet<StockDocumentLine> StockDocumentLines => Set<StockDocumentLine>();
    public DbSet<StockLedgerEntry> StockLedgerEntries => Set<StockLedgerEntry>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();

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

        // Spec 007 / ADR-0014: a conversion belongs to one article and has no id of its own; its key is
        // (tenant, article, unit). Both references include TenantId and restrict deletes: a unit with a
        // conversion cannot be deleted, and an article's conversions are deleted with it by the application.
        modelBuilder.Entity<ArticleUnit>(e =>
        {
            e.ToTable("ArticleUnits");
            e.HasKey(c => new { c.TenantId, c.ArticleId, c.UnitId });
            e.Property(c => c.Factor).HasPrecision(12, UnitConversion.FactorDecimalPlaces);
            e.HasOne<Article>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.ArticleId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Its index (TenantId, UnitId) also serves "articles by alternative unit" and "is this unit used".
            e.HasOne<UnitOfMeasure>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.UnitId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.UpdatedBy })
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
            // Spec 011, R2 / ADR-0019: at most one default warehouse per tenant, and it is active - the second
            // barrier behind the rules in Application. (That there is at least one is kept by provisioning and
            // by the rules that refuse to deactivate or delete it.)
            e.HasIndex([nameof(Warehouse.TenantId)], DbNames.DefaultWarehouseIndex).IsUnique().HasFilter("\"IsDefault\"");
            e.ToTable(t => t.HasCheckConstraint(DbNames.DefaultWarehouseIsActiveCheck, "NOT \"IsDefault\" OR \"IsActive\""));
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
            // Spec 006: the destination of a transfer; its index also serves "documents into this warehouse".
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.ToWarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Spec 006: the two ends of a reversal. At most one reversing document per original.
            e.HasOne<StockDocument>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.ReversalOfId })
                .HasPrincipalKey(d => new { d.TenantId, d.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => new { d.TenantId, d.ReversalOfId }).IsUnique().HasFilter("\"ReversalOfId\" IS NOT NULL");
            e.HasOne<StockDocument>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.ReversedById })
                .HasPrincipalKey(d => new { d.TenantId, d.Id })
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
            // Spec 009: the order a receipt fulfils; its index also serves "documents of this order".
            e.HasOne<PurchaseOrder>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.PurchaseOrderId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Spec 010: the order an issue delivers.
            e.HasOne<SalesOrder>().WithMany()
                .HasForeignKey(d => new { d.TenantId, d.SalesOrderId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.Ignore(d => d.Fulfilment);
            e.Ignore(d => d.Link);
        });

        modelBuilder.Entity<StockDocumentLine>(e =>
        {
            e.ToTable("StockDocumentLines");
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.Quantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            // Spec 007: set at posting and never afterwards; null on a draft line.
            e.Property(l => l.Factor).HasPrecision(12, UnitConversion.FactorDecimalPlaces);
            e.Property(l => l.BaseQuantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            // Spec 008: on a count line, stock on hand when the draft was last saved; null on every other type.
            e.Property(l => l.BookQuantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            e.Ignore(l => l.DifferenceQuantity);
            e.Ignore(l => l.Entry);
            e.Ignore(l => l.BaseValues);
            e.HasIndex(l => new { l.TenantId, l.DocumentId, l.LineNo }).IsUnique();
            // Its index (TenantId, ArticleId) also serves "is this article used".
            e.HasOne<Article>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.ArticleId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Spec 007: the unit the line was entered in; its index also serves "is this unit used".
            e.HasOne<UnitOfMeasure>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.UnitId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Spec 009: a line of a linked document names a line of its order. The line repeats the document's
            // order so that this is a foreign key; both columns are null on an unlinked document.
            e.HasOne<PurchaseOrderLine>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.PurchaseOrderId, l.OrderLineNo })
                .HasPrincipalKey(o => new { o.TenantId, o.OrderId, o.LineNo })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_StockDocumentLines_PurchaseOrderLines_OrderLine");
            // Spec 010: the same for a line of a delivery; OrderLineNo serves both links.
            e.HasOne<SalesOrderLine>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.SalesOrderId, l.OrderLineNo })
                .HasPrincipalKey(o => new { o.TenantId, o.OrderId, o.LineNo })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_StockDocumentLines_SalesOrderLines_OrderLine");
        });

        // Spec 009 / ADR-0016, spec 010 / ADR-0017: one mapping for every kind of order; a kind names its
        // tables and what it calls the partner, the due date and the progress.
        MapOrder<PurchaseOrder, PurchaseOrderLine>(modelBuilder, "PurchaseOrders", "PurchaseOrderLines", "SupplierId", "ExpectedDate", "ReceivedBaseQuantity");
        MapOrder<SalesOrder, SalesOrderLine>(modelBuilder, "SalesOrders", "SalesOrderLines", "CustomerId", "RequestedDate", "DeliveredBaseQuantity");

        modelBuilder.Entity<StockLedgerEntry>(e =>
        {
            e.ToTable("StockLedgerEntries");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Quantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            // The sum of a pair - what its stored balance must equal (spec 011, R20) - is a sum over this index.
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

        // Spec 011 / ADR-0018: one number per (tenant, warehouse, article), derived from the ledger. No id of
        // its own and no audit columns; both references include TenantId and restrict deletes.
        modelBuilder.Entity<StockBalance>(e =>
        {
            e.ToTable("StockBalances", t => t.HasCheckConstraint(DbNames.StockBalanceNotNegativeCheck, "\"Quantity\" >= 0"));
            e.HasKey(b => new { b.TenantId, b.WarehouseId, b.ArticleId });
            e.Property(b => b.Quantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(b => new { b.TenantId, b.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Its index (TenantId, ArticleId) also serves "stock of this article across warehouses".
            e.HasOne<Article>().WithMany()
                .HasForeignKey(b => new { b.TenantId, b.ArticleId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
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

    public async Task<T> AtOneMomentAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        // REPEATABLE READ in PostgreSQL is snapshot isolation: every statement of the transaction reads the
        // snapshot of its first one. Nothing is written, so there is nothing to commit and nothing can conflict.
        await using var transaction = await Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        return await work(cancellationToken);
    }

    private static void MapOrder<TOrder, TLine>(
        ModelBuilder modelBuilder, string table, string lineTable, string partnerColumn, string dueDateColumn, string fulfilledColumn)
        where TOrder : Order<TLine>
        where TLine : OrderLine
    {
        // Every key includes TenantId and restricts deletes: a master an order names
        // cannot be deleted, and lines go only with their draft.
        modelBuilder.Entity<TOrder>(e =>
        {
            e.ToTable(table);
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Status).HasMaxLength(20).HasConversion(v => v.ToName(), v => OrderStatusNames.Parse(v));
            e.Property(o => o.Number).HasMaxLength(StockDocument.NumberMaxLength);
            // What every order has, under the names its kind gives it.
            e.Property(o => o.PartnerId).HasColumnName(partnerColumn);
            e.Property(o => o.DueDate).HasColumnName(dueDateColumn);
            e.Property(o => o.Reference).HasMaxLength(StockDocument.ReferenceMaxLength);
            e.Property(o => o.Note).HasMaxLength(StockDocument.NoteMaxLength);
            e.Ignore(o => o.FulfilmentStatus);
            e.Ignore(o => o.Outstanding);
            // Drafts have no number; PostgreSQL does not compare NULLs, so only confirmed orders are constrained.
            e.HasIndex(o => new { o.TenantId, o.Number }).IsUnique();
            e.HasAlternateKey(o => new { o.TenantId, o.Id });
            e.HasMany(o => o.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.OrderId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            // Its index (TenantId, partner) also serves "orders by partner" and "is this partner used".
            e.HasOne<Partner>().WithMany()
                .HasForeignKey(o => new { o.TenantId, o.PartnerId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(o => new { o.TenantId, o.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(o => new { o.TenantId, o.CreatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(o => new { o.TenantId, o.UpdatedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(o => new { o.TenantId, o.ConfirmedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApiKey>().WithMany()
                .HasForeignKey(o => new { o.TenantId, o.ClosedBy })
                .HasPrincipalKey(k => new { k.TenantId, k.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Frozen at confirmation: article, unit, quantity, unit price, factor, base quantity. Progress, the one
        // column that moves afterwards: the fulfilled quantity, written by the posting and the reversal of a
        // linked stock document (spec 009, R25, section 11).
        modelBuilder.Entity<TLine>(e =>
        {
            e.ToTable(lineTable);
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.Quantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            e.Property(l => l.UnitPrice).HasPrecision(18, PriceRules.DecimalPlaces);
            e.Property(l => l.Factor).HasPrecision(12, UnitConversion.FactorDecimalPlaces);
            e.Property(l => l.BaseQuantity).HasPrecision(18, QuantityRules.DecimalPlaces);
            e.Property(l => l.FulfilledBaseQuantity).HasColumnName(fulfilledColumn).HasPrecision(18, QuantityRules.DecimalPlaces);
            e.Ignore(l => l.LineAmount);
            e.Ignore(l => l.Entry);
            // Unique (TenantId, OrderId, LineNo); a line of a linked stock document references it.
            e.HasAlternateKey(l => new { l.TenantId, l.OrderId, l.LineNo });
            // Its index (TenantId, ArticleId) also serves "is this article used" and the incoming or reserved quantity.
            e.HasOne<Article>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.ArticleId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UnitOfMeasure>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.UnitId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        if (EnforceConfirmedOrderIsImmutable<PurchaseOrder, PurchaseOrderLine>() is { Count: > 0 } purchaseOrderIds
            && PurchaseOrders.Any(o => purchaseOrderIds.Contains(o.Id) && o.Status != OrderStatus.Draft))
            throw ConfirmedOrderChanged();
        if (EnforceConfirmedOrderIsImmutable<SalesOrder, SalesOrderLine>() is { Count: > 0 } salesOrderIds
            && SalesOrders.Any(o => salesOrderIds.Contains(o.Id) && o.Status != OrderStatus.Draft))
            throw ConfirmedOrderChanged();
        if (EnforcePostedIsImmutable() is { Count: > 0 } documentIds
            && StockDocuments.Any(d => documentIds.Contains(d.Id) && d.Status != StockDocumentStatus.Draft))
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
        if (EnforceConfirmedOrderIsImmutable<PurchaseOrder, PurchaseOrderLine>() is { Count: > 0 } purchaseOrderIds
            && await PurchaseOrders.AnyAsync(o => purchaseOrderIds.Contains(o.Id) && o.Status != OrderStatus.Draft, cancellationToken))
            throw ConfirmedOrderChanged();
        if (EnforceConfirmedOrderIsImmutable<SalesOrder, SalesOrderLine>() is { Count: > 0 } salesOrderIds
            && await SalesOrders.AnyAsync(o => salesOrderIds.Contains(o.Id) && o.Status != OrderStatus.Draft, cancellationToken))
            throw ConfirmedOrderChanged();
        if (EnforcePostedIsImmutable() is { Count: > 0 } documentIds
            && await StockDocuments.AnyAsync(d => documentIds.Contains(d.Id) && d.Status != StockDocumentStatus.Draft, cancellationToken))
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
    /// Spec 005, S3 and spec 006, S2: the ledger is append-only and a posted document is immutable, whatever
    /// code asks for the save. Throws for a modified or deleted ledger entry and for a change to a posted or
    /// reversed document or its lines when the document is tracked; returns the ids of documents that are not
    /// tracked but whose lines are being changed, for the caller to look up.
    /// <para>
    /// The one change a posted document accepts is its reversal: <c>posted -> reversed</c> together with
    /// <c>ReversedById</c>, naming a reversing document that is inserted by the same save.
    /// </para>
    /// </summary>
    private List<Guid> EnforcePostedIsImmutable()
    {
        if (ChangeTracker.Entries<StockLedgerEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Stock ledger entries are append-only: they cannot be changed or deleted.");

        var documents = ChangeTracker.Entries<StockDocument>().ToDictionary(d => d.Entity.Id);
        foreach (var document in documents.Values)
        {
            if (document.State is not (EntityState.Modified or EntityState.Deleted) || WasDraft(document))
                continue;
            if (document.State == EntityState.Deleted || !IsReversalOf(document, documents))
                throw PostedDocumentChanged();
        }

        var untracked = new List<Guid>();
        foreach (var line in ChangeTracker.Entries<StockDocumentLine>())
        {
            if (line.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            foreach (var documentId in new[] { line.Property(l => l.DocumentId).OriginalValue, line.Property(l => l.DocumentId).CurrentValue }.Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document))
                    untracked.Add(documentId);
                else if (document.State != EntityState.Added && !WasDraft(document))
                    throw PostedDocumentChanged();
            }
        }
        return untracked;
    }

    private static bool WasDraft(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<StockDocument> document) =>
        document.Property(d => d.Status).OriginalValue == StockDocumentStatus.Draft;

    /// <summary>
    /// Whether the modification of a posted document is exactly its reversal: nothing but <c>Status</c> and
    /// <c>ReversedById</c> changed, from <c>posted</c> and null, to <c>reversed</c> and a document added by
    /// this save that names it as the one it reverses.
    /// </summary>
    private static bool IsReversalOf(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<StockDocument> document,
        Dictionary<Guid, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<StockDocument>> tracked)
    {
        var status = document.Property(d => d.Status);
        var reversedBy = document.Property(d => d.ReversedById);
        if (status.OriginalValue != StockDocumentStatus.Posted || status.CurrentValue != StockDocumentStatus.Reversed)
            return false;
        if (reversedBy.OriginalValue is not null || reversedBy.CurrentValue is not { } reversalId)
            return false;
        if (document.Properties.Any(p => p.IsModified && p.Metadata.Name is not (nameof(StockDocument.Status) or nameof(StockDocument.ReversedById))))
            return false;
        return tracked.TryGetValue(reversalId, out var reversal)
            && reversal.State == EntityState.Added
            && reversal.Entity.ReversalOfId == document.Entity.Id
            && reversal.Entity.Status == StockDocumentStatus.Posted;
    }

    private static InvalidOperationException PostedDocumentChanged() =>
        new("A posted stock document and its lines are immutable; the only change it accepts is its reversal.");

    /// <summary>
    /// Spec 009, S3 and AC-02; spec 010, AC-02: a confirmed or closed order of any kind is immutable, whatever code asks for the save. The
    /// one change the order itself accepts is <c>confirmed &lt;-&gt; closed</c> with <c>ClosedAt</c> and
    /// <c>ClosedBy</c>; the one change its lines accept is progress - the fulfilled quantity, which is not part
    /// of what was ordered. Throws when the order is tracked; returns the ids of orders that are not tracked
    /// but whose lines are being changed in what is frozen, for the caller to look up.
    /// </summary>
    private List<Guid> EnforceConfirmedOrderIsImmutable<TOrder, TLine>()
        where TOrder : Order<TLine>
        where TLine : OrderLine
    {
        var orders = ChangeTracker.Entries<TOrder>().ToDictionary(o => o.Entity.Id);
        foreach (var order in orders.Values)
        {
            if (order.State is not (EntityState.Modified or EntityState.Deleted) || WasDraft<TOrder, TLine>(order))
                continue;
            if (order.State == EntityState.Deleted || !IsCloseOrReopen<TOrder, TLine>(order))
                throw ConfirmedOrderChanged();
        }

        var untracked = new List<Guid>();
        foreach (var line in ChangeTracker.Entries<TLine>())
        {
            if (line.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            if (line.State == EntityState.Modified
                && line.Properties.All(p => !p.IsModified || p.Metadata.Name == nameof(OrderLine.FulfilledBaseQuantity)))
                continue;
            foreach (var orderId in new[] { line.Property(l => l.OrderId).OriginalValue, line.Property(l => l.OrderId).CurrentValue }.Distinct())
            {
                if (!orders.TryGetValue(orderId, out var order))
                    untracked.Add(orderId);
                else if (order.State != EntityState.Added && !WasDraft<TOrder, TLine>(order))
                    throw ConfirmedOrderChanged();
            }
        }
        return untracked;
    }

    private static bool WasDraft<TOrder, TLine>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TOrder> order)
        where TOrder : Order<TLine>
        where TLine : OrderLine =>
        order.Property(o => o.Status).OriginalValue == OrderStatus.Draft;

    /// <summary>
    /// Whether the modification of a confirmed or closed order is exactly a close or a reopen: nothing but
    /// <c>Status</c>, <c>ClosedAt</c> and <c>ClosedBy</c> changed, between <c>confirmed</c> without and
    /// <c>closed</c> with the two.
    /// </summary>
    private static bool IsCloseOrReopen<TOrder, TLine>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TOrder> order)
        where TOrder : Order<TLine>
        where TLine : OrderLine
    {
        if (order.Properties.Any(p => p.IsModified
                && p.Metadata.Name is not (nameof(Order<TLine>.Status) or nameof(Order<TLine>.ClosedAt) or nameof(Order<TLine>.ClosedBy))))
            return false;
        var status = order.Property(o => o.Status);
        var closed = order.Entity.ClosedAt is not null && order.Entity.ClosedBy is not null;
        var open = order.Entity.ClosedAt is null && order.Entity.ClosedBy is null;
        return (status.OriginalValue, status.CurrentValue) switch
        {
            (OrderStatus.Confirmed, OrderStatus.Closed) => closed,
            (OrderStatus.Closed, OrderStatus.Confirmed) => open,
            _ => false,
        };
    }

    private static InvalidOperationException ConfirmedOrderChanged() =>
        new("A confirmed or closed order and its lines are immutable; the only changes it accepts are close and reopen, and the received quantity of its lines.");

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
