namespace Moongazing.OrionLock.ZooKeeper;

using global::org.apache.zookeeper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

/// <summary>DI helpers for the ZooKeeper OrionLock backend.</summary>
public static class OrionLockZooKeeperBuilderExtensions
{
    /// <summary>
    /// Uses ZooKeeper as the OrionLock backend over an already-registered
    /// <see cref="ZooKeeper"/> client. Consumers register the ZooKeeper client themselves
    /// because the connection requires a <see cref="Watcher"/> instance that the consumer
    /// owns (session-state callbacks for re-connect handling).
    /// </summary>
    public static OrionLockBuilder UseZooKeeper(
        this OrionLockBuilder builder,
        Action<ZooKeeperLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ZooKeeperLockOptions();
        configure?.Invoke(options);
        // Validate at DI registration time rather than waiting for the provider's ctor
        // to surface the error during the first acquire. A misconfigured RootPath is a
        // startup-time mistake; failing here keeps the stack trace pointing at the
        // consumer's UseZooKeeper(...) call site.
        options.ValidateAndNormalise();

        builder.Services.TryAddSingleton<IZooKeeperClientAdapter>(
            sp => new DefaultZooKeeperClientAdapter(sp.GetRequiredService<ZooKeeper>()));
        builder.Services.RemoveAll<IDistributedLockProvider>();
        builder.Services.AddSingleton<IDistributedLockProvider>(
            sp => new ZooKeeperLockProvider(sp.GetRequiredService<IZooKeeperClientAdapter>(), options));

        return builder;
    }
}
