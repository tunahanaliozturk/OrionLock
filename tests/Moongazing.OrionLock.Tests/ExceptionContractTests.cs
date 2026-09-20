using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Tests;

/// <summary>
/// <see cref="IDistributedLock"/> documented one exception and could raise a dozen, several of them a
/// backend's own driver type - a <c>SqlException</c>, <c>PostgresException</c>, <c>RpcException</c> or
/// <c>KeeperException</c> straight out of the acquire. Catching lock trouble therefore meant
/// referencing every backend's driver package and writing a different <c>catch</c> per registration,
/// which is exactly what a backend-agnostic interface exists to avoid.
/// </summary>
public sealed class ExceptionContractTests
{
    /// <summary>Stands in for a driver: throws whatever it was handed, from every operation.</summary>
    private sealed class ThrowingProvider(Func<Exception> fault) : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromException<bool>(fault());

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromException<bool>(fault());

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.FromException(fault());
    }

    /// <summary>A driver exception type the core has never heard of, like every real one.</summary>
    private sealed class DriverException(string message) : Exception(message);

    private static IDistributedLock LockOver(Func<Exception> fault)
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseBackend("fake", _ => new ThrowingProvider(fault));
        return services.BuildServiceProvider().GetRequiredService<IDistributedLock>();
    }

    [Fact]
    public async Task ADriverException_ReachesTheCallerAsOrionLockBackendException_WithTheOriginalInside()
    {
        var sut = LockOver(() => new DriverException("connection reset by peer"));

        var ex = await Assert.ThrowsAsync<OrionLockBackendException>(() => sut.AcquireAsync("k"));

        Assert.Equal("k", ex.Key);
        Assert.Contains("acquire", ex.Reason, StringComparison.Ordinal);
        // The driver's own exception is preserved, so a backend-specific diagnosis is still possible.
        Assert.IsType<DriverException>(ex.InnerException);
        Assert.Contains("connection reset by peer", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EverySingleShotEntryPoint_WrapsTheSameWay()
    {
        var sut = LockOver(() => new DriverException("boom"));

        await Assert.ThrowsAsync<OrionLockBackendException>(() => sut.TryAcquireAsync("k"));
        await Assert.ThrowsAsync<OrionLockBackendException>(
            () => sut.TryAcquireAsync("k", TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Cancellation_IsNotWrapped_BecauseItIsControlFlowRatherThanAFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = LockOver(() => new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.AcquireAsync("k", null, cts.Token));
    }

    [Fact]
    public async Task AnOrionLockInvariant_IsNotWrapped_SoAnOwnerTokenCollisionStaysItself()
    {
        // SQL Server and PostgreSQL raise this when an ownerToken is already registered. It is an
        // OrionLock invariant, not the backend failing, so it must not be disguised as a backend fault.
        var sut = LockOver(() => new InvalidOperationException("ownerToken already registered."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.AcquireAsync("k"));
    }

    [Fact]
    public async Task AnAlreadyWrappedBackendException_PassesThroughUnchanged()
    {
        // SQL Server already models the right shape for an unexpected sp_getapplock return code; it must
        // not be wrapped a second time.
        var original = new OrionLockBackendException("k", "sp_getapplock returned -3 (deadlock victim).");
        var sut = LockOver(() => original);

        var ex = await Assert.ThrowsAsync<OrionLockBackendException>(() => sut.AcquireAsync("k"));

        Assert.Same(original, ex);
    }

    [Fact]
    public async Task TheContractHoldsForAHandBuiltLock_NotOnlyForTheDiOne()
    {
        // The guard used to be installed by AddOrionLock alone, so this public constructor - which is
        // part of the shipped API - still handed the caller raw driver exceptions while the interface
        // documented that they arrive wrapped. Two objects of the same type with two different
        // contracts is worse than no contract.
        var sut = new DistributedLock(new ThrowingProvider(() => new DriverException("connection reset")));

        var ex = await Assert.ThrowsAsync<OrionLockBackendException>(() => sut.AcquireAsync("k"));

        Assert.IsType<DriverException>(ex.InnerException);
    }

    [Fact]
    public async Task TheGuardDoesNotSwallowTheFencingToken()
    {
        // The guard decorates IDistributedLockProvider. Without forwarding the FENCED acquire it would
        // fall back to the interface default, which delegates to the unfenced overload and reports no
        // token - so wrapping a fencing-capable backend would silently drop every token it mints.
        var sut = new DistributedLock(new FencedProvider(fencingToken: 42));

        await using var handle = await sut.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal(42, handle.FencingToken);
    }

    /// <summary>A backend that mints a fencing token, like Redis and EF Core do.</summary>
    private sealed class FencedProvider(long fencingToken) : IDistributedLockProvider
    {
        public Task<LockAcquisition> TryAcquireFencedAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(LockAcquisition.Fenced(fencingToken));

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task ThrowIfLost_TurnsALostLeaseIntoLeaseLostException()
    {
        // LeaseLostException was defined and thrown nowhere. This is its home: the point in a critical
        // section where continuing without the lock would be wrong.
        var sut = new DistributedLock(new Testing.InMemoryLockProvider());
        var handle = await sut.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        handle.ThrowIfLost();   // still held: no-op

        await handle.DisposeAsync();

        var ex = Assert.Throws<LeaseLostException>(handle.ThrowIfLost);
        Assert.Equal("k", ex.Key);
    }
}
