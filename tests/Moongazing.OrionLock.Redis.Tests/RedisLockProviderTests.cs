using Moongazing.OrionLock.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

public sealed class RedisLockProviderTests : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder().Build();
#pragma warning disable CA1859 // Tests intentionally exercise the interface surface used by the provider.
    private IConnectionMultiplexer mux = default!;
#pragma warning restore CA1859

    public async Task InitializeAsync()
        => mux = await RedisContainerStartup.StartAndConnectAsync(container).ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        await mux.DisposeAsync();
        await container.DisposeAsync();
    }

    private RedisLockProvider NewProvider() => new(mux, new RedisLockOptions());

    [Fact]
    public async Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
    {
        var p = NewProvider();
        Assert.True(await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
    {
        var p = NewProvider();
        // Wait (1000ms) comfortably outlasts the Redis PX lease (200ms) - a 5x cushion - so a loaded CI
        // runner cannot probe before the key's TTL has elapsed. Earlier 200ms/400ms left only 2x.
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromMilliseconds(200), default);
        await Task.Delay(1000);
        Assert.True(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(2), default);
        Assert.True(await p.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldOnlyReleaseForOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync("k", "owner-2", default);
        Assert.False(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync("k", "owner-1", default);
        Assert.True(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
    }
}
