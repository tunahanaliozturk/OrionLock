using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class UseInMemoryTests
{
    [Fact]
    public async Task UseInMemory_ShouldRegisterWorkingInMemoryLock()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<InMemoryLockProvider>(sp.GetRequiredService<IDistributedLockProvider>());

        var locker = sp.GetRequiredService<IDistributedLock>();
        await using var h = await locker.AcquireAsync("k", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) });
        Assert.Equal("k", h.Key);
    }
}
