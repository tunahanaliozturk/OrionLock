using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DistributedLockTests
{
    private static DistributedLock NewLock(out InMemoryLockProvider provider)
    {
        provider = new InMemoryLockProvider();
        return new DistributedLock(provider);
    }

    [Fact]
    public async Task TryAcquire_ShouldReturnHandle_WhenFree()
    {
        var l = NewLock(out _);
        await using var h = await l.TryAcquireAsync("k");
        Assert.NotNull(h);
        Assert.Equal("k", h!.Key);
    }

    [Fact]
    public async Task TryAcquire_ShouldReturnNull_WhenHeld()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.TryAcquireAsync("k");
        var second = await contender.TryAcquireAsync("k");
        Assert.Null(second);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenFree()
    {
        var l = NewLock(out _);
        await using var h = await l.AcquireAsync("k", TimeSpan.FromSeconds(30));
        Assert.Equal("k", h.Key);
    }

    [Fact]
    public async Task Acquire_ShouldThrowTimeout_WhenHeldPastWaitTimeout()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() =>
            contender.AcquireAsync("k", new DistributedLockOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(500),
                RetryInterval = TimeSpan.FromMilliseconds(50),
                AutoRenew = false,
            }));
        sw.Stop();
        // Lower bound (400ms, just under the 500ms WaitTimeout) still proves the call did NOT return
        // before the timeout elapsed. The upper bound is widened to 5000ms so a slow, loaded CI runner
        // taking far longer than the budget to notice the timeout cannot fail the test - load only ever
        // makes the observed elapsed larger, never smaller.
        Assert.InRange(sw.ElapsedMilliseconds, 400, 5000);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenLockFreesBeforeWaitTimeout()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        var release = Task.Run(async () => { await Task.Delay(150); await first.DisposeAsync(); });
        await using var second = await contender.AcquireAsync("k", new DistributedLockOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            AutoRenew = false,
        });
        await release;
        Assert.Equal("k", second.Key);
    }

    [Fact]
    public async Task Acquire_ShouldReuseOneOwnerToken_AcrossEveryRetry()
    {
        // The blocking acquire is documented (and the reader-writer lock genuinely behaves) as one
        // logical acquirer polling the backend. A fresh token per attempt makes every retry a different
        // acquirer, which breaks fencing identity and orphans anything a partly-succeeded attempt left
        // under a token no later retry can reclaim.
        var provider = new TokenRecordingProvider(grantOnAttempt: 3);
        var l = new DistributedLock(provider);

        await using var handle = await l.AcquireAsync("k", new DistributedLockOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(30),
            RetryInterval = TimeSpan.FromMilliseconds(10),
            AutoRenew = false,
        });

        Assert.Equal(3, provider.SeenTokens.Count);
        Assert.Single(provider.SeenTokens.Distinct());
    }

    [Fact]
    public async Task Acquire_ShouldNotOvershootWaitTimeout_WhenRetryIntervalIsLonger()
    {
        // A RetryInterval longer than the remaining wait budget used to be slept in full before the
        // deadline was re-checked, so the caller waited up to a whole interval past WaitTimeout.
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() =>
            contender.AcquireAsync("k", new DistributedLockOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(200),
                RetryInterval = TimeSpan.FromSeconds(10),   // far longer than the wait budget
                AutoRenew = false,
            }));
        sw.Stop();

        // Pre-fix this took ~10s (one full RetryInterval). The 5s upper bound is generous enough for a
        // loaded runner yet nowhere near the un-clamped 10s sleep.
        Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
    }

    /// <summary>Records the owner token of every acquire attempt and grants only on the Nth.</summary>
    private sealed class TokenRecordingProvider : Moongazing.OrionLock.Providers.IDistributedLockProvider
    {
        private readonly int grantOnAttempt;
        private readonly List<string> seen = [];

        public TokenRecordingProvider(int grantOnAttempt) => this.grantOnAttempt = grantOnAttempt;

        public IReadOnlyList<string> SeenTokens
        {
            get { lock (seen) { return [.. seen]; } }
        }

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            lock (seen)
            {
                seen.Add(o);
                return Task.FromResult(seen.Count >= grantOnAttempt);
            }
        }

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task ReleaseAsync(string k, string o, CancellationToken c) => Task.CompletedTask;
    }

    [Fact]
    public async Task Acquire_ShouldThrowOperationCanceled_WhenTokenCancelled()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            contender.AcquireAsync("k", new DistributedLockOptions { WaitTimeout = TimeSpan.FromSeconds(30) }, cts.Token));
    }
}
