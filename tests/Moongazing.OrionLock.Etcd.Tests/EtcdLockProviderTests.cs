using Moongazing.OrionLock.Etcd;
using Moq;

namespace Moongazing.OrionLock.Etcd.Tests;

public sealed class EtcdLockProviderTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static (Mock<IEtcdClientAdapter> adapter, EtcdLockProvider sut) NewProvider(EtcdLockOptions? options = null)
    {
        var adapter = new Mock<IEtcdClientAdapter>();
        var sut = new EtcdLockProvider(adapter.Object, options);
        return (adapter, sut);
    }

    [Fact]
    public async Task TryAcquireAsync_grants_lease_then_puts_key_with_owner_token()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(11L);
        adapter.Setup(a => a.KvPutIfAbsentAsync("orionlock/k", "owner-1", 11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var acquired = await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);
        adapter.Verify(a => a.LeaseGrantAsync(30, It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.KvPutIfAbsentAsync("orionlock/k", "owner-1", 11L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_revokes_orphan_lease_on_lost_race()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(42L);
        adapter.Setup(a => a.KvPutIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var acquired = await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(acquired);
        adapter.Verify(a => a.LeaseRevokeAsync(42L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_revokes_orphan_lease_when_KvPut_throws()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(99L);
        adapter.Setup(a => a.KvPutIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network down"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None));

        adapter.Verify(a => a.LeaseRevokeAsync(99L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_clamps_lease_to_MinLeaseTtlSeconds()
    {
        var (adapter, sut) = NewProvider(new EtcdLockOptions { MinLeaseTtlSeconds = 15 });
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);
        adapter.Setup(a => a.KvPutIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(3), CancellationToken.None);

        adapter.Verify(a => a.LeaseGrantAsync(15, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryRenewAsync_returns_false_without_active_lease_for_pair()
    {
        var (_, sut) = NewProvider();

        var renewed = await sut.TryRenewAsync("k", "ghost-owner", Lease, CancellationToken.None);

        Assert.False(renewed);
    }

    [Fact]
    public async Task TryRenewAsync_passes_through_adapter_keep_alive_outcome()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.LeaseKeepAliveAsync(11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var renewed = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(renewed);
    }

    [Fact]
    public async Task TryRenewAsync_drops_mapping_on_lease_lost()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.LeaseKeepAliveAsync(11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var first = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);
        var second = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(first);
        Assert.False(second);
        adapter.Verify(a => a.LeaseKeepAliveAsync(11L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_deletes_key_if_match_then_revokes_lease()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.KvDeleteIfMatchAsync("orionlock/k", "owner-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);

        adapter.Verify(a => a.KvDeleteIfMatchAsync("orionlock/k", "owner-1", It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.LeaseRevokeAsync(11L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_is_idempotent_for_unknown_owner_key_pair()
    {
        var (adapter, sut) = NewProvider();

        await sut.ReleaseAsync("k", "unknown-owner", CancellationToken.None);

        adapter.Verify(a => a.KvDeleteIfMatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        adapter.Verify(a => a.LeaseRevokeAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryRenewAsync_ignores_lease_for_a_different_key_held_by_same_owner()
    {
        var adapter = new Mock<IEtcdClientAdapter>();
        var sut = new EtcdLockProvider(adapter.Object);
        adapter.SetupSequence(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100L).ReturnsAsync(200L);
        adapter.Setup(a => a.KvPutIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        adapter.Setup(a => a.LeaseKeepAliveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.TryAcquireAsync("key-A", "owner-shared", Lease, CancellationToken.None);
        await sut.TryAcquireAsync("key-B", "owner-shared", Lease, CancellationToken.None);

        await sut.TryRenewAsync("key-A", "owner-shared", Lease, CancellationToken.None);

        adapter.Verify(a => a.LeaseKeepAliveAsync(100L, It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.LeaseKeepAliveAsync(200L, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KeyPrefix_namespaces_the_full_key()
    {
        var (adapter, sut) = NewProvider(new EtcdLockOptions { KeyPrefix = "tenants/acme/" });
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7L);
        adapter.Setup(a => a.KvPutIfAbsentAsync("tenants/acme/orders", "owner-1", 7L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var acquired = await sut.TryAcquireAsync("orders", "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);
    }

    private static async Task<(Mock<IEtcdClientAdapter> adapter, EtcdLockProvider sut)> AcquireAsync()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(11L);
        adapter.Setup(a => a.KvPutIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);
        return (adapter, sut);
    }
}
