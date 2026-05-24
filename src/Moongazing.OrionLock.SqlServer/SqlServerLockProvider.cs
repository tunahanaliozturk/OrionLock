using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.SqlServer;

/// <summary>
/// SQL Server <c>sp_getapplock</c> backed <see cref="IDistributedLockProvider"/>. Holds one
/// dedicated <see cref="SqlConnection"/> per active lock — the lock lifetime IS the SQL session
/// lifetime, so a crashed process releases its locks automatically.
/// </summary>
public sealed class SqlServerLockProvider : IDistributedLockProvider, IDisposable
{
    private const int MaxResourceLength = 240;

    private readonly string connectionString;
    private readonly SqlServerLockOptions options;
    private readonly ConcurrentDictionary<string, SqlConnection> sessions = new();

    /// <summary>Creates the provider.</summary>
    public SqlServerLockProvider(string connectionString, SqlServerLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        this.connectionString = connectionString;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            int returnCode;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "sp_getapplock";
                cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;

                cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = options.KeyPrefix + key });
                cmd.Parameters.Add(new SqlParameter("@LockMode",    SqlDbType.VarChar,  32)  { Value = "Exclusive" });
                cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
                cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int)           { Value = 0 });
                cmd.Parameters.Add(new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 32)  { Value = "public" });

                var rc = new SqlParameter
                {
                    ParameterName = "@RC",
                    SqlDbType = SqlDbType.Int,
                    Direction = ParameterDirection.ReturnValue
                };
                cmd.Parameters.Add(rc);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                returnCode = (int)rc.Value!;
            }

            switch (returnCode)
            {
                case >= 0:
                    if (!sessions.TryAdd(ownerToken, conn))
                    {
                        // ownerToken collision — vanishingly unlikely with GUIDs, but defensive.
                        await ReleaseInSession(conn, options.KeyPrefix + key, cancellationToken).ConfigureAwait(false);
                        await conn.DisposeAsync().ConfigureAwait(false);
                        throw new InvalidOperationException($"ownerToken '{ownerToken}' already registered.");
                    }
                    return true;

                case -1:
                    await conn.DisposeAsync().ConfigureAwait(false);
                    return false;

                case -2:
                    await conn.DisposeAsync().ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);

                default:
                    await conn.DisposeAsync().ConfigureAwait(false);
                    throw new OrionLockBackendException(
                        key, $"sp_getapplock returned {returnCode} (deadlock victim, validation error, or other backend failure).");
            }
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

        if (!sessions.TryGetValue(ownerToken, out var conn))
        {
            return false;
        }

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Connection is no longer trustworthy — SQL Server has released the session
            // (and therefore the lock) or the link is broken. Forget the session.
            sessions.TryRemove(ownerToken, out _);
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already dead */ }
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!sessions.TryRemove(ownerToken, out var conn))
        {
            // Unknown token (never acquired or already released) — no-op, mirrors Redis/EF Core.
            return;
        }

        try
        {
            await ReleaseInSession(conn, options.KeyPrefix + key, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Connection is dying; SQL Server releases session-scoped locks when the session
            // ends, so disposing the connection below still drops the lock.
        }
        finally
        {
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var kv in sessions.ToArray())
        {
            if (sessions.TryRemove(kv.Key, out var conn))
            {
                try { conn.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    // Helper used by the collision branch in TryAcquireAsync and by ReleaseAsync (Task 8).
    private async Task ReleaseInSession(SqlConnection conn, string resource, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp_releaseapplock";
        cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
        cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = resource });
        cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
        cmd.Parameters.Add(new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 32)  { Value = "public" });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var total = options.KeyPrefix.Length + key.Length;
        if (total > MaxResourceLength)
        {
            throw new ArgumentException(
                $"Lock key (with prefix) is {total} characters; SQL Server sp_getapplock @Resource " +
                $"is limited to ~{MaxResourceLength} characters. Hash long keys on the caller side or " +
                "shorten the prefix.", nameof(key));
        }
    }

    // Test-only accessor used by SqlServerLockProviderTests (InternalsVisibleTo).
    internal SqlConnection? GetSessionForTesting(string ownerToken)
        => sessions.TryGetValue(ownerToken, out var c) ? c : null;
}
