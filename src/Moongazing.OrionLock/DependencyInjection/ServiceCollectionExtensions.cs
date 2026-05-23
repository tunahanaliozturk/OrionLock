using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.DependencyInjection;

/// <summary>DI extensions for OrionLock.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OrionLock core. Call a backend extension on the returned builder
    /// (for example <c>UseRedis</c> or <c>UseEntityFrameworkCore</c>) to supply a provider.
    /// </summary>
    public static OrionLockBuilder AddOrionLock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDistributedLock>(sp =>
            new DistributedLock(sp.GetRequiredService<IDistributedLockProvider>()));

        return new OrionLockBuilder(services);
    }
}
