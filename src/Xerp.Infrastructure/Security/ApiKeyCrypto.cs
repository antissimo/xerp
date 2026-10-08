using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Xerp.Application.Ports;

namespace Xerp.Infrastructure.Security;

public sealed class ApiKeyCrypto : IApiKeyGenerator, IApiKeyHasher
{
    public const string Prefix = "xerp_";

    public string Generate() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
