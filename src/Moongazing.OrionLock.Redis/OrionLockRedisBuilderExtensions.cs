using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Fairness;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>Registers the Redis OrionLock backend.</summary>
public static class OrionLockRedisBuilderExtensions
{
    /// <summary>Uses Redis as the OrionLock backend, connecting with <paramref name="connectionString"/>.</summary>
    public static OrionLockBuilder UseRedis(
        this OrionLockBuilder builder, string connectionString, Action<RedisLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new RedisLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connectionString));
        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            sp => new RedisLockProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options));

        return builder;
    }

    /// <summary>Uses Redis as the OrionLock backend over an already-registered <see cref="IConnectionMultiplexer"/>.</summary>
    public static OrionLockBuilder UseRedis(
        this OrionLockBuilder builder, Action<RedisLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RedisLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            sp => new RedisLockProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options));

        return builder;
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
            sp => new SharedExclusiveLock(sp.GetRequiredService<ISharedExclusiveLockProvider>()));

        return builder;
    }
}
