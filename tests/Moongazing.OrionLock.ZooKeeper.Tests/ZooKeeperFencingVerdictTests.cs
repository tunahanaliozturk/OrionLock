using Moongazing.OrionLock.ZooKeeper;
using Moongazing.OrionLock.Providers;
using Moq;

namespace Moongazing.OrionLock.ZooKeeper.Tests;

/// <summary>
/// The verdict that ZooKeeper cannot fence here, pinned so a later change cannot quietly start handing
/// out the sequence number. Both numbers on offer - the sequential znode's suffix and the parent's
/// cversion - come from the parent, and this provider deletes the parent once its last child is gone.
/// </summary>
public sealed class ZooKeeperFencingVerdictTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Two_acquisitions_separated_by_a_parent_prune_see_the_same_sequence_number()
    {
        // This is the reason, reproduced: acquire, release (which prunes the now-empty parent), acquire
        // again. ZooKeeper restarts the child counter with the recreated parent, so both holders are
        // called lock-0000000000. A token minted from that suffix would be the same number twice - two
        // holders the resource has no way to order, which is the exact failure fencing exists to stop.
        var adapter = new Mock<IZooKeeperClientAdapter>();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                "/orionlock/k", "lock-", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/orionlock/k/lock-0000000000");
        adapter.Setup(a => a.GetChildrenAsync("/orionlock/k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000000" });
        adapter.Setup(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Typed as the interface on purpose: the provider does not override the fenced acquire, so this
        // is the interface default answering - which is exactly the behaviour being pinned.
        IDistributedLockProvider sut = new ZooKeeperLockProvider(adapter.Object);

        var first = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);
        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);
        var second = await sut.TryAcquireFencedAsync("k", "owner-2", Lease, CancellationToken.None);

        Assert.True(first.Acquired);
        Assert.True(second.Acquired);
        Assert.Null(first.FencingToken);
        Assert.Null(second.FencingToken);
    }

    [Fact]
    public async Task A_held_lock_reports_no_token_rather_than_a_number_that_repeats()
    {
        var adapter = new Mock<IZooKeeperClientAdapter>();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/orionlock/k/lock-0000000042");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000042" });
        // Typed as the interface on purpose: the provider does not override the fenced acquire, so this
        // is the interface default answering - which is exactly the behaviour being pinned.
        IDistributedLockProvider sut = new ZooKeeperLockProvider(adapter.Object);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
    }
}
