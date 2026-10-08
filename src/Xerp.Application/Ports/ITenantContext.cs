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
