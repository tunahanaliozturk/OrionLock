namespace Moongazing.OrionLock.Tests.Internal;

using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Xunit;

/// <summary>
/// The measuring decorator wraps EVERY provider registered through <c>AddOrionLock</c>, so anything it
/// fails to forward is unreachable in production no matter what the backend declares.
/// </summary>
public sealed class MeasuringLockProviderTests
{
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

    private sealed class TtlProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public void LeaseDurationIsTtl_is_forwarded_from_a_session_scoped_inner_provider()
    {
        IDistributedLockProvider measured = new MeasuringLockProvider(new SessionScopedProvider());

        Assert.False(measured.LeaseDurationIsTtl);
    }

    [Fact]
    public void LeaseDurationIsTtl_is_forwarded_from_a_ttl_inner_provider()
    {
        IDistributedLockProvider measured = new MeasuringLockProvider(new TtlProvider());

        Assert.True(measured.LeaseDurationIsTtl);
    }

    /// <summary>
    /// Announces the event-driven wait and records what it was handed. Exactly the shape a backend
    /// that blocks server-side or subscribes to a release signal has.
    /// </summary>
    private sealed class BlockingWaitProvider : IDistributedLockProvider
    {
        public int WaitCalls { get; private set; }
        public TimeSpan SeenMaxWait { get; private set; }
        public LockWaitPolicy SeenPolicy { get; private set; }

        public Task<LockAcquisition> WaitForAcquireAsync(
            string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
            LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
        {
            WaitCalls++;
            SeenMaxWait = maxWait;
            SeenPolicy = waitPolicy;
            // A backend that mints tokens mints one for a lock taken by WAITING too.
            return Task.FromResult(LockAcquisition.Fenced(WaitingToken));
        }

        /// <summary>The token this fake mints for a wait that wins.</summary>
        public const long WaitingToken = 4242;

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task WaitForAcquireAsync_is_forwarded_to_the_inner_provider()
    {
        // Same trap as LeaseDurationIsTtl, one release later: a member this decorator does not
        // forward falls back to the interface default - the poll loop - for EVERY backend, so every
        // server-side block, watch and subscription would be unreachable through DI while still
        // passing the provider's own tests.
        var inner = new BlockingWaitProvider();
        IDistributedLockProvider measured = new MeasuringLockProvider(inner);

        var acquired = await measured.WaitForAcquireAsync(
            "k", "owner", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(7),
            new LockWaitPolicy(TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.True(acquired.Acquired);
        // The token has to survive the decorator too: a wait whose token is dropped here leaves
        // fencing working on an idle key and dark under contention.
        Assert.Equal(BlockingWaitProvider.WaitingToken, acquired.FencingToken);
        Assert.Equal(1, inner.WaitCalls);
        Assert.Equal(TimeSpan.FromSeconds(7), inner.SeenMaxWait);
        Assert.Equal(TimeSpan.FromMilliseconds(40), inner.SeenPolicy.RetryInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), inner.SeenPolicy.BackoffCeiling);
    }

    [Fact]
    public async Task A_blocking_backend_is_still_reached_through_the_decorator_from_a_blocking_acquire()
    {
        // The end-to-end version of the same fact: the decorator is what DI hands DistributedLock,
        // so this is the assertion that would have caught the LeaseDurationIsTtl omission too.
        var inner = new BlockingWaitProvider();
        var sut = new DistributedLock(new MeasuringLockProvider(inner));

        await using var handle = await sut.AcquireAsync("k", new DistributedLockOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromMilliseconds(10),
            AutoRenew = false,
        });

        Assert.Equal(1, inner.WaitCalls);
        // End to end: the token minted by the WAIT reaches the handle the caller holds.
        Assert.Equal(BlockingWaitProvider.WaitingToken, handle.FencingToken);
    }
}
