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
        var result = await client.KV.Get(ConsulKvPath.Encode(key), cancellationToken)
            .ConfigureAwait(false);
        // ModifyIndex is a ulong on the wire; Consul's Raft index will not reach the point where this
        // stops fitting in a long before the cluster has other problems.
        return result.Response is { } pair ? (long)pair.ModifyIndex : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two requests for the whole wait: one to learn the key's current modify index, one blocking
    /// query that Consul holds open until the entry changes past that index or the wait time
    /// elapses. The wait time is Consul's own, so the connection is released by the server rather
    /// than abandoned by the client, and cancellation propagates into the HTTP call - nothing is
    /// left outstanding when a caller gives up.
    /// </remarks>
    public async Task<bool> WaitForKeyFreeAsync(string key, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var path = ConsulKvPath.Encode(key);

        var current = await client.KV.Get(path, cancellationToken).ConfigureAwait(false);
        if (IsFree(current.Response))
        {
            // Already free between the caller's failed attempt and this read; say so rather than
            // blocking for a change that has already happened.
            return true;
        }

        // Consul caps its own wait at 10 minutes and adds jitter; anything longer is simply held
        // for as long as it will hold it, and the caller loops.
        var wait = maxWait == Timeout.InfiniteTimeSpan || maxWait > TimeSpan.FromMinutes(10)
            ? TimeSpan.FromMinutes(10)
            : maxWait;
        if (wait <= TimeSpan.Zero)
        {
            return false;
        }

        var blocked = await client.KV.Get(
            path,
            new QueryOptions { WaitIndex = current.LastIndex, WaitTime = wait },
            cancellationToken).ConfigureAwait(false);

        return IsFree(blocked.Response);
    }

    // A KV entry with no session on it is not held: either the holder released it, or its session
    // expired and Consul applied the 'release' behaviour. A missing entry is free for the same
    // reason. Anything else is still somebody's.
    private static bool IsFree(KVPair? pair)
        => pair is null || string.IsNullOrEmpty(pair.Session);

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
