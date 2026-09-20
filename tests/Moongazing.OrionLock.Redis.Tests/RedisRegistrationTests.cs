using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;
using Moq;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// What <c>UseRedis</c> actually registers. Both failures here were silent: a second backend quietly
/// replaced (or failed to replace) the first, and the connection string you passed was quietly thrown
/// away whenever the app already had an <see cref="IConnectionMultiplexer"/> - which is the normal
/// case, since the same app usually caches with Redis too.
/// </summary>
public sealed class RedisRegistrationTests
{
    [Fact]
    public void UseInMemoryThenUseRedis_Throws_RatherThanRunningTheFakeInProduction()
    {
        // The reported shape. Redis registered with TryAddSingleton, so the in-memory fake registered
        // first WON and nothing said so: production ran an in-process dictionary.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOrionLock().UseInMemory().UseRedis("localhost:6379"));

        Assert.Contains("inmemory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("redis", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseRedisWithAConnectionString_HonoursIt_EvenWhenTheAppAlreadyHasAMultiplexer()
    {
        // The app's own cache multiplexer, registered before OrionLock - the normal case, and the one
        // where TryAddSingleton used to swallow the argument below and send the locks to the cache's
        // Redis instead of the one named here.
        var appMultiplexer = new Mock<IConnectionMultiplexer>().Object;
        var services = new ServiceCollection();
        services.AddSingleton(appMultiplexer);

        services.AddOrionLock().UseRedis(
            "orionlock-should-connect-here.invalid:6379,connectTimeout=50");

        using var sp = services.BuildServiceProvider();

        // The app's registration is neither read nor replaced.
        Assert.Same(appMultiplexer, sp.GetRequiredService<IConnectionMultiplexer>());

        // And the provider connects with the string it was handed, so building it fails to reach the
        // unreachable host named above. Before the fix it resolved the app's multiplexer and succeeded -
        // silently locking against the wrong Redis.
        Assert.ThrowsAny<Exception>(() => { _ = sp.GetRequiredService<IDistributedLockProvider>(); });
    }

    [Fact]
    public void UseRedisWithoutAConnectionString_StillSharesTheAppsMultiplexer()
    {
        // The other overload is how you opt IN to sharing the application's connection; it must keep
        // working exactly as before.
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IConnectionMultiplexer>().Object);

        services.AddOrionLock().UseRedis();

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IDistributedLockProvider>());
    }
}
