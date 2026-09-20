using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

/// <summary>
/// <c>LeaseDuration</c> meant three different things and said nothing. Redis and EF Core treated it as
/// a wall-clock TTL, PostgreSQL and SQL Server ignored it (session-scoped), Consul silently raised it to
/// 10 s, etcd to 5 s rounded up to whole seconds, and ZooKeeper ignored it entirely - so a caller who
/// set 2 s and swapped Redis for Consul got a five-times-longer takeover window after a crash, with no
/// warning. A lease the backend cannot honour is now refused, and what it WILL honour is on the handle.
/// </summary>
public sealed class EffectiveLeaseDurationTests
{
    /// <summary>A backend with a lease floor, standing in for Consul (10 s) and etcd (5 s).</summary>
    private sealed class FlooredProvider(TimeSpan floor) : IDistributedLockProvider
    {
        public TimeSpan MinimumLeaseDuration => floor;

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>A session-scoped backend, standing in for PostgreSQL, SQL Server and ZooKeeper.</summary>
    private sealed class SessionScopedProvider : IDistributedLockProvider
    {
        public bool LeaseDurationIsTtl => false;

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>A backend that rounds the lease up, standing in for etcd's whole-second TTLs.</summary>
    private sealed class RoundingProvider : IDistributedLockProvider
    {
        public TimeSpan EffectiveLeaseDuration(TimeSpan requested)
            => TimeSpan.FromSeconds(Math.Ceiling(requested.TotalSeconds));

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static DistributedLockOptions Lease(double seconds) =>
        new() { LeaseDuration = TimeSpan.FromSeconds(seconds), AutoRenew = false };

    [Fact]
    public void ALeaseBelowWhatTheBackendCanHonour_Throws_InsteadOfBeingRaisedSilently()
    {
        var sut = new DistributedLock(new FlooredProvider(TimeSpan.FromSeconds(10)));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.AcquireAsync("k", Lease(2)); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.TryAcquireAsync("k", Lease(2)); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = sut.TryAcquireAsync("k", TimeSpan.FromSeconds(1), Lease(2)); });
    }

    [Fact]
    public async Task ALeaseAtOrAboveTheFloor_IsHonouredExactly()
    {
        var sut = new DistributedLock(new FlooredProvider(TimeSpan.FromSeconds(10)));

        await using var handle = await sut.AcquireAsync("k", Lease(10));

        Assert.Equal(TimeSpan.FromSeconds(10), handle.EffectiveLeaseDuration);
    }

    [Fact]
    public async Task ATtlBackend_ReportsTheRequestedLease()
    {
        var sut = new DistributedLock(new InMemoryLockProvider());

        await using var handle = await sut.AcquireAsync("k", Lease(7));

        Assert.Equal(TimeSpan.FromSeconds(7), handle.EffectiveLeaseDuration);
    }

    [Fact]
    public async Task ASessionScopedBackend_ReportsInfinite_BecauseNoWallClockBoundsTheHold()
    {
        // PostgreSQL advisory locks, SQL Server sp_getapplock and ZooKeeper ephemeral znodes do not
        // expire by clock at all. Reporting the requested 7 s would be the lie that made this ambiguous.
        var sut = new DistributedLock(new SessionScopedProvider());

        await using var handle = await sut.AcquireAsync("k", Lease(7));

        Assert.Equal(Timeout.InfiniteTimeSpan, handle.EffectiveLeaseDuration);
    }

    [Fact]
    public async Task ARoundingBackend_ReportsTheRoundedLease_NotTheRequestedOne()
    {
        var sut = new DistributedLock(new RoundingProvider());

        await using var handle = await sut.AcquireAsync("k", Lease(2.5));

        Assert.Equal(TimeSpan.FromSeconds(3), handle.EffectiveLeaseDuration);
    }

    [Fact]
    public async Task ANestedReentrantHandle_ReportsTheOutermostHoldsLease()
    {
        var sut = new DistributedLock(new InMemoryLockProvider());

        await using var outer = await sut.AcquireAsync("k", Lease(9));
        await using var nested = await sut.AcquireAsync("k", Lease(3));

        Assert.Equal(TimeSpan.FromSeconds(9), nested.EffectiveLeaseDuration);
    }

    [Fact]
    public async Task AReaderWriterHold_ExposesItTheSameWay()
    {
        var sut = new SharedExclusiveLock(new InMemorySharedExclusiveLockProvider());

        await using var handle = await sut.AcquireSharedAsync("k", Lease(6));

        Assert.Equal(TimeSpan.FromSeconds(6), handle.EffectiveLeaseDuration);
    }
}
