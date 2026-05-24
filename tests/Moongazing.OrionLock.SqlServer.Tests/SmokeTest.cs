using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.SqlServer;

namespace Moongazing.OrionLock.SqlServer.Tests;

public sealed class SmokeTest : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture fx;

    public SmokeTest(SqlServerContainerFixture fx) => this.fx = fx;

    [Fact]
    public async Task AddOrionLock_UseSqlServer_AcquireDispose_Works()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseSqlServer(fx.ConnectionString);
        await using var sp = services.BuildServiceProvider();

        var locker = sp.GetRequiredService<IDistributedLock>();
        var key = $"smoke-{Guid.NewGuid():N}";

        await using (var handle = await locker.AcquireAsync(key, new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromSeconds(30)
        }))
        {
            Assert.True(handle.IsHeld);
        }

        // After dispose, another acquire must succeed.
        await using var second = await locker.AcquireAsync(key, new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromSeconds(30)
        });
        Assert.True(second.IsHeld);
    }

    [Fact]
    public async Task TwoConsumers_AcquireSerialise()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseSqlServer(fx.ConnectionString);
        await using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();
        var key = $"smoke-{Guid.NewGuid():N}";

        await using var first = await locker.AcquireAsync(key, new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromSeconds(30)
        });

        // Try-acquire from the *same* IDistributedLock instance is intercepted by
        // in-process reentrancy and would return a nested handle. Build a *second*
        // service provider to simulate a separate consumer.
        var services2 = new ServiceCollection();
        services2.AddOrionLock().UseSqlServer(fx.ConnectionString);
        await using var sp2 = services2.BuildServiceProvider();
        var locker2 = sp2.GetRequiredService<IDistributedLock>();

        var blocked = await locker2.TryAcquireAsync(key, new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30)
        });
        Assert.Null(blocked);
    }
}
