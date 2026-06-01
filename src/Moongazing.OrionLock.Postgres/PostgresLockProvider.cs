using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Postgres;

/// <summary>
/// PostgreSQL <c>pg_try_advisory_lock</c> backed <see cref="IDistributedLockProvider"/>.
/// Holds one dedicated <see cref="NpgsqlConnection"/> per active lock; the lock lifetime
/// IS the session lifetime, so a crashed process releases its locks automatically.
/// </summary>
[BackendName("postgres")]
public sealed class PostgresLockProvider : IDistributedLockProvider, IDisposable
{
    private readonly string connectionString;
    private readonly PostgresLockOptions options;
    private readonly ConcurrentDictionary<string, SessionEntry> sessions = new();

    private sealed record SessionEntry(string Resource, long HashedKey, NpgsqlConnection Connection);

    /// <summary>Creates the provider.</summary>
    public PostgresLockProvider(string connectionString, PostgresLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        if (options.KeyPrefix is null)
        {
            throw new ArgumentException(
                $"{nameof(PostgresLockOptions)}.{nameof(PostgresLockOptions.KeyPrefix)} cannot be null; use string.Empty for no prefix.",
                nameof(options));
        }
        this.connectionString = connectionString;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var resource = options.KeyPrefix + key;
        var hashed = HashKey(resource);
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            bool acquired;
            await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", conn))
            {
                cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
                cmd.Parameters.AddWithValue("@key", hashed);
                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                acquired = result is true;
            }

            if (!acquired)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            if (!sessions.TryAdd(ownerToken, new SessionEntry(resource, hashed, conn)))
            {
                // ownerToken collision: vanishingly unlikely with GUIDs, but defensive.
                await ReleaseInSession(conn, hashed, cancellationToken).ConfigureAwait(false);
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"ownerToken '{ownerToken}' already registered.");
            }
            return true;
        }
        catch
        {
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already failing */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var resource = options.KeyPrefix + key;
        if (!sessions.TryGetValue(ownerToken, out var session) ||
            !string.Equals(session.Resource, resource, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await using var cmd = new NpgsqlCommand("SELECT 1", session.Connection);
            cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Connection is no longer trustworthy; Postgres has released the session
            // (and therefore the lock) or the link is broken. Forget the session.
            sessions.TryRemove(new KeyValuePair<string, SessionEntry>(ownerToken, session));
            try { await session.Connection.DisposeAsync().ConfigureAwait(false); } catch { /* already dead */ }
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var resource = options.KeyPrefix + key;
        if (!sessions.TryGetValue(ownerToken, out var session) ||
            !string.Equals(session.Resource, resource, StringComparison.Ordinal))
        {
            return;
        }

        if (!sessions.TryRemove(new KeyValuePair<string, SessionEntry>(ownerToken, session)))
        {
            return;
        }

        try
        {
            await ReleaseInSession(session.Connection, session.HashedKey, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Connection is dying; Postgres releases session-scoped advisory locks when the
            // session ends, so disposing the connection below still drops the lock.
        }
        finally
        {
            try { await session.Connection.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var kv in sessions.ToArray())
        {
            if (sessions.TryRemove(kv.Key, out var session))
            {
                // Npgsql pools physical sessions by default. Returning the connection to the
                // pool RESETs session state but keeps the underlying backend session alive,
                // which means the advisory lock would remain held until the pool times it
                // out. Explicitly call pg_advisory_unlock before disposing so the lock is
                // released deterministically on Dispose.
                try
                {
                    using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", session.Connection);
                    cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
                    cmd.Parameters.AddWithValue("@key", session.HashedKey);
                    cmd.ExecuteScalar();
                }
                catch { /* best effort; falling through to Dispose closes the session anyway */ }

                try { session.Connection.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    private async Task ReleaseInSession(NpgsqlConnection conn, long hashedKey, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", conn);
        cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
        cmd.Parameters.AddWithValue("@key", hashedKey);
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    internal static long HashKey(string resource)
    {
        var bytes = Encoding.UTF8.GetBytes(resource);
        var hash = SHA256.HashData(bytes);
        // Take first 8 bytes little-endian as int64. Sign does not matter for pg_advisory_lock.
        return BitConverter.ToInt64(hash, 0);
    }

    internal NpgsqlConnection? GetSessionForTesting(string ownerToken)
        => sessions.TryGetValue(ownerToken, out var s) ? s.Connection : null;
}
