using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// What <c>UseRedis</c> actually registers. These were silent failures: a second backend quietly
/// replaced (or failed to replace) the first.
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
}
