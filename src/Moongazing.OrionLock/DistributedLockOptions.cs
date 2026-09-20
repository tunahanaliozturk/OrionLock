namespace Moongazing.OrionLock;

/// <summary>Per-acquisition options for <see cref="IDistributedLock"/>.</summary>
public sealed class DistributedLockOptions
{
    /// <summary>How long the lease is valid before it expires. Default 30 seconds.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a blocking <see cref="IDistributedLock.AcquireAsync"/> waits. Default 10 seconds.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay between acquisition attempts inside a blocking acquire. Default 250 ms.</summary>
    /// <remarks>
    /// Since v2.1 this is the FLOOR of the wait, not the whole of it: a backend that can block or
    /// subscribe returns the moment the lock frees and never sleeps an interval at all. It still
    /// bounds the poll a backend falls back to, and it is still the shortest a fallback poll sleeps
    /// when <see cref="RetryBackoffCeiling"/> raises the upper bound.
    /// </remarks>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Upper bound of the fallback poll's exponential backoff. <see langword="null"/> - the default -
    /// keeps the flat <see cref="RetryInterval"/> every release before v2.1 used.
    /// </summary>
    /// <remarks>
    /// Set it and each unsuccessful poll sleeps a random duration in
    /// <c>[RetryInterval, min(RetryInterval * 2^attempts, RetryBackoffCeiling)]</c>. The randomness
    /// is the point: a flat interval keeps N waiters that arrived together waking on the same tick
    /// forever, so the store sees an N-wide burst every interval for the whole queue drain. A value
    /// below <see cref="RetryInterval"/> is ignored (the floor wins).
    /// </remarks>
    public TimeSpan? RetryBackoffCeiling { get; set; }

    /// <summary>When true, a background watchdog re-extends the lease while the handle is alive. Default true.</summary>
    public bool AutoRenew { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, <see cref="IDistributedLock.AcquireAsync"/> consults the
    /// registered <see cref="Fairness.IFifoWaiterCoordinator"/> so callers acquire the lock in
    /// arrival order under contention rather than racing on the polling-retry loop. Default
    /// <see langword="false"/> preserves v0.3.2 behaviour. The coordinator is consulted only
    /// during blocking <c>AcquireAsync</c>; non-blocking <c>TryAcquireAsync</c> bypasses it.
    /// </summary>
    /// <remarks>
    /// Honours the registered DI <see cref="Fairness.IFifoWaiterCoordinator"/> implementation.
    /// In-process coordination ships with <see cref="Fairness.InProcessFifoWaiterCoordinator"/>;
    /// distributed (cross-process) backends are on the v0.3.x roadmap.
    /// </remarks>
    public bool UseFifoWaiterCoordinator { get; set; }

    /// <summary>
    /// Fairness watchdog grace period. When the renewal loop hits an exception (transient
    /// backend fault), v0.3.9 and earlier continued retrying indefinitely. v0.3.10 lets
    /// the watchdog give up after a grace period since the last successful renewal -
    /// after this elapses without a successful renewal, the lock is treated as confirmed
    /// lost and auto-released so a stuck backend cannot perpetually deny new waiters.
    /// Defaults to <see langword="null"/> = the value of <see cref="LeaseDuration"/>
    /// (matching the lease's natural TTL).
    /// </summary>
    public TimeSpan? RenewalFailureGracePeriod { get; set; }

    /// <summary>
    /// How long the renewal watchdog keeps a hold alive before giving up on it. Defaults to
    /// <see langword="null"/> = ten times <see cref="LeaseDuration"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The watchdog task roots the handle, so a handle that is never disposed - a forgotten
    /// <c>await using</c>, the likeliest mistake with this API - used to renew its lease forever: the
    /// lock was never released, no other process could ever take the key, and on SQL Server and
    /// PostgreSQL a dedicated open connection stayed pinned for the life of the process. Nothing ever
    /// noticed, because renewal kept succeeding.
    /// </para>
    /// <para>
    /// Once this elapses since acquisition the watchdog stops renewing, surrenders the hold
    /// (<see cref="IDistributedLockHandle.IsHeld"/> goes false, <see cref="IDistributedLockHandle.LostToken"/>
    /// trips) and runs a best-effort release, so the key comes back even on the session-scoped backends
    /// where simply not renewing would free nothing. Only the watchdog is bounded: with
    /// <see cref="AutoRenew"/> off there is nothing renewing to stop, and the backend's own TTL already
    /// decides the hold's lifetime.
    /// </para>
    /// <para>
    /// The default is deliberately generous - it is a backstop for a leak, not a work deadline. Raise it
    /// for a genuinely long critical section; a hold that legitimately outlives it should say so rather
    /// than be surrendered mid-flight.
    /// </para>
    /// </remarks>
    public TimeSpan? MaxHoldDuration { get; set; }

    /// <summary>
    /// Validates the option values, on the caller's own thread at acquire time. Every acquire entry
    /// point calls this, so a misconfigured value is reported where it was set rather than as a driver
    /// error from inside the retry loop or the renewal watchdog - or, worse, not at all.
    /// </summary>
    /// <remarks>
    /// The same job <see cref="Providers.WaitForAcquireOptions.ValidateAndNormalise"/> already did for
    /// the polling helper one file over. Deliberately NOT rejected here, because both already work:
    /// a <see cref="RetryInterval"/> longer than <see cref="WaitTimeout"/> is clamped to the remaining
    /// budget by the acquire loop rather than overshooting it, and a <see cref="LeaseDuration"/> shorter
    /// than <see cref="RetryInterval"/> acquires normally - an uncontended acquire never waits at all,
    /// and under contention a short lease frees the key sooner, not later.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">An option value is outside its supported range.</exception>
    internal void ValidateAndNormalise()
    {
        if (LeaseDuration <= TimeSpan.Zero)
        {
            // Zero is not "a very short lease": on Redis it is SET ... PX 0, which is a DELETE, and the
            // Redis and PostgreSQL reader-writer providers reject it outright. A negative one also
            // collapses the renewal interval to its 10 ms floor, so the watchdog hammers the backend
            // for the life of the handle.
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration), LeaseDuration, "LeaseDuration must be positive.");
        }

        if (WaitTimeout < TimeSpan.Zero)
        {
            // Not Timeout.InfiniteTimeSpan either: the acquire loop measures the budget by subtraction,
            // so a negative value is an immediate timeout wearing the costume of an unbounded wait.
            throw new ArgumentOutOfRangeException(
                nameof(WaitTimeout), WaitTimeout,
                "WaitTimeout cannot be negative. Use TimeSpan.Zero for a single attempt; there is no "
                + "infinite value - pass a bounded timeout.");
        }

        if (RetryInterval <= TimeSpan.Zero)
        {
            // A negative interval throws from inside Task.Delay, deep in the acquire loop, with a stack
            // that names OrionLock rather than the caller. Zero is a busy spin on the backend.
            throw new ArgumentOutOfRangeException(
                nameof(RetryInterval), RetryInterval, "RetryInterval must be positive.");
        }

        if (RenewalFailureGracePeriod is { } grace && grace <= TimeSpan.Zero)
        {
            // Zero means the watchdog surrenders a perfectly good lease on the first transient blip.
            throw new ArgumentOutOfRangeException(
                nameof(RenewalFailureGracePeriod), grace,
                "RenewalFailureGracePeriod must be positive when set; leave it null to default to "
                + "LeaseDuration.");
        }

        if (MaxHoldDuration is { } maxHold && maxHold < LeaseDuration)
        {
            // Shorter than one lease means the watchdog gives up before it has renewed even once, which
            // is not a leak backstop but an immediate surrender.
            throw new ArgumentOutOfRangeException(
                nameof(MaxHoldDuration), maxHold,
                "MaxHoldDuration must be at least LeaseDuration when set; leave it null to default to "
                + "ten times LeaseDuration.");
        }
    }

    /// <summary>
    /// The watchdog's give-up deadline: <see cref="MaxHoldDuration"/>, or ten leases when unset.
    /// Saturates instead of overflowing on an absurdly long lease.
    /// </summary>
    internal TimeSpan ResolvedMaxHoldDuration =>
        MaxHoldDuration ?? (LeaseDuration.Ticks > TimeSpan.MaxValue.Ticks / 10
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(LeaseDuration.Ticks * 10));
    /// <summary>The wait shape these options describe, as the provider contract takes it.</summary>
    internal Providers.LockWaitPolicy ToWaitPolicy() => new(RetryInterval, RetryBackoffCeiling);
}
