namespace Moongazing.OrionLock.Consul;

using System.Text;
using global::Consul;

/// <summary>
/// Default <see cref="IConsulClientAdapter"/> over the official
/// <see cref="IConsulClient"/>. Production wiring; unit tests substitute their own adapter.
/// </summary>
public sealed class DefaultConsulClientAdapter : IConsulClientAdapter, IConsulFencingAdapter
{
    private readonly IConsulClient client;

    /// <summary>Construct with an already-resolved Consul client.</summary>
    public DefaultConsulClientAdapter(IConsulClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <inheritdoc />
    public async Task<string> CreateSessionAsync(TimeSpan ttl, string behavior, TimeSpan lockDelay, CancellationToken cancellationToken)
    {
        var entry = new SessionEntry
        {
            TTL = ttl,
            LockDelay = lockDelay,
            Behavior = string.Equals(behavior, "delete", StringComparison.OrdinalIgnoreCase)
                ? SessionBehavior.Delete
                : SessionBehavior.Release,
        };
        var result = await client.Session.Create(entry, cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    /// <inheritdoc />
    public async Task<bool> RenewSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.Session.Renew(sessionId, cancellationToken).ConfigureAwait(false);
            return result.Response is not null;
        }
        catch (SessionExpiredException)
        {
            // Consul.NET raises a typed exception for "session no longer valid"; treat as a
            // lease-lost signal so the OrionLock core can react accordingly.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await client.Session.Destroy(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionExpiredException)
        {
            // Already gone - destroy is idempotent from our caller's perspective.
        }
    }

    /// <inheritdoc />
    public async Task<bool> KvAcquireAsync(string key, string ownerToken, string sessionId, CancellationToken cancellationToken)
    {
        // The path is spliced into /v1/kv/{path} as a URI; see ConsulKvPath. The key itself was already
        // validated by the core (Moongazing.OrionLock.LockKey) before it reached this provider.
        var pair = new KVPair(ConsulKvPath.Encode(key))
        {
            Value = Encoding.UTF8.GetBytes(ownerToken),
            Session = sessionId,
        };
        var result = await client.KV.Acquire(pair, cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A separate GET, because Consul's acquire response body is a bare <c>true</c>/<c>false</c> and
    /// carries no index - which is exactly why fencing on this backend is opt-in.
    /// </remarks>
    public async Task<long?> KvModifyIndexAsync(string key, CancellationToken cancellationToken)
    {
        var result = await client.KV.Get(ConsulKvPath.Encode(key, nameof(key)), cancellationToken)
            .ConfigureAwait(false);
        // ModifyIndex is a ulong on the wire; Consul's Raft index will not reach the point where this
        // stops fitting in a long before the cluster has other problems.
        return result.Response is { } pair ? (long)pair.ModifyIndex : null;
    }

    /// <inheritdoc />
    public async Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken)
    {
        var pair = new KVPair(ConsulKvPath.Encode(key))
        {
            Session = sessionId,
        };
        var result = await client.KV.Release(pair, cancellationToken).ConfigureAwait(false);
        return result.Response;
    }
}
