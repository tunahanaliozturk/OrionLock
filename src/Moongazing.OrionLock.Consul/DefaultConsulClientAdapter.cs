namespace Moongazing.OrionLock.Consul;

using System.Text;
using global::Consul;

/// <summary>
/// Default <see cref="IConsulClientAdapter"/> over the official
/// <see cref="IConsulClient"/>. Production wiring; unit tests substitute their own adapter.
/// </summary>
public sealed class DefaultConsulClientAdapter : IConsulClientAdapter
{
    private readonly IConsulClient client;

    /// <summary>Construct with an already-resolved Consul client.</summary>
    public DefaultConsulClientAdapter(IConsulClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <inheritdoc />
    public async Task<string> CreateSessionAsync(TimeSpan ttl, string behavior, CancellationToken cancellationToken)
    {
        var entry = new SessionEntry
        {
            TTL = ttl,
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
        var pair = new KVPair(key)
        {
            Value = Encoding.UTF8.GetBytes(ownerToken),
            Session = sessionId,
        };
        var result = await client.KV.Acquire(pair, cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    /// <inheritdoc />
    public async Task<bool> KvReleaseAsync(string key, string sessionId, CancellationToken cancellationToken)
    {
        var pair = new KVPair(key)
        {
            Session = sessionId,
        };
        var result = await client.KV.Release(pair, cancellationToken).ConfigureAwait(false);
        return result.Response;
    }
}
