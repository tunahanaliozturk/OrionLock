using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Etcd.Tests;

/// <summary>
/// v2.1: a contended etcd waiter watches the key instead of re-attempting the acquire every retry
/// interval. A failed attempt costs THREE round trips here - lease grant, transactional put, lease
/// revoke - so the poll loop was paying three per waiter per tick; the watch pays them once and
/// then waits.
/// </summary>
/// <remarks>
/// The counting fake is the same model <see cref="EtcdRoundTripCountTests"/> uses: a hand-written
/// adapter whose per-method tallies read as a ledger, so an extra call anywhere shows up as an
/// exact-number mismatch rather than as a vague slowdown.
/// </remarks>
public sealed class EtcdWatchWaitTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private const string Key = "watch-key";

    [Fact]
    public async Task A_waiter_watches_once_and_takes_the_lock_when_the_key_is_deleted()
    {
        // The key is held, so the first attempt loses. The watch then fires, and the retry wins.
        var etcd = new CountingEtcdClient { GrantOnAttempt = 2, WatchAnswers = [true] };
        var sut = new EtcdLockProvider(etcd);

        var acquired = await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        Assert.True(acquired);
        Assert.Equal(1, etcd.WatchCalls);
        // Two attempts: the losing one (grant + put + revoke) and the winning one (grant + put).
        Assert.Equal(2, etcd.PutCalls);
        Assert.Equal(2, etcd.GrantCalls);
        Assert.Equal(1, etcd.RevokeCalls);
        Assert.Equal(6, etcd.TotalCalls);
    }

    [Fact]
    public async Task A_waiter_stays_parked_on_the_watch_however_long_the_holder_keeps_the_key()
    {
        // Three deletes go by that this waiter loses the race for, then it wins. The cost is one
        // watch per hand-off, NOT one attempt per retry interval for the whole wait.
        var etcd = new CountingEtcdClient { GrantOnAttempt = 4, WatchAnswers = [true, true, true] };
        var sut = new EtcdLockProvider(etcd);

        Assert.True(await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default));

        Assert.Equal(3, etcd.WatchCalls);
        Assert.Equal(4, etcd.PutCalls);
    }

    [Fact]
    public async Task A_watch_that_ends_without_a_delete_hands_the_wait_back_rather_than_spinning()
    {
        // False from the watch is the contract's "I could not do this for you". The provider must
        // return false so the CORE sleeps the caller's retry floor and asks again - the poll loop,
        // reached only when the watch is not doing its job.
        var etcd = new CountingEtcdClient { GrantOnAttempt = 99, WatchAnswers = [false] };
        var sut = new EtcdLockProvider(etcd);

        var acquired = await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        Assert.False(acquired);
        Assert.Equal(1, etcd.WatchCalls);
        Assert.Equal(1, etcd.PutCalls);
    }

    [Fact]
    public async Task An_adapter_with_no_watch_support_falls_straight_back_to_polling()
    {
        // WaitForKeyDeletedAsync is a default interface method answering false, so an adapter
        // written before watches existed keeps working - it simply never parks.
        var etcd = new WatchlessEtcdClient();
        var sut = new EtcdLockProvider(etcd);

        Assert.False(await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default));
        Assert.Equal(1, etcd.PutCalls);
    }

    [Fact]
    public async Task A_spent_budget_is_reported_without_opening_a_watch()
    {
        var etcd = new CountingEtcdClient { GrantOnAttempt = 99, WatchAnswers = [] };
        var sut = new EtcdLockProvider(etcd);

        Assert.False(await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.Zero, LockWaitPolicy.Default, default));

        Assert.Equal(1, etcd.PutCalls);
        Assert.Equal(0, etcd.WatchCalls);
    }

    [Fact]
    public async Task The_watch_is_handed_the_prefixed_key_and_the_remaining_budget()
    {
        var etcd = new CountingEtcdClient { GrantOnAttempt = 2, WatchAnswers = [true] };
        var sut = new EtcdLockProvider(etcd, new EtcdLockOptions { KeyPrefix = "/app/locks/" });

        Assert.True(await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(9), LockWaitPolicy.Default, default));

        Assert.Equal("/app/locks/" + Key, etcd.WatchedKey);
        Assert.True(etcd.WatchedBudget > TimeSpan.Zero && etcd.WatchedBudget <= TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task A_cancelled_waiter_surfaces_the_cancellation_rather_than_a_false()
    {
        var etcd = new CountingEtcdClient { GrantOnAttempt = 99, WatchAnswers = [], CancelInsteadOfWatching = true };
        var sut = new EtcdLockProvider(etcd);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));
    }

    [Fact]
    public async Task The_wait_validates_its_key_and_owner_exactly_as_the_single_shot_try_does()
    {
        var sut = new EtcdLockProvider(new WatchlessEtcdClient());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            "", "owner-1", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            Key, " ", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
    }

    /// <summary>A ledger of etcd calls, granting the key on the Nth attempt.</summary>
    private sealed class CountingEtcdClient : IEtcdClientAdapter
    {
        private int attempts;
        private int watchAnswerIndex;

        /// <summary>The attempt number that finally wins; anything before it is refused.</summary>
        public int GrantOnAttempt { get; init; } = 1;

        /// <summary>What each successive watch reports. Exhausted answers report false.</summary>
        public IReadOnlyList<bool> WatchAnswers { get; init; } = [];

        /// <summary>Makes the watch observe a cancelled token, as a real stream would.</summary>
        public bool CancelInsteadOfWatching { get; init; }

        public int GrantCalls { get; private set; }
        public int PutCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public int WatchCalls { get; private set; }
        public string? WatchedKey { get; private set; }
        public TimeSpan WatchedBudget { get; private set; }

        public int TotalCalls => GrantCalls + PutCalls + RevokeCalls + WatchCalls;

        public Task<long> LeaseGrantAsync(int ttlSeconds, CancellationToken cancellationToken)
        {
            GrantCalls++;
            return Task.FromResult((long)GrantCalls);
        }

        public Task<bool> KvPutIfAbsentAsync(string key, string value, long leaseId, CancellationToken cancellationToken)
        {
            PutCalls++;
            return Task.FromResult(++attempts >= GrantOnAttempt);
        }

        public Task LeaseRevokeAsync(long leaseId, CancellationToken cancellationToken)
        {
            RevokeCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> WaitForKeyDeletedAsync(string key, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            WatchCalls++;
            WatchedKey = key;
            WatchedBudget = maxWait;
            if (CancelInsteadOfWatching)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var answer = watchAnswerIndex < WatchAnswers.Count && WatchAnswers[watchAnswerIndex];
            watchAnswerIndex++;
            return Task.FromResult(answer);
        }

        public Task<bool> LeaseKeepAliveAsync(long leaseId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> KvDeleteIfMatchAsync(string key, string expectedValue, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    /// <summary>An adapter written before watches existed: it does not implement the new member.</summary>
    private sealed class WatchlessEtcdClient : IEtcdClientAdapter
    {
        public int PutCalls { get; private set; }

        public Task<long> LeaseGrantAsync(int ttlSeconds, CancellationToken cancellationToken) => Task.FromResult(1L);

        public Task<bool> KvPutIfAbsentAsync(string key, string value, long leaseId, CancellationToken cancellationToken)
        {
            PutCalls++;
            return Task.FromResult(false);
        }

        public Task LeaseRevokeAsync(long leaseId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> LeaseKeepAliveAsync(long leaseId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> KvDeleteIfMatchAsync(string key, string expectedValue, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
