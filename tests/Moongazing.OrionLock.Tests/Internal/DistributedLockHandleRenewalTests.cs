namespace Moongazing.OrionLock.Tests.Internal;

using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Xunit;

/// <summary>
/// Mirror of <c>SharedExclusiveLockHandleRenewalTests</c> for the exclusive handle: an
/// <see cref="OperationCanceledException"/> thrown by the provider for a reason OTHER than the
/// watchdog's own token must not silently stop renewals.
/// </summary>
public sealed class DistributedLockHandleRenewalTests
{
    /// <summary>
    /// Provider whose <see cref="TryRenewAsync"/> throws an OCE bound to an UNRELATED token (not the
    /// watchdog ct). Counts renew attempts so the test can prove the loop kept going rather than
    /// returning after the first throw.
    /// </summary>
    private sealed class UnrelatedOceProvider : IDistributedLockProvider
    {
        private readonly CancellationToken unrelated;
        public int RenewAttempts;

        public UnrelatedOceProvider(CancellationToken unrelated) => this.unrelated = unrelated;

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RenewAttempts);
            // An OCE that is NOT the watchdog's cancellationToken. Pre-fix the handle caught every OCE
            // and returned, leaving isHeld true and LostToken untripped forever.
            throw new OperationCanceledException(unrelated);
        }

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task UnrelatedOce_DoesNotSilentlyStopRenewals()
    {
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();   // a DIFFERENT, already-cancelled token the provider raises OCE for
        var provider = new UnrelatedOceProvider(unrelatedCts.Token);

        var opts = new DistributedLockOptions
        {
            // Short lease -> renewal interval of LeaseDuration/3 (floored at 10ms) fires rapidly.
            LeaseDuration = TimeSpan.FromMilliseconds(60),
            // Generous grace so the repeated unrelated-OCE failures do NOT surrender during the
            // observation window; the point is that the loop keeps retrying, not that it surrenders.
            RenewalFailureGracePeriod = TimeSpan.FromSeconds(30),
            AutoRenew = true,
        };

        await using var handle = new DistributedLockHandle(provider, "k", ownerToken: "owner-1", opts);

        // ~20ms per interval; 1000ms is ~50 intervals, so "> 1 attempt" holds with a huge margin even
        // if a loaded runner starves the timer down to a handful of ticks.
        await Task.Delay(1000);

        Assert.True(Volatile.Read(ref provider.RenewAttempts) > 1,
            $"expected renewals to keep being attempted; got {Volatile.Read(ref provider.RenewAttempts)}");
        Assert.True(handle.IsHeld);
        Assert.False(handle.LostToken.IsCancellationRequested);
    }
}
