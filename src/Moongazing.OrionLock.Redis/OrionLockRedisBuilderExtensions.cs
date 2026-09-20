using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Fairness;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;
using Moongazing.OrionLock.Diagnostics;

namespace Moongazing.OrionLock.Redis;

/// <summary>Registers the Redis OrionLock backend.</summary>
public static class OrionLockRedisBuilderExtensions
{
    /// <summary>
    /// The DI key the connection-string overload registers OrionLock's own multiplexer under.
    /// </summary>
    private const string LockMultiplexerKey = "Moongazing.OrionLock.Redis";

    /// <summary>
    /// Uses Redis as the OrionLock backend, connecting with <paramref name="connectionString"/>.
    /// </summary>
    /// <remarks>
    /// The connection string is honoured even when the application has already registered an
    /// <see cref="IConnectionMultiplexer"/> of its own - which is the normal case, since the same app
    /// usually caches with Redis too. This overload used to register the lock's multiplexer with
    /// <c>TryAddSingleton</c>, so an existing registration won and the argument you passed was silently
    /// discarded: the locks went to the cache's Redis, not the one you named. OrionLock now keeps its
    /// own multiplexer under a private DI key, so the app's <see cref="IConnectionMultiplexer"/> is
    /// neither read nor replaced. Use the no-argument
    /// <see cref="UseRedis(OrionLockBuilder, Action{RedisLockOptions})"/> overload when you DO want the
    /// locks to share the application's connection.
    /// </remarks>
    public static OrionLockBuilder UseRedis(
        this OrionLockBuilder builder, string connectionString, Action<RedisLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new RedisLockOptions();
        configure?.Invoke(options);

        // Keyed, so a repeat call replaces rather than stacks, and the container still owns disposal.
        builder.Services.RemoveAllKeyed<IConnectionMultiplexer>(LockMultiplexerKey);
        builder.Services.AddKeyedSingleton<IConnectionMultiplexer>(
            LockMultiplexerKey, (_, _) => ConnectionMultiplexer.Connect(connectionString));

        return builder.UseBackend(
            "redis",
            sp => new RedisLockProvider(
                sp.GetRequiredKeyedService<IConnectionMultiplexer>(LockMultiplexerKey), options));
    }

    /// <summary>
    /// Uses Redis as the OrionLock backend over the application's already-registered
    /// <see cref="IConnectionMultiplexer"/>, sharing its connection.
    /// </summary>
    public static OrionLockBuilder UseRedis(
        this OrionLockBuilder builder, Action<RedisLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RedisLockOptions();
        configure?.Invoke(options);

        return builder.UseBackend(
            "redis", sp => new RedisLockProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options));
    }

    /// <summary>
    /// Register the <see cref="RedisFifoWaiterCoordinator"/> as the distributed
    /// <see cref="IFifoWaiterCoordinator"/> (v0.3.4). Replaces any previously-registered
    /// coordinator (including the default <c>NullFifoWaiterCoordinator</c> wired by
    /// <c>AddOrionLock</c>). Consumers still opt in per-acquire via
    /// <c>DistributedLockOptions.UseFifoWaiterCoordinator = true</c>.
    /// </summary>
    public static OrionLockBuilder UseRedisFifoWaiterCoordinator(
        this OrionLockBuilder builder, Action<RedisFifoWaiterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RedisFifoWaiterOptions();
        configure?.Invoke(options);

        builder.Services.RemoveAll<IFifoWaiterCoordinator>();
        builder.Services.AddSingleton<IFifoWaiterCoordinator>(
            sp => new RedisFifoWaiterCoordinator(sp.GetRequiredService<IConnectionMultiplexer>(), options));

        return builder;
    }

    /// <summary>
    /// Adds the Redis distributed reader-writer (shared/exclusive) backend (v0.4.2):
    /// <see cref="RedisSharedExclusiveLockProvider"/> as <see cref="ISharedExclusiveLockProvider"/>
    /// plus the composed <see cref="ISharedExclusiveLock"/>. Additive to the exclusive-only
    /// <c>UseRedis</c> registration and over the same already-registered
    /// <see cref="IConnectionMultiplexer"/>; both <see cref="IDistributedLock"/> and
    /// <see cref="ISharedExclusiveLock"/> can then resolve against Redis. Uses
    /// <c>TryAddSingleton</c>, matching the exclusive Redis backend, so a consumer-supplied
    /// implementation registered earlier wins.
    /// </summary>
    public static OrionLockBuilder UseRedisSharedExclusive(
        this OrionLockBuilder builder, Action<RedisSharedExclusiveLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RedisSharedExclusiveLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<ISharedExclusiveLockProvider>(
            sp => new RedisSharedExclusiveLockProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options));
        builder.Services.TryAddSingleton<ISharedExclusiveLock>(
            sp => new SharedExclusiveLock(
                sp.GetRequiredService<ISharedExclusiveLockProvider>(),
                // Without this the reader-writer lock emits its metrics but reaches no registered
                // observer, which is the gap that let the two surfaces drift apart in the first place.
                sp.GetService<ILockEventObserver>()));

        return builder;
    }
}
