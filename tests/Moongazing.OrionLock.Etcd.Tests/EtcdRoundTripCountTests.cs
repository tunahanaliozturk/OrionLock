using Moq;

namespace Moongazing.OrionLock.Etcd.Tests;

/// <summary>
/// Pins the number of etcd client calls one acquire attempt costs.
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
public sealed class EtcdRoundTripCountTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private const string Key = "round-trip-key";

    [Fact]
    public async Task Failed_acquire_costs_three_etcd_calls()
    {
        var etcd = new CountingEtcdClient { PutIfAbsentResult = false };
        var sut = new EtcdLockProvider(etcd);

        var acquired = await sut.TryAcquireAsync(Key, "owner-1", Lease, CancellationToken.None);

        Assert.False(acquired);

        // 1. LeaseGrantAsync     - a fresh lease per attempt, granted BEFORE we know whether the key
        //                          is free. etcd leases are Raft-replicated, so this is a cluster
        //                          write on every poll of a contended key.
        Assert.Equal(1, etcd.LeaseGrantCalls);
        // 2. KvPutIfAbsentAsync  - the compare-and-set transaction that actually answers the
        //                          question. The only call of the three doing the lock's real work.
        Assert.Equal(1, etcd.PutIfAbsentCalls);
        // 3. LeaseRevokeAsync    - revoke the orphan lease, otherwise a contended poll loop would
        //                          leak one lease per attempt until each TTL elapsed.
        Assert.Equal(1, etcd.LeaseRevokeCalls);

        Assert.Equal(0, etcd.DeleteIfMatchCalls);
        Assert.Equal(0, etcd.KeepAliveCalls);
        Assert.Equal(3, etcd.TotalCalls);
    }

    [Fact]
    public async Task Successful_acquire_costs_two_etcd_calls()
    {
        var etcd = new CountingEtcdClient { PutIfAbsentResult = true };
        var sut = new EtcdLockProvider(etcd);

        var acquired = await sut.TryAcquireAsync(Key, "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);

        // Grant the lease, then take the key under it. The winning path keeps the lease alive, so
        // only the revoke is skipped: two of the three calls are structural, not cleanup.
        Assert.Equal(1, etcd.LeaseGrantCalls);
        Assert.Equal(1, etcd.PutIfAbsentCalls);
        Assert.Equal(0, etcd.LeaseRevokeCalls);
        Assert.Equal(2, etcd.TotalCalls);
    }

    /// <summary>
    /// A hand-written counting fake rather than a <see cref="Mock{T}"/>, so the per-method tallies
    /// read as a ledger and an extra call anywhere shows up as an exact-number mismatch.
    /// </summary>
    private sealed class CountingEtcdClient : IEtcdClientAdapter
    {
        /// <summary>What the put-if-absent reports: false models a key another lease already holds.</summary>
        public bool PutIfAbsentResult { get; init; }

        public int LeaseGrantCalls { get; private set; }
        public int KeepAliveCalls { get; private set; }
        public int LeaseRevokeCalls { get; private set; }
        public int PutIfAbsentCalls { get; private set; }
        public int DeleteIfMatchCalls { get; private set; }

        public int TotalCalls =>
            LeaseGrantCalls + KeepAliveCalls + LeaseRevokeCalls + PutIfAbsentCalls + DeleteIfMatchCalls;

        public Task<long> LeaseGrantAsync(int ttlSeconds, CancellationToken cancellationToken)
        {
            LeaseGrantCalls++;
            return Task.FromResult(7L);
        }

        public Task<bool> LeaseKeepAliveAsync(long leaseId, CancellationToken cancellationToken)
        {
            KeepAliveCalls++;
            return Task.FromResult(true);
        }

        public Task LeaseRevokeAsync(long leaseId, CancellationToken cancellationToken)
        {
            LeaseRevokeCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> KvPutIfAbsentAsync(
            string key, string value, long leaseId, CancellationToken cancellationToken)
        {
            PutIfAbsentCalls++;
            return Task.FromResult(PutIfAbsentResult);
        }

        public Task<bool> KvDeleteIfMatchAsync(
            string key, string expectedValue, CancellationToken cancellationToken)
        {
            DeleteIfMatchCalls++;
            return Task.FromResult(true);
        }
    }
}
