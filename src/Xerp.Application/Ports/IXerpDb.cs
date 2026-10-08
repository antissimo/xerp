using Microsoft.EntityFrameworkCore;
using Xerp.Domain.Catalog;
using Xerp.Domain.Inventory;
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
/// cannot see Npgsql.
/// </summary>
public sealed class UniqueConstraintViolationException(string? constraintName, Exception inner)
    : Exception($"Unique constraint '{constraintName}' was violated.", inner)
{
    public string? ConstraintName { get; } = constraintName;
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
