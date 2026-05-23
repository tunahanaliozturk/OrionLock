using Moongazing.OrionLock;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Tests;

public class DistributedLockHandleTests
{
    private sealed class FakeProvider : IDistributedLockProvider
    {
        public volatile bool RenewSucceeds = true;
        public int RenewCount;
        public int ReleaseCount;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            Interlocked.Increment(ref RenewCount);
            return Task.FromResult(RenewSucceeds);
        }

        public Task ReleaseAsync(string k, string o, CancellationToken c)
        {
            Interlocked.Increment(ref ReleaseCount);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Handle_ShouldExposeKeyAndBeHeld_OnCreation()
    {
        var p = new FakeProvider();
        await using var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { AutoRenew = false });
        Assert.Equal("k", h.Key);
        Assert.True(h.IsHeld);
        Assert.False(h.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Watchdog_ShouldRenew_WhileLeaseHeld()
    {
        var p = new FakeProvider();
        await using (new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(150), AutoRenew = true }))
        {
            await Task.Delay(400);
        }
        Assert.True(p.RenewCount >= 2);
    }

    [Fact]
    public async Task Watchdog_ShouldFlipIsHeldAndTripLostToken_WhenRenewalFails()
    {
        var p = new FakeProvider { RenewSucceeds = false };
        await using var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(150), AutoRenew = true });

        var lost = new TaskCompletionSource();
        h.LostToken.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(h.IsHeld);
        Assert.True(h.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_ShouldReleaseAndStopWatchdog()
    {
        var p = new FakeProvider();
        var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(150), AutoRenew = true });
        await h.DisposeAsync();

        Assert.Equal(1, p.ReleaseCount);
        Assert.False(h.IsHeld);
        var renewsAfterDispose = p.RenewCount;
        await Task.Delay(200);
        Assert.Equal(renewsAfterDispose, p.RenewCount);
    }

    [Fact]
    public async Task Dispose_ShouldBeIdempotent()
    {
        var p = new FakeProvider();
        var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { AutoRenew = false });
        await h.DisposeAsync();
        await h.DisposeAsync();
        Assert.Equal(1, p.ReleaseCount);
    }
}
