using Xerp.Application.Ports;

namespace Xerp.Api.Http;

/// <summary>
/// The tenant and API key of the current request. Set once, by <see cref="ApiV1Middleware"/>, from the
/// authenticated credential and from nothing else (spec 001, T1).
/// </summary>
public sealed class RequestActor : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? ApiKeyId { get; private set; }

    public void Set(Guid tenantId, Guid apiKeyId)
    {
        if (TenantId is not null)
            throw new InvalidOperationException("The actor of a request cannot change.");
        TenantId = tenantId;
        ApiKeyId = apiKeyId;
    }
}
