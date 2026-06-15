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
    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var nested = reentrancy.TryEnter(key);
        if (nested is not null)
        {
            return nested;
        }

        var ownerToken = Guid.NewGuid().ToString("N");
        var acquired = await provider
            .TryAcquireAsync(key, ownerToken, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            return null;
        }

        // v0.3.25: thread the lifecycle observer into the handle so it can fire
        // OnLeaseLost / OnReleased.
        var real = new DistributedLockHandle(provider, key, ownerToken, options, eventObserver);
        // v0.3.13: increment the held-concurrent gauge ONLY when a real backend lease is
        // taken. Reentrant nested acquisitions (returned above) and contention path
        // returns (null) are excluded. The handle's DisposeAsync / watchdog-loss paths
        // decrement exactly once via DecrementOnceIfHeld.
        OrionLockDiagnostics.IncrementLeasesHeld();
        return reentrancy.Register(key, real);
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        using var activity = OrionLockDiagnostics.ActivitySource.StartActivity($"OrionLock.Acquire {key}");
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
            var deadline = Stopwatch.StartNew();
            var contended = false;
            int attempts = 0;
            while (true)
            {
                attempts++;
                var handle = await TryAcquireAsync(key, options, cancellationToken).ConfigureAwait(false);
                if (handle is not null)
                {
                    activity?.SetTag("orionlock.outcome", "acquired");
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
                    eventObserver.SafeOnAcquired(key, deadline.Elapsed.TotalMilliseconds);
                    return handle;
                }

                contended = true;
                OrionLockDiagnostics.RecordContention();

                if (deadline.Elapsed >= options.WaitTimeout)
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

                await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
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
