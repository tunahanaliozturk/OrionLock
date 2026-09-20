using Moongazing.OrionLock.ZooKeeper;
using Moq;

namespace Moongazing.OrionLock.ZooKeeper.Tests;

/// <summary>
/// A lock key used to be concatenated straight into the parent znode path, so a key containing <c>/</c>
/// became a CHAIN of znodes - and every <c>/</c>-separated segment is created as a PERSISTENT znode that
/// release never deletes. ZooKeeper keeps its whole tree in memory, so that grew the ensemble's heap with
/// no path back. These tests pin the flattening and the parent cleanup that replaced it.
/// </summary>
public sealed class ZooKeeperPathTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("plain-key_1", "plain-key_1")]
    [InlineData("a/b", "a~002Fb")]
    [InlineData("../evil", "~002E~002E~002Fevil")]
    [InlineData(".", "~002E")]
    [InlineData("a.b", "a~002Eb")]
    public void Encode_ProducesExactlyOneLegalZnodeName(string key, string expected)
    {
        Assert.Equal(expected, ZooKeeperKeyName.Encode(key));
        Assert.DoesNotContain('/', ZooKeeperKeyName.Encode(key));
    }

    [Fact]
    public async Task ASlashedKey_CreatesOneParentZnode_NotAChainOfThem()
    {
        var adapter = NewAdapter(out var ensured);
        var sut = new ZooKeeperLockProvider(adapter.Object);

        await sut.TryAcquireAsync("tenant-1/orders/47", "owner-1", Lease, CancellationToken.None);

        // Root plus exactly one key segment. Before the flattening this was /orionlock/tenant-1/orders/47
        // - four persistent znodes, three of which nothing would ever delete.
        var parent = Assert.Single(ensured);
        Assert.Equal(2, parent.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.StartsWith("/orionlock/", parent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_PrunesTheParentZnode_OnceItsLastChildIsGone()
    {
        var adapter = NewAdapter(out _);
        var sut = new ZooKeeperLockProvider(adapter.Object);
        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        // After the holder's child is deleted the parent is empty.
        adapter.Setup(a => a.GetChildrenAsync("/orionlock/k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);

        adapter.Verify(a => a.DeleteAsync("/orionlock/k", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Release_LeavesTheParentAlone_WhileAnotherWaiterStillHoldsAChild()
    {
        var adapter = NewAdapter(out _);
        var sut = new ZooKeeperLockProvider(adapter.Object);
        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        adapter.Setup(a => a.GetChildrenAsync("/orionlock/k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000002" });

        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);

        adapter.Verify(a => a.DeleteAsync("/orionlock/k", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Acquire_RecreatesTheParent_WhenAConcurrentReleasePrunedItMidFlight()
    {
        // The cleanup introduces a race the provider has to absorb: a release can prune an empty parent
        // between this caller's EnsurePath and its create. That must not surface as a failed acquire.
        var adapter = new Mock<IZooKeeperClientAdapter>();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var attempts = 0;
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == 1
                ? throw new InvalidOperationException("no node")
                : "/orionlock/k/lock-0000000001");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000001" });

        var sut = new ZooKeeperLockProvider(adapter.Object);

        Assert.True(await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None));
        adapter.Verify(a => a.EnsurePathAsync("/orionlock/k", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Acquire_StillFails_WhenTheRetryFailsToo()
    {
        var adapter = new Mock<IZooKeeperClientAdapter>();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("session closed"));

        var sut = new ZooKeeperLockProvider(adapter.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None));
    }

    private static Mock<IZooKeeperClientAdapter> NewAdapter(out List<string> ensuredPaths)
    {
        var ensured = new List<string>();
        ensuredPaths = ensured;
        var adapter = new Mock<IZooKeeperClientAdapter>();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string path, CancellationToken _) => ensured.Add(path))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string parent, string prefix, byte[] _, CancellationToken _)
                => $"{parent}/{prefix}0000000001");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000001" });
        return adapter;
    }
}
