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
    }

    private string FullKey(string lockKey) => options.KeyPrefix + lockKey;

    private TimeSpan SessionTtl(TimeSpan requestedLease)
        => requestedLease > options.MinSessionTtl ? requestedLease : options.MinSessionTtl;

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var ttl = SessionTtl(leaseDuration);
        var sessionId = await consul.CreateSessionAsync(ttl, options.SessionBehavior, options.LockDelay, cancellationToken)
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
            return false;
        }

        try
        {
            ownerKeyToSession[(ownerToken, key)] = sessionId;
        }
        catch
        {
            // Same protection if the mapping store throws (e.g. OOM). Release the KV lock
            // and destroy the session so we do not strand state in Consul that the local
            // process cannot recover.
            await consul.KvReleaseAsync(FullKey(key), sessionId, CancellationToken.None).ConfigureAwait(false);
            await consul.DestroySessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return true;
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
