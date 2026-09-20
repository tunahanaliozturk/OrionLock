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
        // The fault guard belongs HERE, at this lock's own provider boundary, not in the AddOrionLock
        // factory. Installed there, the exception contract depended on how the lock was built: a caller
        // using this public constructor still got raw RedisException / SqlException / RpcException while
        // the interface documented that driver failures arrive as OrionLockBackendException. Two objects
        // of the same type with two different contracts is worse than no contract. Idempotent, so the
        // DI path does not double-wrap.
        this.provider = provider is BackendFaultGuard ? provider : new BackendFaultGuard(provider);
        this.fifoCoordinator = fifoCoordinator ?? new Fairness.NullFifoWaiterCoordinator();
        this.eventObserver = eventObserver is NullLockEventObserver ? null : eventObserver;
    }

    /// <summary>
    /// Refuses a lease the backend cannot honour, rather than letting the backend quietly raise it.
    /// Consul used to round a lease up to 10 seconds and etcd to 5, so a caller who set 2 s and swapped
    /// Redis for Consul got a five-times-longer takeover window after a crash with no warning - while
    /// the README promised application code never changes when you switch backends.
    /// </summary>
    private void ValidateLeaseAgainstBackend(DistributedLockOptions options)
    {
        var floor = provider.MinimumLeaseDuration;
        if (options.LeaseDuration < floor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.LeaseDuration,
                $"The registered backend cannot honour a lease shorter than {floor}; it would silently "
                + "hold the lock for longer than you asked, lengthening the takeover window after a "
                + "crash. Raise LeaseDuration to at least that, or pick a backend with a finer lease.");
        }
    }

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        LockKey.Validate(key);
        options ??= new DistributedLockOptions();
        options.ValidateAndNormalise();
        ValidateLeaseAgainstBackend(options);
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
        ValidateLeaseAgainstBackend(options);
        var owner = reentrancy.EnsureOwnerScope(key);

        // Mint the owner token ONCE and reuse it across every deadline-retry attempt, exactly as the
        // blocking AcquireAsync loop does. A fresh token per attempt would make each retry a DIFFERENT
        // logical acquirer and break fencing identity; reusing one keeps the deadline overload's fencing
        // identity stable, matching the reader-writer deadline overloads.
        var ownerToken = Guid.NewGuid().ToString("N");
        return TryAcquireUntilDeadlineAsync(key, ownerToken, owner, deadline, options, cancellationToken);
    }

    /// <summary>
    /// Sleeps the fallback poll interval after a wait that came back without a grant while budget
    /// remained - the dropped-subscription case. Without it a backend whose subscription keeps
    /// dropping would be re-asked in a tight loop for the rest of the caller's wait budget.
    /// </summary>
    private static async Task BackoffAfterEarlyWaitAsync(
        TimeSpan remaining, Providers.WaitForAcquireOptions pollOptions, Random rng, int attempts,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }
        var backoff = Providers.DistributedLockProviderExtensions.ComputeJitteredDelay(pollOptions, attempts, rng);
        await Task.Delay(backoff < remaining ? backoff : remaining, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Try-until-deadline for the exclusive path. Structurally the same as
    /// <see cref="DeadlineAcquire.TryAcquireUntilDeadlineAsync"/> - a non-positive deadline performs
    /// exactly one attempt and expiry returns <see langword="null"/> rather than throwing - but it
    /// waits through <see cref="Providers.IDistributedLockProvider.WaitForAcquireAsync"/>, which the
    /// shared helper cannot: the reader-writer provider it also serves is a different contract.
    /// </summary>
    private async Task<IDistributedLockHandle?> TryAcquireUntilDeadlineAsync(
        string key, string ownerToken, object owner, TimeSpan deadline,
        DistributedLockOptions options, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var waitPolicy = options.ToWaitPolicy();
        var pollOptions = waitPolicy.ToPollOptions();
        var rng = pollOptions.RandomFactory();
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            attempts++;
            var handle = await TryAcquireAsync(key, ownerToken, owner, options, cancellationToken)
                .ConfigureAwait(false);
            if (handle is not null)
            {
                return handle;
            }

            var remaining = deadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var waited = await provider.WaitForAcquireAsync(
                key, ownerToken, options.LeaseDuration, remaining, waitPolicy, cancellationToken)
                .ConfigureAwait(false);
            if (waited.Acquired)
            {
                return RegisterAcquired(key, ownerToken, owner, options, waited.FencingToken);
            }

            await BackoffAfterEarlyWaitAsync(
                deadline - elapsed.Elapsed, pollOptions, rng, attempts, cancellationToken).ConfigureAwait(false);
        }
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

        return acquired.Acquired
            ? RegisterAcquired(key, ownerToken, owner, options, acquired.FencingToken)
            : null;
    }

    /// <summary>
    /// Wraps a lease the backend has ALREADY granted in a handle and registers it for reentrancy.
    /// Split out of <see cref="TryAcquireAsync(string, string, object, DistributedLockOptions, CancellationToken)"/>
    /// because <see cref="Providers.IDistributedLockProvider.WaitForAcquireAsync"/> also returns a
    /// granted lease, and re-calling TryAcquire to "collect" it would both cost an extra round trip
    /// and fail - the lock is already held, by us.
    /// <para>
    /// <c>fencingToken</c> is the token the granting attempt minted, threaded through rather than
    /// re-read: a lock taken by WAITING is as entitled to its token as one taken on the first
    /// attempt, and dropping it here would leave fencing working on an idle key and silently dark
    /// under exactly the contention it exists to protect against.
    /// </para>
    /// </summary>
    private IDistributedLockHandle RegisterAcquired(
        string key, string ownerToken, object owner, DistributedLockOptions options, long? fencingToken)
    {
        // v0.3.25: thread the lifecycle observer into the handle so it can fire
        // OnLeaseLost / OnReleased.
        var real = new DistributedLockHandle(
            provider, key, ownerToken, options, eventObserver, fencingToken);
        // v0.3.13: increment the held-concurrent gauge ONLY when a real backend lease is
        // taken. Reentrant nested acquisitions and contention path returns (null) are
        // excluded. The handle's DisposeAsync / watchdog-loss paths decrement exactly once
        // via DecrementOnceIfHeld.
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
        ValidateLeaseAgainstBackend(options);
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
            var waitPolicy = options.ToWaitPolicy();
            var pollOptions = waitPolicy.ToPollOptions();
            var rng = pollOptions.RandomFactory();
            int attempts = 0;

            IDistributedLockHandle Granted(IDistributedLockHandle handle)
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
                // v0.3.22: per-acquire attempt count for retry-interval sizing. Only successful
                // acquires emit so cancelled / timed-out paths do not skew the distribution.
                // v2.1: this counts the attempts THIS LOOP issued. A backend that blocks or
                // subscribes does its own waiting inside one WaitForAcquireAsync call, so the
                // histogram collapses towards 2 for those - which is the win, stated in the
                // metric rather than hidden by it.
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

            while (true)
            {
                attempts++;
                var handle = await TryAcquireAsync(
                    key, ownerToken, owner, options, cancellationToken).ConfigureAwait(false);
                if (handle is not null)
                {
                    return Granted(handle);
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

                // v2.1: hand the wait to the backend with the caller's REMAINING budget, so a store
                // that can block or subscribe returns the instant the lock frees instead of being
                // asked again every RetryInterval. The default implementation of this member is the
                // poll loop this line replaced, so a provider that does not override it behaves
                // exactly as before - the delay is still clamped to the remaining budget, and it is
                // still the same owner token across every attempt.
                attempts++;
                var waited = await provider.WaitForAcquireAsync(
                    key, ownerToken, options.LeaseDuration, remaining, waitPolicy, cancellationToken)
                    .ConfigureAwait(false);
                if (waited.Acquired)
                {
                    return Granted(RegisterAcquired(key, ownerToken, owner, options, waited.FencingToken));
                }

                // False means the budget ran out OR the backend's subscription dropped early. The
                // loop handles both, but an early false must not turn into a hot spin against a
                // backend whose subscription keeps dropping: sleep the caller's retry floor first,
                // which is exactly the poll the wait degraded back to.
                await BackoffAfterEarlyWaitAsync(
                    options.WaitTimeout - deadline.Elapsed, pollOptions, rng, attempts, cancellationToken)
                    .ConfigureAwait(false);
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
