using Moongazing.OrionLock;
using Moongazing.OrionLock.Consul;
using Moq;

namespace Moongazing.OrionLock.Consul.Tests;

/// <summary>
/// Consul's token is the key's <c>ModifyIndex</c> - the Raft log index - read back after a successful
/// acquire. It is opt-in because that read is an extra round trip, and it fails the acquire rather than
/// handing back a hold with a silently missing token.
/// </summary>
public sealed class ConsulFencingTokenTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    // Moq requires every As<T>() before the first .Object access, so the capability interface is added
    // while the mock is built rather than per test.
    private static (Mock<IConsulClientAdapter> adapter, Mock<IConsulFencingAdapter> fencing, ConsulLockProvider sut)
        NewProvider(bool fencingTokens)
    {
        var adapter = new Mock<IConsulClientAdapter>();
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var fencing = adapter.As<IConsulFencingAdapter>();
        var sut = new ConsulLockProvider(adapter.Object, new ConsulLockOptions { FencingTokens = fencingTokens });
        return (adapter, fencing, sut);
    }

    [Fact]
    public async Task The_ModifyIndex_becomes_the_fencing_token()
    {
        var (_, fencing, sut) = NewProvider(fencingTokens: true);
        fencing.Setup(a => a.KvModifyIndexAsync("orionlock/k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(90210L);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(taken.Acquired);
        Assert.Equal(90210L, taken.FencingToken);
    }

    [Fact]
    public async Task Fencing_is_off_by_default_and_costs_no_extra_round_trip()
    {
        var (_, fencing, sut) = NewProvider(fencingTokens: false);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
        fencing.Verify(
            a => a.KvModifyIndexAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_index_read_releases_the_key_rather_than_returning_an_untokened_hold()
    {
        // The caller asked for a fenced acquire. Returning a hold with a null token would hand them
        // exactly the silent gap fencing exists to close, so the provider gives the lock back instead.
        var (adapter, fencing, sut) = NewProvider(fencingTokens: true);
        fencing.Setup(a => a.KvModifyIndexAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("consul unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None));

        adapter.Verify(
            a => a.KvReleaseAsync("orionlock/k", "session-1", It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.DestroySessionAsync("session-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_index_read_that_finds_no_key_releases_rather_than_reporting_an_untokened_hold()
    {
        // KvModifyIndexAsync returns null when the key is not there - which, between a successful
        // acquire and the read-back, means the session was invalidated or the entry deleted. We did not
        // really end up holding it. Returning "acquired, no token" here would hand a caller who
        // explicitly asked for fencing a handle with no token over a lock that may already be gone:
        // the same outcome as the throwing case, so it takes the same exit.
        var (adapter, fencing, sut) = NewProvider(fencingTokens: true);
        fencing.Setup(a => a.KvModifyIndexAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        await Assert.ThrowsAsync<OrionLockBackendException>(
            () => sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None));

        adapter.Verify(
            a => a.KvReleaseAsync("orionlock/k", "session-1", It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.DestroySessionAsync("session-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_index_read_does_not_leave_the_owner_mapping_behind()
    {
        // The mapping is what Renew and Release key off. Publishing it for a hold we just gave back
        // would let a later Release destroy a session some other acquirer now owns.
        var (adapter, fencing, sut) = NewProvider(fencingTokens: true);
        fencing.Setup(a => a.KvModifyIndexAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        await Assert.ThrowsAsync<OrionLockBackendException>(
            () => sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None));

        Assert.False(await sut.TryRenewAsync("k", "owner-1", Lease, CancellationToken.None));
    }

    [Fact]
    public async Task A_lost_race_reads_no_index_at_all()
    {
        var (adapter, fencing, sut) = NewProvider(fencingTokens: true);
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var taken = await sut.TryAcquireFencedAsync("k", "owner-1", Lease, CancellationToken.None);

        Assert.False(taken.Acquired);
        Assert.Null(taken.FencingToken);
        fencing.Verify(
            a => a.KvModifyIndexAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
