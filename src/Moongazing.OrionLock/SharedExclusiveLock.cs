using System.Diagnostics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock;

/// <summary>
/// v0.4.0: the default <see cref="ISharedExclusiveLock"/>. Composes a backend
/// <see cref="ISharedExclusiveLockProvider"/> with a blocking-acquire retry loop and
/// lease-renewing handles, exactly as <see cref="DistributedLock"/> does for the exclusive-only
/// provider.
/// </summary>
/// <remarks>
/// <para>
/// Fairness / writer-starvation: this release polls the backend on the same retry interval the
/// exclusive lock uses. The FIFO waiter coordinator (<see cref="DistributedLockOptions.UseFifoWaiterCoordinator"/>)
/// is exclusive-only and is NOT consulted here, so under sustained shared-reader load a waiting
/// writer can in principle be starved if readers keep arriving and the backend always reports the
/// key shared-held. The in-memory backend mitigates this by reserving the key for a waiting writer
/// the moment the last shared holder drains; see the in-memory provider for the documented
/// pending-writer reservation behaviour. Cross-process fair RW ordering is a v0.4.x follow-up.
/// </para>
/// <para>
/// Reentrancy is not modelled for shared/exclusive holds in this release: each acquire takes a
/// fresh backend hold. A nested acquire of the same key in the same flow is treated as an
/// independent holder (a shared-on-shared nesting therefore works; a write-on-read or read-on-write
/// nesting on the same key in the same flow will block or fail like any other holder and can
/// deadlock if used that way).
/// </para>
/// </remarks>
public sealed class SharedExclusiveLock : ISharedExclusiveLock
{
    private readonly ISharedExclusiveLockProvider provider;
    // Optional consumer-registered lifecycle observer. Null and NullLockEventObserver are both
    // treated as 'no observer', exactly as DistributedLock does.
    private readonly ILockEventObserver? eventObserver;

    /// <summary>Creates a reader-writer lock over the given backend provider.</summary>
    public SharedExclusiveLock(ISharedExclusiveLockProvider provider)
        : this(provider, eventObserver: null)
    {
    }

    /// <summary>
    /// Creates a reader-writer lock that also reports lifecycle events to the given
    /// <see cref="ILockEventObserver"/>. The observer receives OnAcquired / OnAcquireTimedOut from
    /// this class and OnLeaseLost / OnReleased from the handles it creates - the same contract
    /// <see cref="DistributedLock"/> honours for exclusive holds.
    /// </summary>
    public SharedExclusiveLock(ISharedExclusiveLockProvider provider, ILockEventObserver? eventObserver)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
        this.eventObserver = eventObserver is NullLockEventObserver ? null : eventObserver;
    }

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireSharedAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => TryAcquireAsync(key, LockMode.Shared, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireExclusiveAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => TryAcquireAsync(key, LockMode.Exclusive, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle> AcquireSharedAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => AcquireAsync(key, LockMode.Shared, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle> AcquireExclusiveAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => AcquireAsync(key, LockMode.Exclusive, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireSharedAsync(
        string key, TimeSpan deadline, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => TryAcquireUntilDeadlineAsync(key, LockMode.Shared, deadline, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireExclusiveAsync(
        string key, TimeSpan deadline, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => TryAcquireUntilDeadlineAsync(key, LockMode.Exclusive, deadline, options, cancellationToken);

    private Task<IDistributedLockHandle?> TryAcquireUntilDeadlineAsync(
        string key, LockMode mode, TimeSpan deadline, DistributedLockOptions? options, CancellationToken cancellationToken)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();

        // Mint the owner token ONCE and reuse it across every deadline-retry attempt, exactly as the
        // blocking AcquireAsync loop does. A fresh token per attempt would make each retry a DIFFERENT
        // logical acquirer: it breaks fencing identity, and it leaks the providers' pending-writer
        // reservation (keyed by owner token, clearable only by the same owner on a later retry), so a
        // partially-succeeded attempt would be orphaned under a token no later retry can reclaim.
        var ownerToken = Guid.NewGuid().ToString("N");
        return DeadlineAcquire.TryAcquireUntilDeadlineAsync(
            (k, o, ct) => TryAcquireAsync(k, ownerToken, mode, o!, ct),
            key, deadline, options, cancellationToken);
    }

    private Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, LockMode mode, DistributedLockOptions? options, CancellationToken cancellationToken)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();
        return TryAcquireAsync(key, Guid.NewGuid().ToString("N"), mode, options, cancellationToken);
    }

    private async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, DistributedLockOptions options, CancellationToken cancellationToken)
    {
        var acquired = await provider
            .TryAcquireAsync(key, ownerToken, mode, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            return null;
        }

        // Thread the lifecycle observer into the handle so it can fire OnLeaseLost / OnReleased.
        var handle = new SharedExclusiveLockHandle(provider, key, ownerToken, mode, options, eventObserver);
        // Same held-concurrent gauge convention as the exclusive path: increment only when a real
        // backend hold is taken. The handle decrements exactly once on dispose / loss.
        OrionLockDiagnostics.IncrementLeasesHeld();
        return handle;
    }

    // Deliberately NOT an async method: an async body would capture the argument failures into the
    // returned Task, so a caller passing an illegal key would see them surface from an await deep inside
    // their critical section instead of from their own acquire call.
    private Task<IDistributedLockHandle> AcquireAsync(
        string key, LockMode mode, DistributedLockOptions? options, CancellationToken cancellationToken)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();
        return AcquireCoreAsync(key, mode, options, cancellationToken);
    }

    private async Task<IDistributedLockHandle> AcquireCoreAsync(
        string key, LockMode mode, DistributedLockOptions options, CancellationToken cancellationToken)
    {
        var modeTag = mode == LockMode.Shared ? "shared" : "exclusive";
        // Hot path: only build the interpolated activity name when a listener is actually
        // subscribed. With no listener StartActivity returns null and the name is never
        // observed, so the per-acquire string allocation is pure waste.
        using var activity = OrionLockDiagnostics.ActivitySource.HasListeners()
            ? OrionLockDiagnostics.ActivitySource.StartActivity($"OrionLock.AcquireRW {key}")
            : null;
        activity?.SetTag("orionlock.key", key);
        activity?.SetTag("orionlock.mode", modeTag);

        try
        {
            // Mint the owner token ONCE and reuse it across every poll attempt of this blocking
            // acquire. A fresh token per attempt would leak the in-memory provider's pending-writer
            // reservation: the reservation is keyed by owner token and only the same owner can clear
            // it on a successful retry, so a per-attempt token would leave a stale reservation that
            // denies new readers until its lease expires after this writer releases.
            var ownerToken = Guid.NewGuid().ToString("N");
            var deadline = Stopwatch.StartNew();
            var contended = false;
            // v2.1: jittered backoff instead of a flat interval. With no RetryBackoffCeiling the
            // jitter window collapses onto RetryInterval, so the default is byte-for-byte the old
            // behaviour; a ceiling breaks the lockstep that keeps N waiters waking together.
            var pollOptions = options.ToWaitPolicy().ToPollOptions();
            var rng = pollOptions.RandomFactory();
            // One counter for two jobs: the attempt_count metric and the backoff exponent. It is
            // incremented at the TOP of the loop, so the metric reports 1 for an uncontended
            // acquire; the backoff therefore takes attempts - 1 to start its curve at exponent 0.
            int attempts = 0;
            while (true)
            {
                attempts++;
                var handle = await TryAcquireAsync(key, ownerToken, mode, options, cancellationToken).ConfigureAwait(false);
                if (handle is not null)
                {
                    activity?.SetTag("orionlock.outcome", "acquired");
                    OrionLockDiagnostics.RecordAcquisition();
                    OrionLockDiagnostics.RecordAcquireDuration(deadline.Elapsed.TotalMilliseconds);
                    // Per-acquire attempt count for retry-interval sizing, same emission point and
                    // successful-acquires-only rule as the exclusive path.
                    OrionLockDiagnostics.RecordAcquireAttemptCount(attempts);
                    if (contended)
                    {
                        OrionLockDiagnostics.RecordContentionDuration(deadline.Elapsed.TotalMilliseconds);
                    }
                    // Safe-invoke swallows observer faults so audit-side outages cannot break acquires.
                    eventObserver.SafeOnAcquired(key, deadline.Elapsed.TotalMilliseconds);
                    return handle;
                }

                contended = true;
                OrionLockDiagnostics.RecordContention();

                var remaining = options.WaitTimeout - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    activity?.SetTag("orionlock.outcome", "timeout");
                    OrionLockDiagnostics.RecordAcquireTimeout(key);
                    // Notify the observer BEFORE the throw (mirrors the counter ordering above).
                    eventObserver.SafeOnAcquireTimedOut(key, deadline.Elapsed.TotalMilliseconds);
                    throw new LockAcquisitionTimeoutException(key, deadline.Elapsed);
                }

                // Clamp the poll delay to the time left until WaitTimeout so a full RetryInterval
                // near the deadline cannot overshoot the caller's wait budget by up to one interval.
                var backoff = Providers.DistributedLockProviderExtensions.ComputeJitteredDelay(
                    pollOptions, attempts - 1, rng);
                var delay = backoff < remaining ? backoff : remaining;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("orionlock.outcome", "cancelled");
            OrionLockDiagnostics.RecordAcquireCancelled();
            throw;
        }
    }
}
