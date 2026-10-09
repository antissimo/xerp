namespace Xerp.Application.Ports;

/// <summary>
/// Who is acting. Derived only from the authenticated credential, never from a URL, header, query
/// string or body. Both values are null when there is no tenant credential (anonymous, platform admin).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? ApiKeyId { get; }
}

public sealed record CurrentTenant(Guid Id, string Code, string Name);

/// <summary>
/// Reads the one tenant row an operation may see: the tenant of the current credential. Tenants are not
/// tenant-owned rows, so they are not exposed as a set (review 001).
/// </summary>
public interface ICurrentTenantReader
{
    /// <returns>Null when there is no tenant credential or the tenant no longer exists.</returns>
    Task<CurrentTenant?> ReadAsync(CancellationToken cancellationToken = default);
}
