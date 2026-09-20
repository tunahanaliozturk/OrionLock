namespace Moongazing.OrionLock.Consul;

/// <summary>
/// Thin abstraction over the subset of Consul KV / Session operations OrionLock needs.
/// Exists so the provider stays testable in isolation: production wires
/// <see cref="DefaultConsulClientAdapter"/> over the official Consul.NET client, unit tests
/// supply a mock or in-memory implementation.
/// </summary>
public interface IConsulClientAdapter
{
    /// <summary>Create a Consul session bound to the given TTL, behaviour, and lock-delay. Returns the session id.</summary>
    Task<string> CreateSessionAsync(TimeSpan ttl, string behavior, TimeSpan lockDelay, CancellationToken cancellationToken);

    /// <summary>Renew an existing session. Returns false when the session no longer exists (caller treats this as lease loss).</summary>
    Task<bool> RenewSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Destroy a session by id. Idempotent.</summary>
    Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>KV acquire-with-session. Returns true on lock grant, false on contention.</summary>
    Task<bool> KvAcquireAsync(string key, string ownerToken, string sessionId, CancellationToken cancellationToken);

    /// <summary>KV release-with-session. Returns true when the release matched the session.</summary>
    Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Optional companion to <see cref="IConsulClientAdapter"/> for an adapter that can read a key's
/// <c>ModifyIndex</c>. <see cref="DefaultConsulClientAdapter"/> implements it;
/// <see cref="ConsulLockProvider"/> uses it only when <see cref="ConsulLockOptions.FencingTokens"/> is
/// on, so an existing custom adapter keeps working untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ModifyIndex and not LockIndex.</b> Consul's <c>LockIndex</c> counts how many times a key has
/// been acquired, which reads exactly like a fencing token - and it is the wrong one. It lives on the KV
/// entry, so it dies with it: a session whose behaviour is <c>delete</c> removes the key on expiry, and
/// the next acquisition starts counting from 1 again, handing out a token some earlier holder already
/// used. <c>ModifyIndex</c> is the Raft log index at which the entry was last written. That index is
/// cluster-global, advances on every committed write, and is never reset or rewound - not by deleting
/// the key, not by session churn, not by a leader election - so it is strictly increasing per key across
/// acquisitions and across processes, which is what a fencing token has to be.
/// </para>
/// </remarks>
public interface IConsulFencingAdapter
{
    /// <summary>The <c>ModifyIndex</c> of <paramref name="key"/>, or null when the key does not exist.</summary>
    Task<long?> KvModifyIndexAsync(string key, CancellationToken cancellationToken);
}
