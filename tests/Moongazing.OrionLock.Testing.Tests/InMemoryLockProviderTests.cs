using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class InMemoryLockProviderTests
{
    [Fact]
    public async Task TryAcquire_ShouldSucceed_WhenKeyFree()
    {
        var p = new InMemoryLockProvider();
        Assert.True(await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldFail_WhenKeyHeldByAnother()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.False(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
    {
        var p = new InMemoryLockProvider();
        // The wait must comfortably outlast the lease so a slow, loaded CI runner cannot probe before
        // expiry. A 200ms lease with a 1000ms wait gives a 5x cushion; the absolute durations are large
        // enough that scheduler jitter (tens of ms) is negligible. The earlier 50ms-lease/120ms-wait
        // pairing left only 70ms of slack.
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromMilliseconds(200), default);
        await Task.Delay(1000);
        Assert.True(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldFail_WhenNotOwner()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.False(await p.TryRenewAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
        Assert.True(await p.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldOnlyReleaseForOwner()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync("k", "owner-2", default);   // wrong owner, no-op
        Assert.False(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync("k", "owner-1", default);   // real owner
        Assert.True(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
    }
}
