namespace Moongazing.OrionLock.Providers;

/// <summary>
/// The raw, single-attempt lock primitive a backend implements. The core OrionLock package
/// composes reentrancy, lease renewal, and blocking-acquire retry on top of this.
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>Tries once, without waiting, to acquire <paramref name="key"/> for <paramref name="ownerToken"/>.</summary>
    Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// The same single attempt as <see cref="TryAcquireAsync"/>, but also reporting the fencing token
    /// this acquisition minted. The core calls THIS overload; the default implementation delegates to
    /// <see cref="TryAcquireAsync"/> and reports no token, so a backend that cannot mint one needs no
    /// change at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fencing token is a number that strictly increases with every successful acquisition OF THE SAME
    /// KEY, across processes. The holder passes it to the resource it is protecting and the resource
    /// rejects any write carrying a token lower than the highest it has already seen - which is the only
    /// known defence against a holder that paused (GC, VM migration), lost its lease without noticing and
    /// then woke up and wrote. See <c>docs/fencing-tokens.md</c>.
    /// </para>
    /// <para>
    /// A backend that overrides this MUST mint the token in the SAME atomic step that grants the lock,
    /// and MUST return <see langword="null"/> if the best it can offer is only nearly monotonic. Callers
    /// trust a token; a number that looks like one but occasionally repeats or goes backwards is worse
    /// than admitting there is none.
    /// </para>
    /// </remarks>
    async Task<LockAcquisition> TryAcquireFencedAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => await TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false)
            ? LockAcquisition.Unfenced
            : LockAcquisition.NotAcquired;

    /// <summary>Extends the lease if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Releases the lock if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken);

    /// <summary>
    /// v0.3.21: True when the backend honours <c>leaseDuration</c> as a wall-clock TTL
    /// (Redis, in-memory). False when the backend holds the lock for the lifetime of an
    /// open session/transaction regardless of the supplied duration (PostgreSQL
    /// advisory locks, SQL Server sp_getapplock). Lease-expiration diagnostics
    /// (<c>orionlock.lease.expired_before_release</c>) are gated on this so
    /// session-scoped backends do not produce false positives when a caller legitimately
    /// holds the lock longer than the configured <c>LeaseDuration</c>.
    /// </summary>
    bool LeaseDurationIsTtl => true;

    /// <summary>
    /// The shortest lease this backend can honour. A requested
    /// <see cref="DistributedLockOptions.LeaseDuration"/> below this is rejected by the core with
    /// <see cref="ArgumentOutOfRangeException"/> at acquire time. Defaults to
    /// <see cref="TimeSpan.Zero"/> - no floor.
    /// </summary>
    /// <remarks>
    /// Backends used to raise a too-short lease silently: Consul to 10 seconds, etcd to 5. A caller who
    /// set 2 s and swapped Redis for Consul got a takeover window five times longer than they asked for
    /// after a crash, with no warning - while the README promised that application code never changes
    /// when you switch backends. Refusing is the honest answer: the caller either raises the lease or
    /// picks a backend that can honour it.
    /// </remarks>
    TimeSpan MinimumLeaseDuration => TimeSpan.Zero;

    /// <summary>
    /// The lease this backend will ACTUALLY honour for <paramref name="requested"/>, as reported by
    /// <see cref="IDistributedLockHandle.EffectiveLeaseDuration"/> so a caller can assert on it.
    /// </summary>
    /// <remarks>
    /// The default says what <see cref="LeaseDurationIsTtl"/> already implies: a TTL backend honours the
    /// requested duration as a wall clock, and a session-scoped one (PostgreSQL advisory locks, SQL
    /// Server <c>sp_getapplock</c>, ZooKeeper ephemeral znodes) does not bound the hold by clock at all,
    /// which is reported as <see cref="Timeout.InfiniteTimeSpan"/>. Only a backend that rounds - etcd,
    /// which takes whole-second TTLs - needs to override.
    /// </remarks>
    TimeSpan EffectiveLeaseDuration(TimeSpan requested)
        => LeaseDurationIsTtl ? requested : Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Waits up to <paramref name="maxWait"/> for <paramref name="key"/> to become acquirable and
    /// takes it for <paramref name="ownerToken"/>. Reports the acquisition exactly as
    /// <see cref="TryAcquireFencedAsync"/> does, including the fencing token; a
    /// <see cref="LockAcquisition.Acquired"/> of <see langword="false"/> means the budget ran out or
    /// the backend's wait ended early without a grant. Cancellation surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation is the poll loop the core used to run inline: repeated
    /// <see cref="TryAcquireFencedAsync"/> with the caller's
    /// <see cref="LockWaitPolicy.RetryInterval"/> as the floor. Every existing and third-party
    /// provider therefore keeps behaving exactly as it did without touching a line of its code.
    /// </para>
    /// <para>
    /// It reports a <see cref="LockAcquisition"/> rather than a bare <see langword="bool"/> for one
    /// reason: a lock taken by WAITING is as entitled to its fencing token as one taken by the first
    /// attempt. A bool here would mint no token for any contended acquire, so fencing would hold on
    /// an idle key and quietly go dark under exactly the contention it exists to protect against.
    /// An override MUST therefore carry the token its winning attempt produced.
    /// </para>
    /// <para>
    /// Override it when the store can say "the lock is free now" instead of being asked 40 times a
    /// second: SQL Server's own lock queue, PostgreSQL's blocking <c>pg_advisory_lock</c>, a Redis
    /// release notification, an etcd watch, a Consul blocking query, a ZooKeeper predecessor watch.
    /// An override MUST still honour <paramref name="maxWait"/> and
    /// <paramref name="cancellationToken"/>, and MUST leave no connection, watch or subscription
    /// parked behind a caller that gave up.
    /// </para>
    /// <para>
    /// Reporting not-acquired before <paramref name="maxWait"/> elapses is legal and is how a
    /// dropped subscription is reported: the core re-checks the remaining budget and calls again, so
    /// the wait degrades to polling rather than failing.
    /// </para>
    /// </remarks>
    /// <param name="key">Lock key.</param>
    /// <param name="ownerToken">Caller-supplied owner identity, stable across the whole wait.</param>
    /// <param name="leaseDuration">TTL applied on success.</param>
    /// <param name="maxWait">Remaining wait budget. Never negative; may be <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="waitPolicy">What to do while waiting when the backend has to fall back to polling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LockAcquisition> WaitForAcquireAsync(
        string key,
        string ownerToken,
        TimeSpan leaseDuration,
        TimeSpan maxWait,
        LockWaitPolicy waitPolicy,
        CancellationToken cancellationToken)
        => DistributedLockProviderExtensions.PollUntilAcquiredAsync(
            this, key, ownerToken, leaseDuration, maxWait, waitPolicy.ToPollOptions(),
            attempt: null, cancellationToken);
}
