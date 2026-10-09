using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xerp.Application.Ports;
using Xerp.Infrastructure.Persistence;
using Xerp.Infrastructure.Security;
using Xerp.Infrastructure.Tenancy;
using Xerp.Infrastructure.Time;

namespace Xerp.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the database and the other ports. The host must register <see cref="ITenantContext"/>.</summary>
    public static IServiceCollection AddXerpInfrastructure(
        this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddDbContext<XerpDbContext>((provider, options) => options.UseNpgsql(connectionString(provider)));
        services.AddScoped<IXerpDb>(provider => provider.GetRequiredService<XerpDbContext>());
        services.AddScoped<IApiKeyLookup, ApiKeyLookup>();
        services.AddScoped<ICurrentTenantReader, CurrentTenantReader>();
        services.AddScoped<ITenantProvisioningStore, TenantProvisioningStore>();
        services.AddSingleton<ApiKeyCrypto>();
        services.AddSingleton<IApiKeyGenerator>(provider => provider.GetRequiredService<ApiKeyCrypto>());
        services.AddSingleton<IApiKeyHasher>(provider => provider.GetRequiredService<ApiKeyCrypto>());
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
