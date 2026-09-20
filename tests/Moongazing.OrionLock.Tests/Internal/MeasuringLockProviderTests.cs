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
}
