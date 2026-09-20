using Moongazing.OrionLock.Etcd;
using Moq;

namespace Moongazing.OrionLock.Etcd.Tests;

/// <summary>
/// etcd's token is the mvcc revision the acquiring transaction committed at, taken from that
/// transaction's own response - so these tests are about the provider passing it through faithfully and
/// not inventing one when the adapter cannot supply it.
/// </summary>
public sealed class EtcdFencingTokenTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_committed_revision_becomes_the_fencing_token()
    {
        var adapter = new Mock<IEtcdClientAdapter>();
        adapter.As<IEtcdFencingAdapter>()
            .Setup(a => a.KvPutIfAbsentFencedAsync("orionlock/k", "owner-1", 11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Providers.LockAcquisition.Fenced(4711L));
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(11L);
        var sut = new EtcdLockProvider(adapter.Object);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(taken.Acquired);
        Assert.Equal(4711L, taken.FencingToken);
    }

    [Fact]
    public async Task A_lost_race_reports_no_token_and_revokes_the_orphan_lease()
    {
        var adapter = new Mock<IEtcdClientAdapter>();
        adapter.As<IEtcdFencingAdapter>()
            .Setup(a => a.KvPutIfAbsentFencedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Providers.LockAcquisition.NotAcquired);
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(42L);
        var sut = new EtcdLockProvider(adapter.Object);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(taken.Acquired);
        Assert.Null(taken.FencingToken);
        adapter.Verify(a => a.LeaseRevokeAsync(42L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_adapter_that_cannot_report_a_revision_still_acquires_and_reports_no_token()
    {
        // A custom adapter written against the original interface keeps working: the capability
        // interface is probed, not required, so the acquire path is unchanged and the token is null
        // rather than a number the provider made up on the adapter's behalf.
        var adapter = new Mock<IEtcdClientAdapter>();
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(7L);
        adapter.Setup(a => a.KvPutIfAbsentAsync("orionlock/k", "owner-1", 7L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = new EtcdLockProvider(adapter.Object);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
    }

    [Fact]
    public async Task Successive_acquisitions_carry_the_successive_revisions_etcd_reports()
    {
        // etcd's revision advances on every committed write across the whole keyspace, so a later
        // acquisition of the same key is always a higher number - including after the key was deleted
        // and recreated, which is what a released or expired lock looks like.
        var adapter = new Mock<IEtcdClientAdapter>();
        adapter.Setup(a => a.LeaseGrantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(1L);
        var revisions = new Queue<long>([17L, 23L, 99L]);
        adapter.As<IEtcdFencingAdapter>()
            .Setup(a => a.KvPutIfAbsentFencedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Providers.LockAcquisition.Fenced(revisions.Dequeue()));
        var sut = new EtcdLockProvider(adapter.Object);

        var tokens = new List<long?>();
        foreach (var owner in new[] { "a", "b", "c" })
        {
            tokens.Add((await sut.TryAcquireFencedAsync("k", owner, Lease, CancellationToken.None)).FencingToken);
            await sut.ReleaseAsync("k", owner, CancellationToken.None);
        }

        Assert.Equal([17L, 23L, 99L], tokens);
    }
}
