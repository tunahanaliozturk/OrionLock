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

    // ownerToken -> sessionId. Each owner gets one session per active lock so renew/release
    // can find the matching Consul session without re-creating it. Keyed by ownerToken
    // because the OrionLock core mints a fresh token per acquire so the cardinality is
    // bounded by active locks per process.
    private readonly ConcurrentDictionary<string, string> ownerToSession = new(StringComparer.Ordinal);

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
        var sessionId = await consul.CreateSessionAsync(ttl, options.SessionBehavior, cancellationToken)
            .ConfigureAwait(false);

        var acquired = await consul.KvAcquireAsync(FullKey(key), ownerToken, sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!acquired)
        {
            // Lost the race; release the orphan session so we don't leak holds on the Consul
            // server.
            await consul.DestroySessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        ownerToSession[ownerToken] = sessionId;
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerToSession.TryGetValue(ownerToken, out var sessionId))
        {
            // We never held this lock - or the session was already destroyed.
            return false;
        }

        var renewed = await consul.RenewSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!renewed)
        {
            // Consul reports the session is gone; lease is lost. Drop our local mapping so a
            // subsequent renew doesn't spam the dead session id.
            ownerToSession.TryRemove(ownerToken, out _);
        }
        return renewed;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerToSession.TryRemove(ownerToken, out var sessionId))
        {
            return;
        }

        // Releasing the KV explicitly mirrors the v0.3.x semantics for other backends:
        // the lock becomes available immediately, not after Consul's lazy session GC.
        await consul.KvReleaseAsync(FullKey(key), sessionId, cancellationToken).ConfigureAwait(false);
        await consul.DestroySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }
}
