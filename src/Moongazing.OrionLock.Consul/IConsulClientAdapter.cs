namespace Moongazing.OrionLock.Consul;

/// <summary>
/// Thin abstraction over the subset of Consul KV / Session operations OrionLock needs.
/// Exists so the provider stays testable in isolation: production wires
/// <see cref="DefaultConsulClientAdapter"/> over the official Consul.NET client, unit tests
/// supply a mock or in-memory implementation.
/// </summary>
public interface IConsulClientAdapter
{
    /// <summary>Create a Consul session bound to the given TTL + behaviour. Returns the session id.</summary>
    Task<string> CreateSessionAsync(TimeSpan ttl, string behavior, CancellationToken cancellationToken);

    /// <summary>Renew an existing session. Returns false when the session no longer exists (caller treats this as lease loss).</summary>
    Task<bool> RenewSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Destroy a session by id. Idempotent.</summary>
    Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>KV acquire-with-session. Returns true on lock grant, false on contention.</summary>
    Task<bool> KvAcquireAsync(string key, string ownerToken, string sessionId, CancellationToken cancellationToken);

    /// <summary>KV release-with-session. Returns true when the release matched the session.</summary>
    Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken);
}
