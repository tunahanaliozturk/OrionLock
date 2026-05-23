using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class ReentrancyTests
{
    [Fact]
    public async Task ReAcquire_SameKey_ShouldNotTouchBackend_AndShouldSucceed()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        await using var inner = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal("k", inner.Key);
        Assert.Equal(1, provider.AcquireCount);
    }

    [Fact]
    public async Task OuterDispose_ShouldReleaseBackend_OnlyAfterInnerDisposed()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var inner = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        await inner.DisposeAsync();
        Assert.Equal(0, provider.ReleaseCount);

        await outer.DisposeAsync();
        Assert.Equal(1, provider.ReleaseCount);
    }

    [Fact]
    public async Task TryAcquire_DifferentKey_ShouldHitBackend()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var a = await l.AcquireAsync("k1", new DistributedLockOptions { AutoRenew = false });
        await using var b = await l.AcquireAsync("k2", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal(2, provider.AcquireCount);
    }

    [Fact]
    public async Task ReAcquire_AfterFullRelease_ShouldHitBackendAgain()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await (await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false })).DisposeAsync();
        await (await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false })).DisposeAsync();

        Assert.Equal(2, provider.AcquireCount);
    }

    private sealed class CountingProvider : Moongazing.OrionLock.Providers.IDistributedLockProvider
    {
        public int AcquireCount;
        public int ReleaseCount;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            Interlocked.Increment(ref AcquireCount);
            return Task.FromResult(true);
        }

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task ReleaseAsync(string k, string o, CancellationToken c)
        {
            Interlocked.Increment(ref ReleaseCount);
            return Task.CompletedTask;
        }
    }
}
