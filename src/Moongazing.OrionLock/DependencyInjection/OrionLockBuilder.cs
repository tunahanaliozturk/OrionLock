using Microsoft.Extensions.DependencyInjection;

namespace Moongazing.OrionLock.DependencyInjection;

/// <summary>
/// Returned by <c>AddOrionLock</c>. Backend packages add <c>Use*</c> extension methods on this
/// type to register an <see cref="Providers.IDistributedLockProvider"/>.
/// </summary>
public sealed class OrionLockBuilder
{
    /// <summary>Creates a builder over the given service collection.</summary>
    public OrionLockBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }
}
