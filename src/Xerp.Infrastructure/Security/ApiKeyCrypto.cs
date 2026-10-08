using Xerp.Application.Ports;

namespace Xerp.Infrastructure.Security;

public sealed class ApiKeyCrypto : IApiKeyGenerator, IApiKeyHasher
{
    public string Generate() => throw new NotImplementedException();

    public string Hash(string key) => throw new NotImplementedException();
}
