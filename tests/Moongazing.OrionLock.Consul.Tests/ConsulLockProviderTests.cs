using Moongazing.OrionLock.Consul;
using Moq;

namespace Moongazing.OrionLock.Consul.Tests;

public sealed class ConsulLockProviderTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static (Mock<IConsulClientAdapter> adapter, ConsulLockProvider sut) NewProvider(
        ConsulLockOptions? options = null)
    {
        var adapter = new Mock<IConsulClientAdapter>();
        var sut = new ConsulLockProvider(adapter.Object, options);
        return (adapter, sut);
    }

    [Fact]
    public async Task TryAcquireAsync_creates_session_then_acquires_key()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                "orionlock/k", "owner-1", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var acquired = await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);
        adapter.Verify(a => a.CreateSessionAsync(Lease, "release", TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.KvAcquireAsync("orionlock/k", "owner-1", "session-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_destroys_orphan_session_when_acquire_loses_race()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-x");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var acquired = await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(acquired);
        adapter.Verify(a => a.DestroySessionAsync("session-x", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_uses_MinSessionTtl_when_lease_below_floor()
    {
        var (adapter, sut) = NewProvider(new ConsulLockOptions { MinSessionTtl = TimeSpan.FromSeconds(15) });
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(5), CancellationToken.None);

        adapter.Verify(a => a.CreateSessionAsync(TimeSpan.FromSeconds(15), "release", TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryRenewAsync_returns_false_when_no_active_session_for_owner()
    {
        var (_, sut) = NewProvider();

        var renewed = await sut.TryRenewAsync("k", "ghost-owner", Lease, CancellationToken.None);

        Assert.False(renewed);
    }

    [Fact]
    public async Task TryRenewAsync_returns_true_when_adapter_renew_succeeds()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.RenewSessionAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var renewed = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(renewed);
    }

    [Fact]
    public async Task TryRenewAsync_drops_mapping_when_session_expired_in_consul()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.RenewSessionAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var first = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);
        var second = await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(first);
        Assert.False(second);
        // Second call must NOT hit the adapter again; mapping was dropped after the first
        // failed renew.
        adapter.Verify(a => a.RenewSessionAsync("session-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_releases_kv_then_destroys_session()
    {
        var (adapter, sut) = await AcquireAsync();
        adapter.Setup(a => a.KvReleaseAsync("orionlock/k", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.ReleaseAsync("k", "owner-1", CancellationToken.None);

        adapter.Verify(a => a.KvReleaseAsync("orionlock/k", "session-1", It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.DestroySessionAsync("session-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_is_idempotent_for_unknown_owner()
    {
        var (adapter, sut) = NewProvider();

        // Should NOT throw and should NOT hit the adapter.
        await sut.ReleaseAsync("k", "unknown-owner", CancellationToken.None);

        adapter.Verify(a => a.KvReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        adapter.Verify(a => a.DestroySessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SessionBehavior_delete_flows_through_to_create_session()
    {
        var (adapter, sut) = NewProvider(new ConsulLockOptions { SessionBehavior = "delete" });
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-d");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        adapter.Verify(a => a.CreateSessionAsync(It.IsAny<TimeSpan>(), "delete", TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KeyPrefix_namespaces_the_full_key()
    {
        var (adapter, sut) = NewProvider(new ConsulLockOptions { KeyPrefix = "tenants/acme/" });
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                "tenants/acme/orders", "owner-1", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var acquired = await sut.TryAcquireAsync("orders", "owner-1", Lease, CancellationToken.None);

        Assert.True(acquired);
    }

    [Fact]
    public async Task TryRenewAsync_ignores_session_for_a_different_key_held_by_same_owner()
    {
        // The same ownerToken is presented for two different keys. Renewing one MUST NOT
        // touch the other's session.
        var adapter = new Mock<IConsulClientAdapter>();
        var sut = new ConsulLockProvider(adapter.Object);
        adapter.SetupSequence(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-A").ReturnsAsync("session-B");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        adapter.Setup(a => a.RenewSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.TryAcquireAsync("key-A", "owner-shared", Lease, CancellationToken.None);
        await sut.TryAcquireAsync("key-B", "owner-shared", Lease, CancellationToken.None);

        await sut.TryRenewAsync("key-A", "owner-shared", Lease, CancellationToken.None);

        adapter.Verify(a => a.RenewSessionAsync("session-A", It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.RenewSessionAsync("session-B", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryAcquireAsync_destroys_session_when_KV_acquire_throws()
    {
        var adapter = new Mock<IConsulClientAdapter>();
        var sut = new ConsulLockProvider(adapter.Object);
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-x");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network down"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None));

        adapter.Verify(a => a.DestroySessionAsync("session-x", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LockDelay_flows_through_to_CreateSessionAsync()
    {
        var adapter = new Mock<IConsulClientAdapter>();
        var sut = new ConsulLockProvider(adapter.Object, new ConsulLockOptions
        {
            LockDelay = TimeSpan.FromSeconds(2),
        });
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);

        adapter.Verify(a => a.CreateSessionAsync(
            It.IsAny<TimeSpan>(), "release", TimeSpan.FromSeconds(2), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_rejects_blank_key_argument()
    {
        var (_, sut) = NewProvider();

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.ReleaseAsync(" ", "owner-1", CancellationToken.None));
    }

    private static async Task<(Mock<IConsulClientAdapter> adapter, ConsulLockProvider sut)> AcquireAsync()
    {
        var (adapter, sut) = NewProvider();
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None);
        return (adapter, sut);
    }
}
