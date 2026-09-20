using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.ZooKeeper.Tests;

/// <summary>
/// v2.1: the actual ZooKeeper lock recipe. The polling loop created an ephemeral sequential child,
/// listed the siblings, deleted the child and slept - four round trips per tick, and a fresh
/// sequence number every tick, which threw away the FIFO ordering the sequence numbers exist for.
/// The wait creates the child ONCE and watches its immediate predecessor.
/// </summary>
/// <remarks>
/// The counting fake is the same model <see cref="ZooKeeperRoundTripCountTests"/> uses, so the
/// before-and-after is the same ledger: four calls per failed poll there, two per position gained
/// here.
/// </remarks>
public sealed class ZooKeeperPredecessorWatchTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private const string Key = "predecessor-key";
    private const string Parent = "/orionlock/predecessor-key";

    [Fact]
    public void The_predecessor_is_the_child_immediately_ahead_of_us_not_the_lowest()
    {
        // Watching the LOWEST child would wake every waiter on every hand-off - the herd effect the
        // recipe exists to avoid. Each waiter watches only the one directly in front of it.
        IReadOnlyList<string> children = ["lock-0000000001", "lock-0000000002", "lock-0000000003"];

        Assert.True(ZooKeeperLockProvider.TryGetPredecessor(children, "lock-0000000003", out var third));
        Assert.Equal("lock-0000000002", third);
        Assert.True(ZooKeeperLockProvider.TryGetPredecessor(children, "lock-0000000002", out var second));
        Assert.Equal("lock-0000000001", second);
    }

    [Fact]
    public void Being_in_the_list_with_nothing_ahead_of_us_means_we_hold_the_lock()
    {
        IReadOnlyList<string> children = ["lock-0000000001", "lock-0000000002"];

        Assert.True(ZooKeeperLockProvider.TryGetPredecessor(children, "lock-0000000001", out var predecessor));
        Assert.Null(predecessor);
    }

    [Fact]
    public void A_node_that_is_not_in_the_list_is_not_ownership()
    {
        // "Nothing sorts before us" and "we are not there" are the same answer to the narrower
        // question, so a helper that returns only a predecessor reports a vanished node as
        // ownership. An empty list, and a list holding only LATER children, both used to look like
        // "we are first" - and the caller entered its critical section holding no lock at all.
        Assert.False(ZooKeeperLockProvider.TryGetPredecessor([], "lock-0000000001", out var fromEmpty));
        Assert.Null(fromEmpty);

        IReadOnlyList<string> onlyLater = ["lock-0000000005", "lock-0000000006"];
        Assert.False(ZooKeeperLockProvider.TryGetPredecessor(onlyLater, "lock-0000000002", out var fromLater));
        Assert.Null(fromLater);

        IReadOnlyList<string> earlierAndLater = ["lock-0000000001", "lock-0000000005"];
        Assert.False(ZooKeeperLockProvider.TryGetPredecessor(earlierAndLater, "lock-0000000002", out var fromGap));
        Assert.Null(fromGap);
    }

    [Fact]
    public async Task A_waiter_creates_its_child_once_and_watches_the_one_in_front_of_it()
    {
        // Our child is second in line. One create, one listing, one watch, one listing after the
        // watch fires: the child is never deleted and re-created, so our place in the queue is
        // never given up.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            ChildListings =
            [
                ["lock-0000000001", "lock-0000000002"],
                ["lock-0000000002"],
            ],
            WatchAnswers = [true],
        };
        var sut = new ZooKeeperLockProvider(zk);

        var acquired = await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        Assert.True(acquired.Acquired);
        Assert.Equal(1, zk.CreateCalls);
        Assert.Equal(0, zk.DeleteCalls);
        Assert.Equal(2, zk.GetChildrenCalls);
        Assert.Equal(1, zk.WatchCalls);
        Assert.Equal(Parent + "/lock-0000000001", zk.WatchedPath);
    }

    [Fact]
    public async Task A_waiter_that_is_already_first_takes_the_lock_without_watching_anything()
    {
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000001",
            ChildListings = [["lock-0000000001"]],
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.True((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(0, zk.WatchCalls);
        // Ensure path, create, list. Nothing else: the winning path is three calls, same as the
        // old one, and the losing path is where the saving is.
        Assert.Equal(3, zk.TotalCalls);
    }

    [Fact]
    public async Task Each_position_gained_costs_one_listing_and_one_watch()
    {
        // Two waiters ahead of us, so we advance twice. Four calls for the whole wait on top of the
        // ensure-path and the create; the old loop paid four PER TICK for as long as it waited.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000003",
            ChildListings =
            [
                ["lock-0000000001", "lock-0000000002", "lock-0000000003"],
                ["lock-0000000002", "lock-0000000003"],
                ["lock-0000000003"],
            ],
            WatchAnswers = [true, true],
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.True((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(3, zk.GetChildrenCalls);
        Assert.Equal(2, zk.WatchCalls);
        Assert.Equal(1, zk.CreateCalls);
        Assert.Equal(0, zk.DeleteCalls);
    }

    [Fact]
    public async Task A_waiter_whose_budget_runs_out_removes_its_child_before_giving_up()
    {
        // An abandoned child blocks every waiter behind it until the session expires.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            ChildListings = [["lock-0000000001", "lock-0000000002"]],
            WatchAnswers = [false],
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(1, zk.DeleteCalls);
        Assert.Equal(Parent + "/lock-0000000002", zk.DeletedPath);
    }

    [Fact]
    public async Task A_cancelled_waiter_removes_its_child_before_the_cancellation_propagates()
    {
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            ChildListings = [["lock-0000000001", "lock-0000000002"]],
            WatchAnswers = [],
            CancelInsteadOfWatching = true,
        };
        var sut = new ZooKeeperLockProvider(zk);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));

        Assert.Equal(1, zk.DeleteCalls);
    }

    [Fact]
    public async Task An_adapter_with_no_watch_support_removes_its_child_and_hands_the_wait_back()
    {
        var zk = new WatchlessZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            Children = ["lock-0000000001", "lock-0000000002"],
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(1, zk.DeleteCalls);
    }

    [Fact]
    public async Task A_waiter_whose_own_node_vanished_does_not_claim_the_lock()
    {
        // Our node was removed by something other than us - an operator, another client - while we
        // could still list the parent. Every child left sorts AFTER ours, so "no predecessor" is
        // true and used to be read as ownership: the caller went into its critical section with no
        // ZooKeeper lock behind it. Handing the wait back is the only safe answer.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            ChildListings = [["lock-0000000005"]],
        };
        var sut = new ZooKeeperLockProvider(zk);

        var acquired = await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        Assert.False(acquired.Acquired);

        // And nothing was registered, so a later release cannot delete a node we never held.
        await sut.ReleaseAsync(Key, "owner-1", default);
        Assert.Equal(0, zk.DeleteCalls);
    }

    [Fact]
    public async Task An_empty_parent_is_not_ownership_either()
    {
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            ChildListings = [[]],
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);
    }

    [Fact]
    public async Task A_cleanup_delete_that_fails_once_is_retried_rather_than_orphaning_the_node()
    {
        // A node left at the head of the queue blocks this waiter and every later one until the
        // session ends, and the delete most likely to fail is the one issued during the blip that
        // caused the give-up in the first place. One attempt was too few.
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000002",
            ChildListings = [["lock-0000000001", "lock-0000000002"]],
            WatchAnswers = [false],
            DeleteFailures = 1,
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.False((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        Assert.Equal(2, zk.DeleteCalls);
        Assert.True(zk.NodeDeleted, "the waiter's node was orphaned at the head of the queue");
    }

    [Fact]
    public async Task A_won_wait_registers_the_node_so_the_release_deletes_the_right_one()
    {
        var zk = new CountingZooKeeperClient
        {
            CreatedPath = Parent + "/lock-0000000001",
            ChildListings = [["lock-0000000001"]],
        };
        var sut = new ZooKeeperLockProvider(zk);

        Assert.True((await sut.WaitForAcquireAsync(
            Key, "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default)).Acquired);

        await sut.ReleaseAsync(Key, "owner-1", default);

        Assert.Equal(Parent + "/lock-0000000001", zk.DeletedPath);
    }

    [Fact]
    public async Task The_wait_validates_its_key_and_owner_exactly_as_the_single_shot_try_does()
    {
        var sut = new ZooKeeperLockProvider(new WatchlessZooKeeperClient());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            "", "owner-1", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            Key, " ", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
    }

    /// <summary>A ledger of ZooKeeper calls, replaying a scripted sequence of sibling listings.</summary>
    private sealed class CountingZooKeeperClient : IZooKeeperClientAdapter
    {
        private int listingIndex;
        private int watchAnswerIndex;

        public string CreatedPath { get; init; } = string.Empty;

        /// <summary>One sibling listing per <c>GetChildren</c>; the last one repeats.</summary>
        public IReadOnlyList<IReadOnlyList<string>> ChildListings { get; init; } = [];

        /// <summary>What each successive predecessor watch reports. Exhausted answers report false.</summary>
        public IReadOnlyList<bool> WatchAnswers { get; init; } = [];

        public bool CancelInsteadOfWatching { get; init; }

        public int EnsurePathCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int GetChildrenCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int WatchCalls { get; private set; }
        public string? WatchedPath { get; private set; }
        public string? DeletedPath { get; private set; }

        public int TotalCalls => EnsurePathCalls + CreateCalls + GetChildrenCalls + DeleteCalls + WatchCalls;

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
            if (ChildListings.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }
            var listing = ChildListings[Math.Min(listingIndex, ChildListings.Count - 1)];
            listingIndex++;
            return Task.FromResult(listing);
        }

        /// <summary>How many delete attempts fail before one succeeds - a connection blip.</summary>
        public int DeleteFailures { get; init; }

        /// <summary>True once a delete actually landed, as opposed to merely being attempted.</summary>
        public bool NodeDeleted { get; private set; }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (DeleteCalls <= DeleteFailures)
            {
                throw new InvalidOperationException("ZooKeeper is reconnecting.");
            }
            DeletedPath = path;
            NodeDeleted = true;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> WaitForNodeDeletedAsync(string path, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            WatchCalls++;
            WatchedPath = path;
            if (CancelInsteadOfWatching)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var answer = watchAnswerIndex < WatchAnswers.Count && WatchAnswers[watchAnswerIndex];
            watchAnswerIndex++;
            return Task.FromResult(answer);
        }
    }

    /// <summary>An adapter written before watches existed: it does not implement the new member.</summary>
    private sealed class WatchlessZooKeeperClient : IZooKeeperClientAdapter
    {
        public string CreatedPath { get; init; } = string.Empty;

        public IReadOnlyList<string> Children { get; init; } = [];

        public int DeleteCalls { get; private set; }

        public Task EnsurePathAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> CreateEphemeralSequentialAsync(
            string parentPath, string childPrefix, byte[] data, CancellationToken cancellationToken)
            => Task.FromResult(CreatedPath);

        public Task<IReadOnlyList<string>> GetChildrenAsync(string parentPath, CancellationToken cancellationToken)
            => Task.FromResult(Children);

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
