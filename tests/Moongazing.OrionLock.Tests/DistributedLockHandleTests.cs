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
            // Renewal interval is LeaseDuration/3 (~50ms); a 1000ms hold is ~20 intervals, so the
            // ">= 2 renewals" assertion holds with a wide margin even if a loaded CI runner starves the
            // watchdog timer down to a few ticks. The earlier 400ms hold could drop below 2 under load.
            await Task.Delay(1000);
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
        // Generous ceiling: the lost token fires once the renewal-failure grace period elapses; 10s far
        // exceeds that even on a badly loaded runner, so this never times out spuriously while still
        // failing if the token genuinely never trips.
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));

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
        // Wait long enough (500ms, ~10 renewal intervals for the 150ms lease) that a watchdog timer
        // which failed to stop on dispose WOULD have fired again; the count staying flat then proves the
        // watchdog was truly torn down. A shorter wait could miss a leaked late tick.
        await Task.Delay(500);
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

    [Fact]
    public async Task LostToken_ShouldStillBeReadable_AfterDispose()
    {
        // A `finally` or logging path that reads the handle after `await using` used to get an
        // ObjectDisposedException off CancellationTokenSource.Token.
        var p = new FakeProvider();
        var h = new DistributedLockHandle(p, "k", "owner-1", new DistributedLockOptions { AutoRenew = false });
        await h.DisposeAsync();

        var token = h.LostToken;                     // must not throw
        Assert.False(token.IsCancellationRequested);
        Assert.False(h.IsHeld);
    }

    [Fact]
    public async Task IsHeld_ShouldGoFalse_WhenTheLeaseExpires_WithoutAutoRenew()
    {
        // With AutoRenew off no watchdog runs, so nothing ever noticed the lease running out: IsHeld
        // stayed true forever and LostToken never tripped, even though a TTL backend had expired the
        // key and could already have handed it to someone else.
        var p = new FakeProvider();
        await using var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(100), AutoRenew = false });

        Assert.True(h.IsHeld);

        var lost = new TaskCompletionSource();
        h.LostToken.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(h.IsHeld);
        Assert.True(h.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task IsHeld_ShouldStayTrue_PastTheLease_OnASessionScopedBackend()
    {
        // Session-scoped backends (Postgres advisory locks, SQL Server sp_getapplock) hold for the
        // lifetime of the session regardless of LeaseDuration, so expiring the handle there would be
        // wrong.
        var p = new SessionScopedProvider();
        await using var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(50), AutoRenew = false });

        await Task.Delay(300);

        Assert.True(h.IsHeld);
        Assert.False(h.LostToken.IsCancellationRequested);
    }

    private sealed class SessionScopedProvider : IDistributedLockProvider
    {
        public bool LeaseDurationIsTtl => false;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task ReleaseAsync(string k, string o, CancellationToken c) => Task.CompletedTask;
    }
}
