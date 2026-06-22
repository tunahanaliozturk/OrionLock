using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing.Tests;

/// <summary>
/// Watchdog-renewal behaviour of <see cref="SharedExclusiveLockHandle"/> that the in-memory
/// provider cannot exercise: specifically that an <see cref="OperationCanceledException"/> thrown by
/// the provider for a reason OTHER than the watchdog's own token does not silently stop renewals.
/// </summary>
public class SharedExclusiveLockHandleRenewalTests
{
    /// <summary>
    /// Provider whose <see cref="TryRenewAsync"/> throws an OCE bound to an UNRELATED token (not the
    /// watchdog ct). Counts how many times renewal was attempted so the test can prove the loop kept
    /// going rather than returning after the first throw.
    /// </summary>
    private sealed class UnrelatedOceProvider : ISharedExclusiveLockProvider
    {
        private readonly CancellationToken unrelated;
        public int RenewAttempts;

        public UnrelatedOceProvider(CancellationToken unrelated) => this.unrelated = unrelated;

        public Task<bool> TryAcquireAsync(
            string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(
            string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RenewAttempts);
            // Throw an OCE that is NOT the watchdog's cancellationToken. Pre-fix, the handle caught
            // every OCE and returned; post-fix only ct.IsCancellationRequested is terminal.
            throw new OperationCanceledException(unrelated);
        }

        public Task ReleaseAsync(string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
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
            // Generous grace so repeated unrelated-OCE renew failures do NOT surrender during the
            // observation window; the point is that the loop keeps retrying, not that it surrenders.
            RenewalFailureGracePeriod = TimeSpan.FromSeconds(30),
            AutoRenew = true,
        };

        await using var handle = new SharedExclusiveLockHandle(
            provider, "k", ownerToken: "owner-1", LockMode.Exclusive, opts);

        // Give the watchdog time for MANY renewal intervals (~20ms each). 1000ms is ~50 intervals, so
        // the "> 1 attempt" assertion holds with a huge margin even if a loaded runner starves the timer
        // down to a handful of ticks. The earlier 150ms window could yield too few ticks under load.
        await Task.Delay(1000);

        // The unrelated OCE must be treated as a transient renew failure, not a terminal stop:
        // renewals keep being attempted, the handle is still held, and LostToken has not tripped.
        Assert.True(Volatile.Read(ref provider.RenewAttempts) > 1,
            $"expected renewals to keep being attempted; got {Volatile.Read(ref provider.RenewAttempts)}");
        Assert.True(handle.IsHeld);
        Assert.False(handle.LostToken.IsCancellationRequested);
    }
}
