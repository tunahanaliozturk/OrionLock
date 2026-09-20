namespace Moongazing.OrionLock.Consul;

using System.Collections.Concurrent;
using Moongazing.OrionLock.Providers;

/// <summary>
/// <see cref="IDistributedLockProvider"/> backed by Consul session-bound KV semantics.
/// Each (lockKey, ownerToken) pair gets a Consul session whose TTL is the OrionLock lease
/// duration; <see cref="TryAcquireAsync"/> issues a session-scoped KV acquire,
/// <see cref="TryRenewAsync"/> renews the session, and <see cref="ReleaseAsync"/> destroys
/// the session (which Consul propagates as a KV release).
/// </summary>
/// <remarks>
/// Session expiry semantics: when the holder process crashes, Consul's session TTL
/// eventually elapses and Consul applies the configured behaviour (<c>release</c> by
/// default, which puts the key back in the pool). Blocking waiters in the OrionLock core
/// see the key become free on their next polling tick.
/// </remarks>
public sealed class ConsulLockProvider : IDistributedLockProvider
{
    private readonly IConsulClientAdapter consul;
    private readonly ConsulLockOptions options;

    // (ownerToken, key) -> sessionId. Each acquire mints a fresh ownerToken, so in normal
    // OrionLock usage one ownerToken maps to one key; but the contract permits callers to
    // present the same ownerToken for different keys, and using the token alone would let
    // a Release for key A destroy a session that holds key B. Keying on (token, key)
    // ensures Renew and Release only touch the session whose original Acquire matched both.
    private readonly ConcurrentDictionary<(string Owner, string Key), string> ownerKeyToSession =
        new();

    /// <summary>Construct over a Consul adapter (production wires DefaultConsulClientAdapter).</summary>
    public ConsulLockProvider(IConsulClientAdapter consul, ConsulLockOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(consul);
        this.consul = consul;
        this.options = options ?? new ConsulLockOptions();
        // The prefix is spliced into the same /v1/kv/{path} HTTP path the key is, so it is validated
        // here too - not only in UseConsul - to cover a provider built by hand.
        this.options.ValidateAndNormalise();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Consul refuses a session TTL below 10 seconds, so this provider cannot honour a shorter lease.
    /// It used to raise one silently, which meant a caller who asked for 2 s and swapped Redis for
    /// Consul got a five-times-longer takeover window after a crash without being told.
    /// </remarks>
    public TimeSpan MinimumLeaseDuration => options.MinSessionTtl;

    private string FullKey(string lockKey) => options.KeyPrefix + lockKey;

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => (await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false))
            .Acquired;

    /// <inheritdoc />
    /// <remarks>
    /// Reports a token only when <see cref="ConsulLockOptions.FencingTokens"/> is on and the adapter
    /// implements <see cref="IConsulFencingAdapter"/>; see that option for the token's derivation and
    /// the round trip it costs.
    /// </remarks>
    public async Task<LockAcquisition> TryAcquireFencedAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        // The core refuses anything below MinimumLeaseDuration, so the requested lease IS the TTL.
        var sessionId = await consul.CreateSessionAsync(
                leaseDuration, options.SessionBehavior, options.LockDelay, cancellationToken)
            .ConfigureAwait(false);

        bool acquired;
        try
        {
            acquired = await consul.KvAcquireAsync(FullKey(key), ownerToken, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Any failure between CreateSession and KvAcquire MUST destroy the orphan session
            // so we do not leak the lease on the Consul server. CancellationToken.None is
            // deliberate: cleanup runs even if the outer call was cancelled.
            await consul.DestroySessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!acquired)
        {
            await consul.DestroySessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return LockAcquisition.NotAcquired;
        }

        long? modifyIndex = null;
        try
        {
            // Read the index back BEFORE publishing the mapping, so the same cleanup that covers a
            // failed mapping store covers a failed read. We hold the key here, so nothing else can
            // acquire it and move the index under us; an out-of-band write to the entry would only
            // push the index HIGHER, which keeps the token monotonic either way.
            if (options.FencingTokens && consul is IConsulFencingAdapter fencing)
            {
                modifyIndex = await fencing.KvModifyIndexAsync(FullKey(key), cancellationToken)
                    .ConfigureAwait(false);

                // A null index means the entry is not there - so between the acquire Consul just
                // granted and this read, the session was invalidated or the key was deleted. We do not
                // hold what we were told we hold. Treating that as "acquired, no token" would be the
                // worst of both: a caller who explicitly enabled fencing gets a handle with no token,
                // over a lock that may already belong to someone else. It is the same failure as a
                // throwing read, so it takes the same exit - the catch below gives the key back.
                if (modifyIndex is null)
                {
                    throw new OrionLockBackendException(
                        key,
                        "the KV entry disappeared between the successful acquire and the fencing-index "
                        + "read, so the session was invalidated or the key deleted and the lock is not "
                        + "actually held.");
                }
            }

            ownerKeyToSession[(ownerToken, key)] = sessionId;
        }
        catch
        {
            // Same protection if the index read or the mapping store fails (e.g. OOM). Release the KV
            // lock and destroy the session so we do not strand state in Consul that the local process
            // cannot recover - and so a caller that asked for a fenced acquire never receives a hold
            // whose token is quietly missing. The mapping is published only AFTER the read succeeds, so
            // nothing here can leave a (owner, key) entry pointing at a session we just destroyed.
            await consul.KvReleaseAsync(FullKey(key), sessionId, CancellationToken.None).ConfigureAwait(false);
            await consul.DestroySessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return modifyIndex is { } index ? LockAcquisition.Fenced(index) : LockAcquisition.Unfenced;
    }

    /// <summary>
    /// Waits on a Consul blocking query instead of re-attempting the acquire every retry interval.
    /// A failed attempt costs three round trips here - create session, KV acquire, destroy session -
    /// so the poll loop was paying three per waiter per tick; the blocking query pays them once and
    /// then lets Consul hold the request open until the key changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Consul's <c>release</c> session behaviour clears the session from the KV entry when a
    /// holder's session expires, so the blocking query covers a crashed holder as well as an
    /// explicit release.
    /// </para>
    /// <para>
    /// The query is a hint, never the authority: every wake re-attempts the acquire, because
    /// several waiters see the same change and only one of them can win. An adapter that does not
    /// implement blocking queries answers <see langword="false"/> immediately, and the core turns
    /// that back into the poll loop this replaced.
    /// </para>
    /// </remarks>
    public async Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var infinite = maxWait == Timeout.InfiniteTimeSpan;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The FENCED attempt, so a lock taken by WAITING carries the same token a lock taken on
            // the first attempt would have - otherwise fencing would work on an idle key and go
            // dark under exactly the contention it exists to protect against.
            var acquisition = await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Acquired)
            {
                return acquisition;
            }

            var remaining = maxWait - elapsed.Elapsed;
            if (!infinite && remaining <= TimeSpan.Zero)
            {
                return LockAcquisition.NotAcquired;
            }

            if (!await consul.WaitForKeyFreeAsync(
                    FullKey(key), infinite ? Timeout.InfiniteTimeSpan : remaining, cancellationToken)
                .ConfigureAwait(false))
            {
                // Budget spent, no blocking query available, or the query returned with the key
                // still held. Hand the wait back: the core sleeps the caller's retry floor and asks
                // again, which is the poll loop, reached only when the query is not doing its job.
                return LockAcquisition.NotAcquired;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerKeyToSession.TryGetValue((ownerToken, key), out var sessionId))
        {
            // We never held this (owner, key) pair - or the session was already destroyed.
            return false;
        }

        var renewed = await consul.RenewSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!renewed)
        {
            // Consul reports the session is gone; lease is lost. Drop our local mapping so a
            // subsequent renew does not spam the dead session id.
            ownerKeyToSession.TryRemove((ownerToken, key), out _);
        }
        return renewed;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerKeyToSession.TryRemove((ownerToken, key), out var sessionId))
        {
            return;
        }

        // Releasing the KV explicitly mirrors the v0.3.x semantics for other backends:
        // the lock becomes available immediately, not after Consul's lazy session GC.
        await consul.KvReleaseAsync(FullKey(key), sessionId, cancellationToken).ConfigureAwait(false);
        await consul.DestroySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }
}
