using Moongazing.OrionLock.ZooKeeper;
using Moq;

namespace Moongazing.OrionLock.ZooKeeper.Tests;

public sealed class ZooKeeperLockProviderTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static (Mock<IZooKeeperClientAdapter> adapter, ZooKeeperLockProvider sut) NewProvider(
        ZooKeeperLockOptions? options = null)
    {
        var adapter = new Mock<IZooKeeperClientAdapter>();
        var sut = new ZooKeeperLockProvider(adapter.Object, options);
        return (adapter, sut);
    }

    [Fact]
    public async Task TryAcquireAsync_acquires_when_created_child_is_lowest_sequence()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                "/orionlock/k", "lock-", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/orionlock/k/lock-0000000001");
        adapter.Setup(a => a.GetChildrenAsync("/orionlock/k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000001" });

        var acquired = await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);
    }

    [Fact]
    public async Task TryAcquireAsync_returns_false_when_another_child_has_lower_sequence()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/orionlock/k/lock-0000000005");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000001", "lock-0000000005" });

        var acquired = await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(acquired);
        adapter.Verify(a => a.DeleteAsync("/orionlock/k/lock-0000000005", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryRenewAsync_returns_false_for_unknown_owner_key_pair()
    {
        var (adapter, sut) = NewProvider();

        var renewed = await sut.TryRenewAsync("k", "ghost-owner", Lease, CancellationToken.None);

        Assert.False(renewed);
        adapter.Verify(a => a.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryRenewAsync_returns_true_when_znode_still_exists()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.ExistsAsync("/orionlock/k/lock-0000000001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var renewed = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(renewed);
    }

    [Fact]
    public async Task TryRenewAsync_drops_mapping_when_znode_gone()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var first = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);
        var second = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(first);
        Assert.False(second);
        // Mapping was dropped after the first failure so the second call short-circuits
        // without hitting the adapter again.
        adapter.Verify(a => a.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_deletes_owned_child_znode()
    {
        var (adapter, sut) = await AcquireAsync();

        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);

        adapter.Verify(a => a.DeleteAsync("/orionlock/k/lock-0000000001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_is_idempotent_for_unknown_owner_key_pair()
    {
        var (adapter, sut) = NewProvider();

        await sut.ReleaseAsync("k", "unknown-owner", CancellationToken.None);

        adapter.Verify(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReleaseAsync_swallows_delete_exception_so_caller_does_not_throw()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network down"));

        // ZooKeeper auto-deletes the ephemeral on session close so a delete failure here
        // is benign; the release call must NOT propagate the exception to the OrionLock
        // core.
        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);
    }

    [Fact]
    public async Task RootPath_namespaces_the_parent_znode()
    {
        var (adapter, sut) = NewProvider(new ZooKeeperLockOptions { RootPath = "/tenants/acme" });
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                "/tenants/acme/orders", "lock-", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tenants/acme/orders/lock-0000000001");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000001" });

        var acquired = await sut.TryAcquireAsync("orders", "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);
        adapter.Verify(a => a.EnsurePathAsync("/tenants/acme/orders", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Constructor_validates_RootPath_options()
    {
        var adapter = new Mock<IZooKeeperClientAdapter>();
        Assert.Throws<ArgumentException>(
            () => new ZooKeeperLockProvider(adapter.Object, new ZooKeeperLockOptions { RootPath = string.Empty }));
        Assert.Throws<ArgumentException>(
            () => new ZooKeeperLockProvider(adapter.Object, new ZooKeeperLockOptions { RootPath = "  " }));
    }

    [Fact]
    public void Constructor_normalises_RootPath_to_have_leading_slash()
    {
        var adapter = new Mock<IZooKeeperClientAdapter>();
        var opts = new ZooKeeperLockOptions { RootPath = "tenants/acme/" };
        _ = new ZooKeeperLockProvider(adapter.Object, opts);
        Assert.Equal("/tenants/acme", opts.RootPath);
    }

    [Fact]
    public async Task TryAcquireAsync_deletes_created_znode_when_GetChildren_throws()
    {
        var adapter = new Mock<IZooKeeperClientAdapter>();
        var sut = new ZooKeeperLockProvider(adapter.Object);
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/orionlock/k/lock-0000000007");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network blip"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None));

        // The newly-created ephemeral child MUST be deleted so it does not block other
        // waiters until session expiry.
        adapter.Verify(a => a.DeleteAsync("/orionlock/k/lock-0000000007", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<(Mock<IZooKeeperClientAdapter> adapter, ZooKeeperLockProvider sut)> AcquireAsync()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.EnsurePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.Setup(a => a.CreateEphemeralSequentialAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/orionlock/k/lock-0000000001");
        adapter.Setup(a => a.GetChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "lock-0000000001" });
        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);
        return (adapter, sut);
    }
}
