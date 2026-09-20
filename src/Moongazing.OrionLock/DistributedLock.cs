using System.Diagnostics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock;

/// <summary>
/// The default <see cref="IDistributedLock"/>. Composes a backend <see cref="IDistributedLockProvider"/>
/// with a blocking-acquire retry loop, same-process reentrancy, and lease-renewing handles.
/// </summary>
public sealed class DistributedLock : IDistributedLock
{
    private readonly IDistributedLockProvider provider;
    private readonly Fairness.IFifoWaiterCoordinator fifoCoordinator;
    private readonly ReentrancyRegistry reentrancy = new();
    // v0.3.25 optional consumer-registered lifecycle observer. Null and
    // NullLockEventObserver are both treated as 'no observer'.
    private readonly ILockEventObserver? eventObserver;

    /// <summary>Creates a lock over the given backend provider.</summary>
    public DistributedLock(IDistributedLockProvider provider)
        : this(provider, fifoCoordinator: null, eventObserver: null)
    {
    }

    /// <summary>
    /// Creates a lock over the given backend provider with an optional FIFO waiter
    /// coordinator (v0.3.3). When <paramref name="fifoCoordinator"/> is null, a
    /// <see cref="Fairness.NullFifoWaiterCoordinator"/> is used so v0.3.2 behaviour is
    /// preserved unless the consumer opts in via
    /// <see cref="DistributedLockOptions.UseFifoWaiterCoordinator"/>.
    /// </summary>
    public DistributedLock(IDistributedLockProvider provider, Fairness.IFifoWaiterCoordinator? fifoCoordinator)
        : this(provider, fifoCoordinator, eventObserver: null)
    {
    }

    /// <summary>
    /// v0.3.25 ctor overload that also wires the optional
    /// <see cref="ILockEventObserver"/> announced (contract-only) in v0.3.24. The
    /// observer receives OnAcquired / OnAcquireTimedOut from this class and
    /// OnLeaseLost / OnReleased from the handles it creates.
    /// </summary>
    public DistributedLock(
        IDistributedLockProvider provider,
        Fairness.IFifoWaiterCoordinator? fifoCoordinator,
        ILockEventObserver? eventObserver)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
        this.fifoCoordinator = fifoCoordinator ?? new Fairness.NullFifoWaiterCoordinator();
        this.eventObserver = eventObserver is NullLockEventObserver ? null : eventObserver;
    }

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();
        // Establish the reentrancy owner scope HERE, in the caller's synchronous frame, so it survives
        // into the caller's critical section. See ReentrancyRegistry.EnsureOwnerScope.
        var owner = reentrancy.EnsureOwnerScope(key);
        return TryAcquireAsync(key, Guid.NewGuid().ToString("N"), owner, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, TimeSpan deadline, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();
        var owner = reentrancy.EnsureOwnerScope(key);

        // Mint the owner token ONCE and reuse it across every deadline-retry attempt, exactly as the
        // blocking AcquireAsync loop does. A fresh token per attempt would make each retry a DIFFERENT
        // logical acquirer and break fencing identity; reusing one keeps the deadline overload's fencing
        // identity stable, matching the reader-writer deadline overloads.
        var ownerToken = Guid.NewGuid().ToString("N");
        return DeadlineAcquire.TryAcquireUntilDeadlineAsync(
            (k, o, ct) => TryAcquireAsync(k, ownerToken, owner, o!, ct), key, deadline, options, cancellationToken);
    }

    private async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, string ownerToken, object owner, DistributedLockOptions options, CancellationToken cancellationToken)
    {
        var nested = reentrancy.TryEnter(key, owner);
        if (nested is not null)
        {
            return nested;
        }

        // The fenced overload is the one the core calls. Its interface default simply forwards to
        // TryAcquireAsync and reports no token, so a backend that cannot fence is unaffected; a backend
        // that can mints the token inside the same atomic step that grants the lock, which is the only
        // way the token and the acquisition cannot come apart.
        var acquired = await provider
            .TryAcquireFencedAsync(key, ownerToken, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired.Acquired)
        {
            return null;
        }

        // v0.3.25: thread the lifecycle observer into the handle so it can fire
        // OnLeaseLost / OnReleased.
        var real = new DistributedLockHandle(
            provider, key, ownerToken, options, eventObserver, acquired.FencingToken);
        // v0.3.13: increment the held-concurrent gauge ONLY when a real backend lease is
        // taken. Reentrant nested acquisitions (returned above) and contention path
        // returns (null) are excluded. The handle's DisposeAsync / watchdog-loss paths
        // decrement exactly once via DecrementOnceIfHeld.
        OrionLockDiagnostics.IncrementLeasesHeld();
        return reentrancy.Register(key, owner, real);
    }

    /// <inheritdoc />
    public Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();
        // Deliberately NOT an async method: EnsureOwnerScope must run in the caller's own execution
        // context (an async body's context changes are discarded when it returns), so the blocking
        // acquire is a thin synchronous shim over the async core.
        var owner = reentrancy.EnsureOwnerScope(key);
        return AcquireCoreAsync(key, owner, options, cancellationToken);
    }

    private async Task<IDistributedLockHandle> AcquireCoreAsync(
        string key, object owner, DistributedLockOptions options, CancellationToken cancellationToken)
    {
        // Hot path: only build the interpolated activity name when a listener is actually
        // subscribed. With no listener StartActivity returns null and the name is never
        // observed, so the per-acquire string allocation is pure waste. HasListeners() gates
        // it without changing the activity that a subscribed listener sees.
        using var activity = OrionLockDiagnostics.ActivitySource.HasListeners()
            ? OrionLockDiagnostics.ActivitySource.StartActivity($"OrionLock.Acquire {key}")
            : null;
        activity?.SetTag("orionlock.key", key);

        // v0.3.3: opt-in FIFO ordering. When enabled, the caller waits for its turn at the
        // head of the per-key queue BEFORE entering the polling-retry loop. LeaveAsync runs
        // in a finally so a thrown timeout / cancellation does not strand subsequent waiters.
        Fairness.IFifoWaiterTicket? ticket = null;
        if (options.UseFifoWaiterCoordinator)
        {
            var fifoSw = Stopwatch.StartNew();
            ticket = await fifoCoordinator.EnterAsync(key, cancellationToken).ConfigureAwait(false);
            fifoSw.Stop();
            // v0.3.18: record only the FIFO ticket wait so operators can isolate
            // head-of-line block latency from the backend acquisition latency.
            OrionLockDiagnostics.RecordFifoCoordinatorEnter(fifoSw.Elapsed.TotalMilliseconds);
        }

        try
        {
            // Mint the owner token ONCE and reuse it across every poll attempt of this blocking
            // acquire, matching the deadline overload above and SharedExclusiveLock.AcquireAsync.
            // A fresh token per attempt would make each retry a DIFFERENT logical acquirer, breaking
            // fencing identity and orphaning anything a partially-succeeded attempt left behind under
            // a token no later retry can reclaim.
            var ownerToken = Guid.NewGuid().ToString("N");
            var deadline = Stopwatch.StartNew();
            var contended = false;
            int attempts = 0;
            while (true)
            {
                attempts++;
                var handle = await TryAcquireAsync(
                    key, ownerToken, owner, options, cancellationToken).ConfigureAwait(false);
                if (handle is not null)
                {
                    activity?.SetTag("orionlock.outcome", "acquired");
                    // The token goes on the SPAN and nowhere near a metric tag: it is unique per
                    // acquisition, so as a metric dimension it would mint a fresh time series for every
                    // single acquire. Spans are sampled and stored per-trace, which is what makes the
                    // same value affordable there. See docs/lock-key-cardinality.md - the reasoning is
                    // identical to the one that keeps the raw key off the Meter.
                    if (handle.FencingToken is { } fencingToken)
                    {
                        activity?.SetTag("orionlock.fencing_token", fencingToken);
                    }
                    OrionLockDiagnostics.RecordAcquisition();
                    OrionLockDiagnostics.RecordAcquireDuration(deadline.Elapsed.TotalMilliseconds);
                    // v0.3.22: per-acquire attempt count for retry-interval sizing.
                    // Only successful acquires emit so cancelled / timed-out paths do
                    // not skew the distribution.
                    OrionLockDiagnostics.RecordAcquireAttemptCount(attempts);
                    // v0.3.15: only contended acquires emit on the contention histogram
                    // so its p99 reflects actual contention pressure rather than being
                    // diluted by uncontested happy paths.
                    if (contended)
                    {
                        OrionLockDiagnostics.RecordContentionDuration(deadline.Elapsed.TotalMilliseconds);
                    }
                    // v0.3.25: wire-up of the v0.3.24 contract. Safe-invoke swallows
                    // observer faults so audit-side outages cannot break acquires.
                    eventObserver.SafeOnAcquired(
                        key, deadline.Elapsed.TotalMilliseconds, handle.FencingToken);
                    return handle;
                }

                contended = true;
                OrionLockDiagnostics.RecordContention();

                var remaining = options.WaitTimeout - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    activity?.SetTag("orionlock.outcome", "timeout");
                    // v0.3.23: emit timeout with the hashed-bucket key tag so operators
                    // can use topk() to find the keys driving the most timeouts.
                    OrionLockDiagnostics.RecordAcquireTimeout(key);
                    // v0.3.25: notify the observer BEFORE the throw (mirrors the
                    // counter ordering above).
                    eventObserver.SafeOnAcquireTimedOut(key, deadline.Elapsed.TotalMilliseconds);
                    throw new LockAcquisitionTimeoutException(key, deadline.Elapsed);
                }

                // Clamp the poll delay to the time left until WaitTimeout so a full RetryInterval near
                // the deadline cannot overshoot the caller's wait budget by up to one interval, matching
                // SharedExclusiveLock.AcquireAsync and the deadline overload.
                var delay = options.RetryInterval < remaining ? options.RetryInterval : remaining;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // v0.3.28: the caller abandoned the acquire (graceful shutdown / client gave up),
            // distinct from a WaitTimeout SLO breach. Count it separately so the timeout signal
            // stays clean for alerting, then let the cancellation propagate unchanged.
            activity?.SetTag("orionlock.outcome", "cancelled");
            OrionLockDiagnostics.RecordAcquireCancelled();
            throw;
        }
        finally
        {
            if (ticket is not null)
            {
                await fifoCoordinator.LeaveAsync(ticket, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Acquires the lock using a lease of <paramref name="leaseDuration"/> and default wait/retry.</summary>
    public Task<IDistributedLockHandle> AcquireAsync(
        string key, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => AcquireAsync(key, new DistributedLockOptions { LeaseDuration = leaseDuration }, cancellationToken);
}
