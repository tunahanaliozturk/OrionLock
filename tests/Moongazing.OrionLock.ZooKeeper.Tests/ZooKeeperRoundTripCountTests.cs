using Moq;

namespace Moongazing.OrionLock.ZooKeeper.Tests;

/// <summary>
/// Pins the number of ZooKeeper client calls one acquire attempt costs.
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
public sealed class ZooKeeperRoundTripCountTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private const string Key = "round-trip-key";
    private const string Parent = "/orionlock/round-trip-key";

    [Fact]
    public async Task Failed_acquire_costs_four_zookeeper_calls()
    {
        // Another waiter already owns lock-0000000001, so our lock-0000000002 is not the lowest
        // sequence number and the attempt loses.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            Children = ["lock-0000000001", "lock-0000000002"],
        };
        var sut = new ZooKeeperLockProvider(zk);

        var acquired = await sut.TryAcquireAsync(Key, "owner-1", Lease, CancellationToken.None);

        Assert.False(acquired);

        // 1. EnsurePathAsync   - idempotent recursive create of the lock key's parent znode. Paid on
        //                        every attempt even though the parent almost always already exists.
        Assert.Equal(1, zk.EnsurePathCalls);
        // 2. CreateEphemeralSequentialAsync - our candidate znode. A write to the ensemble, so it
        //                        costs a quorum round trip, and it happens BEFORE we know whether we
        //                        can win.
        Assert.Equal(1, zk.CreateCalls);
        // 3. GetChildrenAsync  - read the whole sibling list to find out we are not the lowest. No
        //                        watch is registered, so the next poll repeats all of this.
        Assert.Equal(1, zk.GetChildrenCalls);
        // 4. DeleteAsync       - remove the candidate znode we just created, otherwise it would
        //                        block the waiters behind us until our session expires.
        Assert.Equal(1, zk.DeleteCalls);

        Assert.Equal(0, zk.ExistsCalls);
        Assert.Equal(4, zk.TotalCalls);
    }

    [Fact]
    public async Task Successful_acquire_costs_three_zookeeper_calls()
    {
        // Our znode is the only child, so it holds the lowest sequence number and wins.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000001",
            Children = ["lock-0000000001"],
        };
        var sut = new ZooKeeperLockProvider(zk);

        var acquired = await sut.TryAcquireAsync(Key, "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);

        // Ensure path + create candidate + read siblings. The winning path skips only the delete,
        // so three of the four calls a failed attempt costs are structural, not cleanup.
        Assert.Equal(1, zk.EnsurePathCalls);
        Assert.Equal(1, zk.CreateCalls);
        Assert.Equal(1, zk.GetChildrenCalls);
        Assert.Equal(0, zk.DeleteCalls);
        Assert.Equal(3, zk.TotalCalls);
    }

    /// <summary>
    /// A hand-written counting fake rather than a <see cref="Mock{T}"/>, so the per-method tallies
    /// read as a ledger and an extra call anywhere shows up as an exact-number mismatch.
    /// </summary>
    private sealed class CountingZooKeeperClient : IZooKeeperClientAdapter
    {
        public string CreatedPath { get; init; } = string.Empty;

        /// <summary>Sibling znode names the ensemble reports; index 0 is the current lock holder.</summary>
        public IReadOnlyList<string> Children { get; init; } = [];

        public int EnsurePathCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int GetChildrenCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int ExistsCalls { get; private set; }

        public int TotalCalls =>
            EnsurePathCalls + CreateCalls + GetChildrenCalls + DeleteCalls + ExistsCalls;

        public Task EnsurePathAsync(string path, CancellationToken cancellationToken)
        {
            EnsurePathCalls++;
            return Task.CompletedTask;
        }

        public Task<string> CreateEphemeralSequentialAsync(
            string parentPath, string childPrefix, byte[] data, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult(CreatedPath);
        }

        public Task<IReadOnlyList<string>> GetChildrenAsync(string parentPath, CancellationToken cancellationToken)
        {
            GetChildrenCalls++;
            return Task.FromResult(Children);
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
        {
            ExistsCalls++;
            return Task.FromResult(true);
        }
    }
}
