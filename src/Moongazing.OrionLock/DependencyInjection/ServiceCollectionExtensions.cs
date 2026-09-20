using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.Fairness;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.DependencyInjection;

/// <summary>DI extensions for OrionLock.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OrionLock core. Call a backend extension on the returned builder
    /// (for example <c>UseRedis</c> or <c>UseEntityFrameworkCore</c>) to supply a provider.
    /// </summary>
    /// <remarks>
    /// The registered <see cref="IDistributedLockProvider"/> is wrapped in an internal measuring
    /// decorator that emits per-backend acquire-latency and lease-renewal histograms on the
    /// OrionLock Meter. Backend identification comes from <see cref="Diagnostics.BackendNameAttribute"/>
    /// on the concrete provider type.
    /// </remarks>
    public static OrionLockBuilder AddOrionLock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Default fairness coordinator: no-op (preserves v0.3.2 behaviour). Consumers replace
        // with InProcessFifoWaiterCoordinator (or a future distributed implementation) before
        // calling AddOrionLock to enable FIFO ordering via DistributedLockOptions.UseFifoWaiterCoordinator.
        services.TryAddSingleton<IFifoWaiterCoordinator, NullFifoWaiterCoordinator>();

        services.TryAddSingleton<IDistributedLock>(sp =>
        {
            var raw = sp.GetRequiredService<IDistributedLockProvider>();
            var measured = raw is MeasuringLockProvider ? raw : new MeasuringLockProvider(raw);
            // The fault guard goes OUTSIDE the measuring decorator, not inside it. Inside, the backend
            // tag would be resolved from the guard rather than from the concrete provider, and every
            // metric would lose which backend it came from. Out here the measuring decorator still sees
            // - and counts - the raw driver failure before this wraps it for the caller.
            var guarded = new BackendFaultGuard(measured);
            var fifo = sp.GetRequiredService<IFifoWaiterCoordinator>();
            // v0.3.25: explicit GetService for the optional observer so consumer
            // registration is honoured (the ActivatorUtilities longest-ctor trap from
            // v0.2.20 OrionPatch / v6.5.23 OrionGuard does not apply here because we
            // construct explicitly, but GetService must still be threaded by hand).
            var observer = sp.GetService<ILockEventObserver>();
            return new DistributedLock(guarded, fifo, observer);
        });

        return new OrionLockBuilder(services);
    }
}
