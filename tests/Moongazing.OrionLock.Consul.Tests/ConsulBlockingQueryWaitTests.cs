using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Consul.Tests;

/// <summary>
/// v2.1: a contended Consul waiter parks on a blocking query instead of re-attempting the acquire
/// every retry interval. A failed attempt costs THREE round trips here - create session, KV
/// acquire, destroy session - so the poll loop was paying three per waiter per tick.
/// </summary>
/// <remarks>
/// The counting fake is the same model <see cref="ConsulRoundTripCountTests"/> uses: a hand-written
/// adapter whose per-method tallies read as a ledger, so an extra call anywhere shows up as an
/// exact-number mismatch.
/// </remarks>
public sealed class ConsulBlockingQueryWaitTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private const string Key = "blocking-query-key";

    [Fact]
    public async Task A_waiter_blocks_once_and_takes_the_lock_when_the_key_comes_back_free()
    {
        var consul = new CountingConsulClient { GrantOnAttempt = 2, QueryAnswers = [true] };
        var sut = new ConsulLockProvider(consul);

        var acquired = await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        Assert.True(acquired.Acquired);
        Assert.Equal(1, consul.BlockingQueryCalls);
        // Two attempts: the losing one (create + acquire + destroy) and the winning one
        // (create + acquire).
        Assert.Equal(2, consul.CreateSessionCalls);
        Assert.Equal(2, consul.AcquireCalls);
        Assert.Equal(1, consul.DestroySessionCalls);
        Assert.Equal(6, consul.TotalCalls);
    }

    [Fact]
    public async Task A_waiter_stays_parked_however_many_hand_offs_it_loses()
    {
        var consul = new CountingConsulClient { GrantOnAttempt = 4, QueryAnswers = [true, true, true] };
        var sut = new ConsulLockProvider(consul);

        Assert.True((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(3, consul.BlockingQueryCalls);
        Assert.Equal(4, consul.AcquireCalls);
    }

    [Fact]
    public async Task A_query_that_comes_back_with_the_key_still_held_hands_the_wait_back()
    {
        // False is the contract's "I could not do this for you": the core sleeps the caller's retry
        // floor and asks again rather than this method spinning the query.
        var consul = new CountingConsulClient { GrantOnAttempt = 99, QueryAnswers = [false] };
        var sut = new ConsulLockProvider(consul);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(1, consul.BlockingQueryCalls);
        Assert.Equal(1, consul.AcquireCalls);
    }

    [Fact]
    public async Task An_adapter_with_no_blocking_query_falls_straight_back_to_polling()
    {
        var consul = new QuerylessConsulClient();
        var sut = new ConsulLockProvider(consul);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);
        Assert.Equal(1, consul.AcquireCalls);
    }

    [Fact]
    public async Task A_spent_budget_is_reported_without_opening_a_query()
    {
        var consul = new CountingConsulClient { GrantOnAttempt = 99, QueryAnswers = [] };
        var sut = new ConsulLockProvider(consul);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.Zero, LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(1, consul.AcquireCalls);
        Assert.Equal(0, consul.BlockingQueryCalls);
    }

    [Fact]
    public async Task The_query_is_handed_the_prefixed_key_and_the_remaining_budget()
    {
        var consul = new CountingConsulClient { GrantOnAttempt = 2, QueryAnswers = [true] };
        var sut = new ConsulLockProvider(consul, new ConsulLockOptions { KeyPrefix = "app/locks/" });

        Assert.True((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(9), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal("app/locks/" + Key, consul.QueriedKey);
        Assert.True(consul.QueriedBudget > TimeSpan.Zero && consul.QueriedBudget <= TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task A_cancelled_waiter_surfaces_the_cancellation_rather_than_a_false()
    {
        var consul = new CountingConsulClient { GrantOnAttempt = 99, QueryAnswers = [], CancelInsteadOfQuerying = true };
        var sut = new ConsulLockProvider(consul);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));
    }

    [Fact]
    public async Task The_wait_validates_its_key_and_owner_exactly_as_the_single_shot_try_does()
    {
        var sut = new ConsulLockProvider(new QuerylessConsulClient());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            "", "owner-1", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            Key, " ", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
    }

    /// <summary>A ledger of Consul calls, granting the key on the Nth attempt.</summary>
    private sealed class CountingConsulClient : IConsulClientAdapter
    {
        private int attempts;
        private int queryAnswerIndex;

        public int GrantOnAttempt { get; init; } = 1;

        /// <summary>What each successive blocking query reports. Exhausted answers report false.</summary>
        public IReadOnlyList<bool> QueryAnswers { get; init; } = [];

        public bool CancelInsteadOfQuerying { get; init; }

        public int CreateSessionCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int DestroySessionCalls { get; private set; }
        public int BlockingQueryCalls { get; private set; }
        public string? QueriedKey { get; private set; }
        public TimeSpan QueriedBudget { get; private set; }

        public int TotalCalls => CreateSessionCalls + AcquireCalls + DestroySessionCalls + BlockingQueryCalls;

        public Task<string> CreateSessionAsync(TimeSpan ttl, string behavior, TimeSpan lockDelay, CancellationToken cancellationToken)
        {
            CreateSessionCalls++;
            return Task.FromResult($"session-{CreateSessionCalls}");
        }

        public Task<bool> KvAcquireAsync(string key, string ownerToken, string sessionId, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return Task.FromResult(++attempts >= GrantOnAttempt);
        }

        public Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            DestroySessionCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> WaitForKeyFreeAsync(string key, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            BlockingQueryCalls++;
            QueriedKey = key;
            QueriedBudget = maxWait;
            if (CancelInsteadOfQuerying)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var answer = queryAnswerIndex < QueryAnswers.Count && QueryAnswers[queryAnswerIndex];
            queryAnswerIndex++;
            return Task.FromResult(answer);
        }

        public Task<bool> RenewSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>An adapter written before blocking queries: it does not implement the new member.</summary>
    private sealed class QuerylessConsulClient : IConsulClientAdapter
    {
        public int AcquireCalls { get; private set; }

        public Task<string> CreateSessionAsync(TimeSpan ttl, string behavior, TimeSpan lockDelay, CancellationToken cancellationToken)
            => Task.FromResult("session-1");

        public Task<bool> KvAcquireAsync(string key, string ownerToken, string sessionId, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return Task.FromResult(false);
        }

        public Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RenewSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
