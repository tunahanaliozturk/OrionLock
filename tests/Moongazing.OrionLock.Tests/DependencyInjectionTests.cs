using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddOrionLock_ShouldRegister_IDistributedLock_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddOrionLock();
        services.AddSingleton<IDistributedLockProvider, InMemoryLockProvider>();

        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<IDistributedLock>();
        var b = sp.GetRequiredService<IDistributedLock>();

        Assert.IsType<DistributedLock>(a);
        Assert.Same(a, b);
    }

    [Fact]
    public void AddOrionLock_ShouldReturnBuilder_ExposingServices()
    {
        var services = new ServiceCollection();
        var builder = services.AddOrionLock();
        Assert.Same(services, builder.Services);
    }

    [Fact]
    public async Task ResolvedLock_ShouldFunction_OverRegisteredProvider()
    {
        var services = new ServiceCollection();
        services.AddOrionLock();
        services.AddSingleton<IDistributedLockProvider, InMemoryLockProvider>();

        using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();

        await using var h = await locker.AcquireAsync("k", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) });
        Assert.Equal("k", h.Key);
    }
}
