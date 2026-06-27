using System.Collections.Concurrent;
using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

/// <summary>
/// v0.6.0: deterministic in-memory facts for the exclusive-lock <c>TryAcquireAsync(key, deadline, ...)</c>
/// overload: success when free, <see langword="null"/> (not throw) on the deadline while contended, success
/// after the holder drains, single-attempt on a non-positive deadline, cancellation propagation, a
/// no-overshoot timing guard, and a stable owner token across retries. The exclusive counterpart of the
/// v0.5.0 reader-writer deadline overload facts.
/// </summary>
public sealed class DistributedLockDeadlineTests
{
    private static DistributedLockOptions Fast() => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(30),
        RetryInterval = TimeSpan.FromMilliseconds(10),
        AutoRenew = false,
    };

    [Fact]
    public async Task Deadline_ReturnsHandle_WhenFree()
    {
        var l = new DistributedLock(new InMemoryLockProvider());
        await using var h = await l.TryAcquireAsync("k", TimeSpan.FromMilliseconds(200), Fast());
        Assert.NotNull(h);
        Assert.Equal("k", h!.Key);
    }

    [Fact]
    public async Task Deadline_ReturnsNull_NotThrow_WhenContendedPastDeadline()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var held = await holder.TryAcquireAsync("k", Fast());
        Assert.NotNull(held);

        var result = await contender.TryAcquireAsync("k", TimeSpan.FromMilliseconds(120), Fast());
        Assert.Null(result);
    }

    [Fact]
    public async Task Deadline_Succeeds_AfterHolderDrains()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        var held = await holder.TryAcquireAsync("k", Fast());
        Assert.NotNull(held);

        var acquire = contender.TryAcquireAsync("k", TimeSpan.FromSeconds(5), Fast());
        await held!.DisposeAsync();

        await using var won = await acquire;
        Assert.NotNull(won);
        Assert.True(won!.IsHeld);
    }

    [Fact]
    public async Task Deadline_NonPositive_PerformsExactlyOneAttempt()
    {
        var recorder = new AttemptRecordingProvider(new InMemoryLockProvider());
        var l = new DistributedLock(recorder);

        // Hold the key so the single attempt fails, then assert exactly one TryAcquire round-trip happened.
        await using var holder = await new DistributedLock(recorder).TryAcquireAsync("k", Fast());
        recorder.Attempts = 0;

        var result = await l.TryAcquireAsync("k", TimeSpan.Zero, Fast());
        Assert.Null(result);
        Assert.Equal(1, recorder.Attempts);
    }

    [Fact]
    public async Task Deadline_Cancellation_Propagates()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var held = await holder.TryAcquireAsync("k", Fast());
        using var cts = new CancellationTokenSource(120);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            contender.TryAcquireAsync("k", TimeSpan.FromSeconds(30), Fast(), cts.Token));
    }

    [Fact]
    public async Task Deadline_DoesNotOvershoot_ByAFullRetryInterval()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var held = await holder.TryAcquireAsync("k", Fast());

        // A 200ms deadline with a 250ms retry interval must NOT wait a full 250ms past the deadline; the
        // delay is clamped to the time left. The bound must stay UNDER deadline + one full interval (450ms)
        // or it would also pass for an unclamped implementation that waited the full extra interval, proving
        // nothing. 400ms gives a loaded runner ~200ms of headroom over the deadline while still excluding a
        // full unclamped overshoot.
        var options = new DistributedLockOptions
        {
            RetryInterval = TimeSpan.FromMilliseconds(250),
            AutoRenew = false,
        };
        var sw = Stopwatch.StartNew();
        var result = await contender.TryAcquireAsync("k", TimeSpan.FromMilliseconds(200), options);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 400, $"deadline overshot: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Deadline_UsesOneStableToken_AcrossAllAttempts()
    {
        var recorder = new TokenRecordingProvider(new InMemoryLockProvider());
        var l = new DistributedLock(recorder);

        // Hold the key so the deadline-acquire must retry several times before giving up.
        await using var holder = await new DistributedLock(recorder).TryAcquireAsync("k", Fast());
        recorder.AcquireTokens.Clear();

        var result = await l.TryAcquireAsync("k", TimeSpan.FromMilliseconds(120), Fast());
        Assert.Null(result);

        Assert.True(recorder.AcquireTokens.Count > 1, "expected the deadline acquire to retry more than once");
        Assert.Single(recorder.AcquireTokens.Distinct());
    }

    private sealed class AttemptRecordingProvider(IDistributedLockProvider inner) : IDistributedLockProvider
    {
        private readonly IDistributedLockProvider inner = inner;

        public int Attempts;

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            return inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken);
        }

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => inner.ReleaseAsync(key, ownerToken, cancellationToken);

        public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;
    }

    private sealed class TokenRecordingProvider(IDistributedLockProvider inner) : IDistributedLockProvider
    {
        private readonly IDistributedLockProvider inner = inner;

        public ConcurrentQueue<string> AcquireTokens { get; } = new();

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            AcquireTokens.Enqueue(ownerToken);
            return inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken);
        }

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => inner.ReleaseAsync(key, ownerToken, cancellationToken);

        public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;
    }
}
