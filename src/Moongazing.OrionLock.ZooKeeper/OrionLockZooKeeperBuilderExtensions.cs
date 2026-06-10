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

        // Register the open ACL factory as the default; consumers calling UseDigestAcl
        // (or registering their own IZooKeeperAclFactory) will win via the TryAdd here.
        builder.Services.TryAddSingleton<IZooKeeperAclFactory, OpenZooKeeperAclFactory>();
        builder.Services.TryAddSingleton<IZooKeeperClientAdapter>(
            sp => new DefaultZooKeeperClientAdapter(
                sp.GetRequiredService<ZooKeeper>(),
                sp.GetRequiredService<IZooKeeperAclFactory>()));
        builder.Services.RemoveAll<IDistributedLockProvider>();
        builder.Services.AddSingleton<IDistributedLockProvider>(
            sp => new ZooKeeperLockProvider(sp.GetRequiredService<IZooKeeperClientAdapter>(), options));

        return builder;
    }

    /// <summary>
    /// Replace the default <see cref="OpenZooKeeperAclFactory"/> with a digest-based
    /// factory. Caller is responsible for ALSO adding the matching auth-info on the
    /// ZooKeeper session before the lock provider is used:
    /// <c>client.addAuthInfo("digest", Encoding.UTF8.GetBytes($"{username}:{password}"))</c>.
    /// Otherwise the session will not satisfy the ACL the factory issues and acquire
    /// calls will surface as <c>NoAuthException</c>.
    /// </summary>
    public static OrionLockBuilder UseDigestAcl(
        this OrionLockBuilder builder, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(password);
        builder.Services.RemoveAll<IZooKeeperAclFactory>();
        builder.Services.AddSingleton<IZooKeeperAclFactory>(
            new DigestZooKeeperAclFactory(username, password));
        return builder;
    }
}
