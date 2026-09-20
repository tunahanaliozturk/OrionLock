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
    /// <inheritdoc />
    /// <remarks>
    /// PostgreSQL advisory locks are session-scoped: they are released only when the
    /// owning session ends, regardless of the supplied <c>leaseDuration</c>. v0.3.21
    /// expired-before-release diagnostics are suppressed for this provider so a caller
    /// that legitimately holds the lock past <c>LeaseDuration</c> is not flagged.
    /// </remarks>
    public bool LeaseDurationIsTtl => false;

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
    /// <remarks>
    /// <para>
    /// <b>No fencing token.</b> An advisory lock is a row-less, table-less entry in a shared memory
    /// structure: nothing about it persists, so there is nowhere to keep a per-key counter. The whole
    /// point of this provider over the EF Core lock table is that it needs no schema, and minting a
    /// token would need one - a sequence or a counter table the caller has to create and grant on.
    /// </para>
    /// <para>
    /// The schema-free candidates do not survive inspection. <c>pg_current_xact_id()</c> is strictly
    /// increasing, but calling it burns a real transaction id on every acquire, which is wraparound
    /// pressure and autovacuum work nobody signed up for by taking a lock.
    /// <c>pg_snapshot_xmax(pg_current_snapshot())</c> and <c>pg_current_wal_lsn()</c> cost nothing but
    /// are only non-decreasing: two acquisitions with no intervening transaction or WAL write read the
    /// SAME value, and two holders sharing a token is the exact failure a fencing token exists to
    /// prevent.
    /// </para>
    /// <para>
    /// So this provider reports <see langword="null"/>. If you need fencing on PostgreSQL, use the EF
    /// Core backend, whose lock row carries a counter incremented by the same UPDATE that takes it.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Waits in PostgreSQL's own advisory-lock queue. <c>pg_advisory_lock</c> blocks where
    /// <c>pg_try_advisory_lock</c> returns immediately, so the whole wait is one round trip that
    /// returns the instant the lock frees instead of a poll every retry interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait is bounded by <c>statement_timeout</c>, set from the caller's remaining budget in
    /// the same round trip as the lock request. PostgreSQL cancels the blocked statement itself
    /// when the budget lapses and reports SQLSTATE 57014, which this reads as "not acquired" rather
    /// than as a fault. <see cref="PostgresLockOptions.CommandTimeout"/> still bounds the network
    /// round trip, on top of the wait.
    /// </para>
    /// <para>
    /// A cancelled caller does not leave a backend parked in the wait queue: Npgsql sends a cancel
    /// request, the connection is best-effort unlocked and then disposed, so the session - and its
    /// place in the queue - goes with it.
    /// </para>
    /// </remarks>
    public async Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var resource = options.KeyPrefix + key;
        var hashed = HashKey(resource);
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // SET takes no parameters, so the budget is formatted in - an integer this method
            // computed, never caller text.
            var statementTimeoutMs = StatementTimeoutMsFor(maxWait);
            await using (var cmd = new NpgsqlCommand(
                $"SET statement_timeout = {statementTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}; " +
                "SELECT pg_advisory_lock(@key)", conn))
            {
                cmd.CommandTimeout = CommandTimeoutSecondsFor(maxWait);
                cmd.Parameters.AddWithValue("@key", hashed);
                try
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException ex) when (IsBudgetExpiry(ex.SqlState, cancellationToken))
                {
                    // statement_timeout fired: the budget ran out and we do NOT hold the lock.
                    await conn.DisposeAsync().ConfigureAwait(false);
                    return LockAcquisition.NotAcquired;
                }
                catch (PostgresException ex)
                    when (ex.SqlState == PostgresErrorCodes.QueryCanceled && cancellationToken.IsCancellationRequested)
                {
                    // SQLSTATE 57014 is the server saying "this query was cancelled" and says nothing
                    // about WHO cancelled it. Npgsql normally converts a token-triggered cancellation
                    // into OperationCanceledException itself, so this is the path for when it does not
                    // - a cancellation racing the statement_timeout, or an older driver. Reading it as
                    // a budget expiry would hand the caller a timeout for something they asked for,
                    // which is the quieter and worse half of the SQL Server bug next door.
                    try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already failing */ }
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            // Leave the session as we found it, so the renew probe and the release that run on this
            // connection for the rest of the lease are not governed by the wait's budget.
            await using (var reset = new NpgsqlCommand("RESET statement_timeout", conn))
            {
                reset.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
                await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!sessions.TryAdd(ownerToken, new SessionEntry(resource, hashed, conn)))
            {
                await ReleaseInSession(conn, hashed, cancellationToken).ConfigureAwait(false);
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"ownerToken '{ownerToken}' already registered.");
            }
            // Unfenced, for the reason the class remarks give: a session-scoped advisory lock
            // writes nothing durable to count acquisitions in. The wait is not dropping a token
            // that existed - there is none to drop.
            return LockAcquisition.Unfenced;
        }
        catch
        {
            // The grant can land in the same instant the caller gives up, so an abandoned wait
            // unlocks before it drops the connection rather than trusting the pool to do it.
            try
            {
                await ReleaseInSession(conn, hashed, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* the connection is already failing; disposing it ends the session anyway */ }
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already failing */ }
            throw;
        }
    }

    /// <summary>
    /// Whether a cancelled statement is THIS provider's own <c>statement_timeout</c> firing - the
    /// budget running out - rather than the caller cancelling.
    /// </summary>
    /// <remarks>
    /// PostgreSQL reports both as SQLSTATE 57014, so the error code alone cannot tell them apart and
    /// the caller's token has to break the tie. Getting it backwards means a cancelled caller is told
    /// the lock was not free, which is a lie they cannot distinguish from a real timeout.
    /// </remarks>
    internal static bool IsBudgetExpiry(string? sqlState, CancellationToken cancellationToken)
        => sqlState == PostgresErrorCodes.QueryCanceled && !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// The caller's remaining budget as <c>statement_timeout</c> takes it: milliseconds, with 0
    /// meaning "no limit". Saturating rather than overflowing, because a wrapped value would turn a
    /// bounded wait into an unbounded one - or into an immediate cancellation.
    /// </summary>
    internal static int StatementTimeoutMsFor(TimeSpan maxWait)
        => maxWait == Timeout.InfiniteTimeSpan
            ? 0
            : (int)Math.Clamp(Math.Ceiling(maxWait.TotalMilliseconds), 1, int.MaxValue);

    /// <summary>
    /// Command timeout for a blocking advisory-lock request, in seconds. The configured
    /// <see cref="PostgresLockOptions.CommandTimeout"/> bounds the network round trip; the wait is
    /// expected to take the caller's budget, so the budget is added on top. An infinite budget maps
    /// to 0, Npgsql's "no command timeout".
    /// </summary>
    internal int CommandTimeoutSecondsFor(TimeSpan maxWait)
    {
        if (maxWait == Timeout.InfiniteTimeSpan)
        {
            return 0;
        }
        var total = Math.Ceiling(options.CommandTimeout.TotalSeconds + Math.Max(0d, maxWait.TotalSeconds));
        return (int)Math.Clamp(total, 1, int.MaxValue);
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
