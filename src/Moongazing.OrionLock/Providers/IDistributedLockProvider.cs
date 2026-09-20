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
}
