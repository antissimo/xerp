using Microsoft.EntityFrameworkCore;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;
using Xerp.Domain.Partners;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Ports;

/// <summary>
/// The database as Application sees it (ADR-0001). Every tenant-owned set is already filtered to the
/// current tenant by the implementation; Application code never filters by tenant itself and never
/// calls IgnoreQueryFilters().
/// </summary>
public interface IXerpDb
{
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<UnitOfMeasure> UnitsOfMeasure { get; }
    DbSet<Article> Articles { get; }
    DbSet<ArticleUnit> ArticleUnits { get; }
    DbSet<Partner> Partners { get; }
    DbSet<Warehouse> Warehouses { get; }
    DbSet<StockDocument> StockDocuments { get; }
    DbSet<StockDocumentLine> StockDocumentLines { get; }

    /// <summary>Append-only: entries are added by posting and never changed or deleted (spec 005, S3).</summary>
    DbSet<StockLedgerEntry> StockLedgerEntries { get; }

    DbSet<DocumentCounter> DocumentCounters { get; }

    /// <exception cref="UniqueConstraintViolationException">A unique index rejected the change.</exception>
    /// <exception cref="ForeignKeyViolationException">A foreign key rejected the change.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction that holds a lock on the current tenant, so that
    /// two such pieces of work of one tenant never overlap. Reads inside <paramref name="work"/> see
    /// everything committed before the lock was obtained.
    /// </summary>
    Task<T> SerializedPerTenantAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown by <see cref="IXerpDb.SaveChangesAsync"/> when the database rejects a change because of a
/// unique index. Infrastructure translates the provider's error into this type because Application
/// cannot see Npgsql, and says what the index means: which entity, which properties.
/// </summary>
public sealed class UniqueConstraintViolationException(
    string? constraintName, Type? entityType, IReadOnlyList<string> properties, Exception inner)
    : Exception($"Unique constraint '{constraintName}' was violated.", inner)
{
    public string? ConstraintName { get; } = constraintName;

    /// <summary>The entity whose index was violated; null when the index is not part of the model.</summary>
    public Type? EntityType { get; } = entityType;

    /// <summary>The model properties the index covers (for a code: <c>TenantId</c> and <see cref="DbNames.CodeLower"/>).</summary>
    public IReadOnlyList<string> Properties { get; } = properties;

    /// <summary>True when the violated index is the one that keeps the codes of <typeparamref name="TEntity"/> unique.</summary>
    public bool IsCodeOf<TEntity>() => EntityType == typeof(TEntity) && Properties.Contains(DbNames.CodeLower);
}

/// <summary>
/// Thrown by <see cref="IXerpDb.SaveChangesAsync"/> when the database rejects a change because of a
/// foreign key (ADR-0008, decision 6): either a written row points at a row that does not exist
/// (any more), or a deleted row is still referenced.
/// </summary>
public sealed class ForeignKeyViolationException(string? constraintName, bool blockedDelete, Exception inner)
    : Exception($"Foreign key '{constraintName}' was violated.", inner)
{
    public string? ConstraintName { get; } = constraintName;

    /// <summary>True when a delete was refused because the row is still referenced; false when a written row's reference is missing.</summary>
    public bool BlockedDelete { get; } = blockedDelete;
}
