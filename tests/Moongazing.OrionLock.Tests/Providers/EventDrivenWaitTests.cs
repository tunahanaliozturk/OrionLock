namespace Moongazing.OrionLock.Tests.Providers;

using System.Collections.Concurrent;
using System.Diagnostics;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;
using Xunit;

/// <summary>
/// v2.1: the core asks the BACKEND to wait instead of asking it again every RetryInterval. These
/// are the contract facts that every backend override below depends on - that the default is still
/// the old poll loop, that an override is actually reached, that the wait budget, cancellation and
/// the deadline overload are unchanged, and that a wait which ends early degrades to polling
/// instead of failing the caller.
/// </summary>
public sealed class EventDrivenWaitTests
{
    private static DistributedLockOptions Fast(TimeSpan? wait = null) => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(30),
        WaitTimeout = wait ?? TimeSpan.FromSeconds(5),
        RetryInterval = TimeSpan.FromMilliseconds(10),
        AutoRenew = false,
    };

    [Fact]
    public async Task A_provider_that_does_not_override_still_polls_exactly_as_before()
    {
        // The whole non-breaking claim in one assertion: this provider knows nothing about the new
        // member, and the blocking acquire still reaches it by repeated TryAcquireAsync.
        var provider = new PollOnlyProvider(grantOnAttempt: 4);
        var sut = new DistributedLock(provider);

        await using var handle = await sut.AcquireAsync("k", Fast());

        // Four TryAcquireAsync calls through one blocking acquire IS the default poll loop
        // running: this provider has no wait of its own for the core to have used instead.
        Assert.Equal(4, provider.Attempts);
    }

    [Fact]
    public async Task An_overriding_provider_gets_the_whole_wait_in_one_call()
    {
        // One refused attempt, then ONE wait call that blocks until the lock frees. That is the
        // 4.08-provider-calls-per-waiter figure collapsing to 2.
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromMilliseconds(40));
        var sut = new DistributedLock(provider);

        await using var handle = await sut.AcquireAsync("k", Fast());

        Assert.Equal(1, provider.Attempts);
        Assert.Equal(1, provider.WaitCalls);
    }

    [Fact]
    public async Task The_wait_receives_the_remaining_budget_not_the_whole_timeout()
    {
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromMilliseconds(20));
        var sut = new DistributedLock(provider);

        await using var handle = await sut.AcquireAsync("k", Fast(TimeSpan.FromSeconds(3)));

        var budget = Assert.Single(provider.SeenMaxWaits);
        Assert.True(budget > TimeSpan.Zero, "the wait must get a positive budget");
        Assert.True(budget <= TimeSpan.FromSeconds(3), $"the wait must not exceed WaitTimeout, got {budget}");
    }

    [Fact]
    public async Task The_wait_receives_the_callers_retry_interval_as_the_policy_floor()
    {
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromMilliseconds(10));
        var sut = new DistributedLock(provider);

        var options = Fast();
        options.RetryBackoffCeiling = TimeSpan.FromSeconds(2);
        await using var handle = await sut.AcquireAsync("k", options);

        var policy = Assert.Single(provider.SeenPolicies);
        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.RetryInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.BackoffCeiling);
    }

    [Fact]
    public async Task A_wait_that_ends_early_falls_back_instead_of_failing_the_caller()
    {
        // A dropped subscription is reported as "false before the budget elapsed". The caller must
        // not see that as a timeout - the core re-checks the budget and waits again.
        var provider = new FlakyWaitProvider(dropsBeforeGranting: 3);
        var sut = new DistributedLock(provider);

        await using var handle = await sut.AcquireAsync("k", Fast());

        Assert.Equal(4, provider.WaitCalls);
        Assert.True(handle.IsHeld);
    }

    [Fact]
    public async Task A_wait_that_never_grants_still_times_out_at_WaitTimeout()
    {
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromHours(1));
        var sut = new DistributedLock(provider);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(
            () => sut.AcquireAsync("k", Fast(TimeSpan.FromMilliseconds(200))));
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 150, 3000);
    }

    [Fact]
    public async Task A_wait_that_never_grants_still_observes_cancellation()
    {
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromHours(1));
        var sut = new DistributedLock(provider);

        using var cts = new CancellationTokenSource(120);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.AcquireAsync("k", Fast(TimeSpan.FromSeconds(30)), cts.Token));
    }

    [Fact]
    public async Task The_deadline_overload_waits_through_the_backend_too()
    {
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromMilliseconds(40));
        var sut = new DistributedLock(provider);

        await using var handle = await sut.TryAcquireAsync("k", TimeSpan.FromSeconds(5), Fast());

        Assert.NotNull(handle);
        Assert.Equal(1, provider.WaitCalls);
    }

    [Fact]
    public async Task The_deadline_overload_still_returns_null_rather_than_throwing()
    {
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromHours(1));
        var sut = new DistributedLock(provider);

        var handle = await sut.TryAcquireAsync("k", TimeSpan.FromMilliseconds(150), Fast());

        Assert.Null(handle);
    }

    [Fact]
    public async Task A_granted_wait_produces_a_working_handle_without_a_second_acquire()
    {
        // Collecting the lease with another TryAcquireAsync would both cost a round trip and fail,
        // because the lock is already held - by us.
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromMilliseconds(20));
        var sut = new DistributedLock(provider);

        var handle = await sut.AcquireAsync("k", Fast());

        Assert.True(handle.IsHeld);
        Assert.Equal(1, provider.Attempts);
        await handle.DisposeAsync();
        Assert.Equal(1, provider.Releases);
    }

    [Fact]
    public async Task A_lock_taken_by_waiting_still_carries_its_fencing_token()
    {
        // A wait reports a LockAcquisition, not a bool, precisely so this holds. A bool would mint
        // no token for any CONTENDED acquire, so fencing would work on an idle key and go dark
        // under exactly the contention it exists to protect against.
        var provider = new BlockingProvider(grantAfter: TimeSpan.FromMilliseconds(20));
        var sut = new DistributedLock(provider);

        await using var handle = await sut.AcquireAsync("k", Fast());

        Assert.Equal(BlockingProvider.WaitingToken, handle.FencingToken);
    }

    [Fact]
    public async Task The_in_memory_provider_parks_waiters_instead_of_spinning_them()
    {
        // The Docker-free version of the contention benchmark, run twice over the SAME provider:
        // once with the event-driven wait reachable, once with it hidden behind a decorator that
        // does not forward it, so the default poll runs instead. The counted number is the backend
        // round trips the queue drain costs AFTER every waiter is already parked.
        var parked = await DrainWithThirtyTwoWaitersAsync(forwardTheWait: true);
        var polled = await DrainWithThirtyTwoWaitersAsync(forwardTheWait: false);

        // A parked waiter costs its ONE refused attempt and nothing more: every later hand-off
        // happens inside the wait the backend is already holding open.
        Assert.True(parked <= 36, $"parked waiters should cost about one call each, got {parked}");
        // The poll path pays for every waiter on every tick of the whole drain. The exact multiple
        // depends on how fast the runner drains the queue, so the claim is relative: polling must
        // cost materially more than parking for the same 32 waiters on the same provider.
        Assert.True(polled > parked * 2, $"polling cost {polled} calls, parking cost {parked} - expected polling to cost far more");
    }

    private static async Task<int> DrainWithThirtyTwoWaitersAsync(bool forwardTheWait)
    {
        const int waiters = 32;
        var counting = new CountingProvider(new InMemoryLockProvider(), forwardTheWait);
        var gate = new DistributedLock(counting);
        var options = Fast(TimeSpan.FromSeconds(30));

        var held = await gate.TryAcquireAsync("k", options);
        Assert.NotNull(held);

        // Per-waiter barrier, for the reason the contention benchmark spells out: an aggregate
        // count cannot tell 32 waiters that each failed once from one fast waiter that failed 32
        // times, so the gate would open while some callers were still queued on the thread pool
        // and their first attempt would land in the middle of the measurement.
        var atTheGate = new CountdownEvent(waiters);
        var tasks = new Task[waiters];
        for (var i = 0; i < waiters; i++)
        {
            var waiter = new DistributedLock(counting);
            tasks[i] = Task.Run(async () =>
            {
                var probe = await waiter.TryAcquireAsync("k", options);
                Assert.Null(probe);
                atTheGate.Signal();
                await using var h = await waiter.AcquireAsync("k", options);
            });
        }

        Assert.True(atTheGate.Wait(TimeSpan.FromSeconds(20)), "the waiters never all reached the backend");
        counting.Reset();
        await held!.DisposeAsync();
        await Task.WhenAll(tasks);
        return counting.Attempts;
    }

    /// <summary>Grants on the Nth <c>TryAcquireAsync</c> and does NOT override the wait.</summary>
    private sealed class PollOnlyProvider(int grantOnAttempt) : IDistributedLockProvider
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(Interlocked.Increment(ref attempts) >= grantOnAttempt);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>Refuses every <c>TryAcquireAsync</c> and grants from inside the wait after a delay.</summary>
    private sealed class BlockingProvider(TimeSpan grantAfter) : IDistributedLockProvider
    {
        private int attempts;
        private int waitCalls;
        private int releases;

        public int Attempts => Volatile.Read(ref attempts);
        public int WaitCalls => Volatile.Read(ref waitCalls);
        public int Releases => Volatile.Read(ref releases);
        public ConcurrentQueue<TimeSpan> SeenMaxWaits { get; } = new();
        public ConcurrentQueue<LockWaitPolicy> SeenPolicies { get; } = new();

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(false);
        }

        /// <summary>The token this fake mints for a wait that wins.</summary>
        public const long WaitingToken = 77;

        public async Task<LockAcquisition> WaitForAcquireAsync(
            string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
            LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref waitCalls);
            SeenMaxWaits.Enqueue(maxWait);
            SeenPolicies.Enqueue(waitPolicy);

            var block = grantAfter < maxWait ? grantAfter : maxWait;
            await Task.Delay(block, cancellationToken).ConfigureAwait(false);
            return grantAfter <= maxWait ? LockAcquisition.Fenced(WaitingToken) : LockAcquisition.NotAcquired;
        }

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref releases);
            return Task.CompletedTask;
        }
    }

    /// <summary>Models a subscription that drops: returns false early N times, then grants.</summary>
    private sealed class FlakyWaitProvider(int dropsBeforeGranting) : IDistributedLockProvider
    {
        private int waitCalls;

        public int WaitCalls => Volatile.Read(ref waitCalls);

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<LockAcquisition> WaitForAcquireAsync(
            string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
            LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
            => Task.FromResult(Interlocked.Increment(ref waitCalls) > dropsBeforeGranting
                ? LockAcquisition.Unfenced
                : LockAcquisition.NotAcquired);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Counts attempts. <paramref name="forwardTheWait"/> false reproduces the decorator bug this
    /// codebase has already shipped once: the inner provider's wait is unreachable and every waiter
    /// falls back to the default poll loop.
    /// </summary>
    private sealed class CountingProvider(IDistributedLockProvider inner, bool forwardTheWait) : IDistributedLockProvider
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public void Reset() => Volatile.Write(ref attempts, 0);

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            return inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken);
        }

        public async Task<LockAcquisition> WaitForAcquireAsync(
            string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
            LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
            => forwardTheWait
                ? await inner.WaitForAcquireAsync(key, ownerToken, leaseDuration, maxWait, waitPolicy, cancellationToken)
                    .ConfigureAwait(false)
                : await DistributedLockProviderExtensions.WaitForAcquireAsync(
                    this, key, ownerToken, leaseDuration, maxWait,
                    new WaitForAcquireOptions
                    {
                        InitialDelay = waitPolicy.RetryInterval,
                        MaxDelay = waitPolicy.BackoffCeiling ?? waitPolicy.RetryInterval,
                    },
                    cancellationToken).ConfigureAwait(false)
                    ? LockAcquisition.Unfenced
                    : LockAcquisition.NotAcquired;

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => inner.ReleaseAsync(key, ownerToken, cancellationToken);

        public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;
    }
}
