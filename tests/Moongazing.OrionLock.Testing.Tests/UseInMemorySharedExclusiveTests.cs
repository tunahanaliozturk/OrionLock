using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class UseInMemorySharedExclusiveTests
{
    [Fact]
    public void UseInMemory_RegistersSharedExclusiveProviderAndLock()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<InMemorySharedExclusiveLockProvider>(
            sp.GetRequiredService<ISharedExclusiveLockProvider>());
        Assert.IsType<SharedExclusiveLock>(sp.GetRequiredService<ISharedExclusiveLock>());
    }

    [Fact]
    public async Task UseInMemory_SharedExclusiveLock_Works_EndToEnd()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();

        using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<ISharedExclusiveLock>();

        var opts = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromMilliseconds(100),
            RetryInterval = TimeSpan.FromMilliseconds(10),
            AutoRenew = false,
        };

        await using var r1 = await locker.AcquireSharedAsync("k", opts);
        await using var r2 = await locker.AcquireSharedAsync("k", opts);
        Assert.Equal("k", r1.Key);
        Assert.Equal("k", r2.Key);

        // Writer cannot get in while readers are held.
        Assert.Null(await locker.TryAcquireExclusiveAsync("k", opts));
    }

    [Fact]
    public void UseInMemory_StillRegistersExclusiveProvider()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<InMemoryLockProvider>(sp.GetRequiredService<IDistributedLockProvider>());
    }
}
