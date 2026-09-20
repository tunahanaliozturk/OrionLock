using Moq;

namespace Moongazing.OrionLock.Consul.Tests;

/// <summary>
/// Pins the number of Consul client calls one acquire attempt costs.
/// </summary>
/// <remarks>
/// <para>
/// This is a performance invariant expressed as a test rather than as a benchmark number, because a
/// benchmark number is something a human has to notice in a table and a failing test is not. Every
/// blocking <c>AcquireAsync</c> poll against a contended key pays the FAILED-attempt count below,
/// so it is the figure that multiplies by the waiter count and the poll rate under contention.
/// </para>
/// <para>
/// These counts are a BASELINE captured before the backend performance work. They are not a
/// statement that the current numbers are correct - they are a statement of what the numbers are
/// today, so a change that improves or regresses them cannot land silently. When the backend work
/// reduces a count, update the expected value here in the same commit and say why.
/// </para>
/// </remarks>
public sealed class ConsulRoundTripCountTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private const string Key = "round-trip-key";

    [Fact]
    public async Task Failed_acquire_costs_three_consul_calls()
    {
        var consul = new CountingConsulClient { KvAcquireResult = false };
        var sut = new ConsulLockProvider(consul);

        var acquired = await sut.TryAcquireAsync(Key, "owner-1", Lease, CancellationToken.None);

        Assert.False(acquired);

        // 1. CreateSessionAsync  - a fresh Consul session per attempt, created BEFORE we know whether
        //                          the key is free. This is a write to the Consul servers, so it costs
        //                          a Raft round trip on every poll of a contended key.
        Assert.Equal(1, consul.CreateSessionCalls);
        // 2. KvAcquireAsync      - the session-scoped KV acquire that actually answers the question.
        //                          The only call of the three that is doing the lock's real work.
        Assert.Equal(1, consul.KvAcquireCalls);
        // 3. DestroySessionAsync - tear the orphan session back down, otherwise a contended poll loop
        //                          would leak one session per attempt until each TTL elapsed.
        Assert.Equal(1, consul.DestroySessionCalls);

        Assert.Equal(0, consul.KvReleaseCalls);
        Assert.Equal(0, consul.RenewSessionCalls);
        Assert.Equal(3, consul.TotalCalls);
    }

    [Fact]
    public async Task Successful_acquire_costs_two_consul_calls()
    {
        var consul = new CountingConsulClient { KvAcquireResult = true };
        var sut = new ConsulLockProvider(consul);

        var acquired = await sut.TryAcquireAsync(Key, "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);

        // Create the session, then take the key with it. The winning path keeps the session alive,
        // so only the destroy is skipped: two of the three calls are structural, not cleanup.
        Assert.Equal(1, consul.CreateSessionCalls);
        Assert.Equal(1, consul.KvAcquireCalls);
        Assert.Equal(0, consul.DestroySessionCalls);
        Assert.Equal(2, consul.TotalCalls);
    }

    /// <summary>
    /// A hand-written counting fake rather than a <see cref="Mock{T}"/>, so the per-method tallies
    /// read as a ledger and an extra call anywhere shows up as an exact-number mismatch.
    /// </summary>
    private sealed class CountingConsulClient : IConsulClientAdapter
    {
        /// <summary>What the KV acquire reports: false models a key another session already holds.</summary>
        public bool KvAcquireResult { get; init; }

        public int CreateSessionCalls { get; private set; }
        public int RenewSessionCalls { get; private set; }
        public int DestroySessionCalls { get; private set; }
        public int KvAcquireCalls { get; private set; }
        public int KvReleaseCalls { get; private set; }

        public int TotalCalls =>
            CreateSessionCalls + RenewSessionCalls + DestroySessionCalls + KvAcquireCalls + KvReleaseCalls;

        public Task<string> CreateSessionAsync(
            TimeSpan ttl, string behavior, TimeSpan lockDelay, CancellationToken cancellationToken)
        {
            CreateSessionCalls++;
            return Task.FromResult("session-1");
        }

        public Task<bool> RenewSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            RenewSessionCalls++;
            return Task.FromResult(true);
        }

        public Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            DestroySessionCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> KvAcquireAsync(
            string key, string ownerToken, string sessionId, CancellationToken cancellationToken)
        {
            KvAcquireCalls++;
            return Task.FromResult(KvAcquireResult);
        }

        public Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken)
        {
            KvReleaseCalls++;
            return Task.FromResult(true);
        }
    }
}
