using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.SqlServer;

/// <summary>
/// SQL Server <c>sp_getapplock</c> backed <see cref="IDistributedLockProvider"/>. Holds one
/// dedicated <see cref="SqlConnection"/> per active lock — the lock lifetime IS the SQL session
/// lifetime, so a crashed process releases its locks automatically.
/// </summary>
[BackendName("sqlserver")]
public sealed class SqlServerLockProvider : IDistributedLockProvider, IDisposable
{
    /// <inheritdoc />
    /// <remarks>
    /// SQL Server <c>sp_getapplock</c> is session-scoped: locks release when the owning
    /// session/transaction ends, regardless of the supplied <c>leaseDuration</c>.
    /// v0.3.21 expired-before-release diagnostics are suppressed for this provider so
    /// a caller that legitimately holds the lock past <c>LeaseDuration</c> is not
    /// flagged.
    /// </remarks>
    public bool LeaseDurationIsTtl => false;

    private const int MaxResourceLength = 240;

    private readonly string connectionString;
    private readonly SqlServerLockOptions options;
    private readonly ConcurrentDictionary<string, SessionEntry> sessions = new();

    private sealed record SessionEntry(string Resource, SqlConnection Connection);

    /// <summary>Creates the provider.</summary>
    public SqlServerLockProvider(string connectionString, SqlServerLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        if (options.KeyPrefix is null)
        {
            throw new ArgumentException(
                $"{nameof(SqlServerLockOptions)}.{nameof(SqlServerLockOptions.KeyPrefix)} cannot be null; use string.Empty for no prefix.",
                nameof(options));
        }
        this.connectionString = connectionString;
        this.options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>No fencing token,</b> for the same reason as the PostgreSQL advisory-lock provider:
    /// <c>sp_getapplock</c> writes nothing that outlives the session, so there is no per-key state to
    /// count acquisitions in. Minting a token would mean a <c>SEQUENCE</c> or a counter table the caller
    /// must create and grant on - a schema requirement this provider exists precisely to avoid. Use the
    /// EF Core backend on SQL Server when you need fencing; its lock row carries a counter bumped by the
    /// same UPDATE that takes it. The same is true of a lock taken by WAITING below: unfenced, because
    /// there is nothing here to mint a token from, not because the wait path forgot to carry one.
    /// </remarks>
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        // @LockTimeout 0: ask once, never queue. This is the single-shot primitive.
        => (await GetAppLockAsync(key, ownerToken, lockTimeoutMs: 0, waitBudget: TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(false)).Acquired;

    /// <summary>
    /// Waits in SQL Server's OWN lock queue rather than asking it again every retry interval.
    /// <c>sp_getapplock</c> takes a <c>@LockTimeout</c>; passing the caller's remaining budget
    /// instead of 0 turns the whole wait into one round trip that returns the instant the lock
    /// frees - and inherits SQL Server's FIFO application-lock manager, so waiters are served in
    /// arrival order instead of racing on a poll tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command timeout is raised to cover the wait: leaving it at
    /// <see cref="SqlServerLockOptions.CommandTimeout"/> would have SqlClient abort a legitimate
    /// queued wait as if the network had hung. It is the caller's budget PLUS the configured
    /// allowance, so the configured value still bounds the network round trip on top of the wait.
    /// </para>
    /// <para>
    /// A cancelled caller does not leave a connection parked in the queue: the command is
    /// cancelled, the exception propagates through the <c>catch</c> below, and the connection - and
    /// with it the session and its place in the queue - is disposed.
    /// </para>
    /// <para>
    /// The budget is kept HERE, on this side's monotonic clock, rather than being handed to
    /// <c>@LockTimeout</c> and believed. <c>sp_getapplock</c>'s timeout is enforced against SQL
    /// Server's own lock-wait accounting, which is documented as a ceiling and is not wall clock: on
    /// a CPU-saturated host it runs far ahead of it, and a 700 ms <c>@LockTimeout</c> has been
    /// measured in CI giving up after 213 ms of real time while
    /// <c>sys.dm_exec_session_wait_stats</c> credited that same wait with 3.5 seconds. The caller
    /// sized the budget, so a round that comes back empty with budget still on the clock is
    /// re-issued with what is left. When the server's timer is honest - the normal case - this is
    /// still exactly one round trip.
    /// </para>
    /// </remarks>
    public Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        // "Wait forever" has no budget to keep, and re-issuing it could only lose the queue place
        // it already holds, so it stays the single blocking round trip it has always been.
        return maxWait == Timeout.InfiniteTimeSpan
            ? GetAppLockAsync(key, ownerToken, LockTimeoutMsFor(maxWait), maxWait, cancellationToken)
            : WaitWithinBudgetAsync(
                maxWait,
                waitPolicy,
                remaining => GetAppLockAsync(
                    key, ownerToken, LockTimeoutMsFor(remaining), remaining, cancellationToken),
                cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="attemptAsync"/> until it wins the lock or <paramref name="maxWait"/> is
    /// genuinely spent on this side's monotonic clock, handing each attempt what is LEFT of the
    /// budget rather than the original figure.
    /// </summary>
    /// <remarks>
    /// Always attempts at least once, so a zero budget is the single-shot try it was before. An
    /// attempt that comes back empty without consuming <see cref="LockWaitPolicy.RetryInterval"/>
    /// sleeps the difference first: the caller's own poll floor, which is what this parameter is
    /// for. Without it a server whose lock timer refused instantly would turn a long budget into a
    /// hot loop against the database instead of the poll the wait is meant to degrade into.
    /// </remarks>
    internal static async Task<LockAcquisition> WaitWithinBudgetAsync(
        TimeSpan maxWait,
        LockWaitPolicy waitPolicy,
        Func<TimeSpan, Task<LockAcquisition>> attemptAsync,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var remaining = maxWait - elapsed.Elapsed;
            var roundStarted = elapsed.Elapsed;

            var acquisition = await attemptAsync(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false);
            if (acquisition.Acquired)
            {
                return acquisition;
            }

            var left = maxWait - elapsed.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return LockAcquisition.NotAcquired;
            }

            var floor = waitPolicy.RetryInterval - (elapsed.Elapsed - roundStarted);
            if (floor > TimeSpan.Zero)
            {
                await Task.Delay(floor < left ? floor : left, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The caller's remaining budget as <c>sp_getapplock @LockTimeout</c> takes it: milliseconds in
    /// an int, with -1 meaning "wait forever". Saturating rather than overflowing, because a
    /// wrapped negative would silently turn a long wait into an infinite one.
    /// </summary>
    internal static int LockTimeoutMsFor(TimeSpan maxWait)
        => maxWait == Timeout.InfiniteTimeSpan
            ? -1
            : (int)Math.Clamp(Math.Ceiling(maxWait.TotalMilliseconds), 0, int.MaxValue);

    private async Task<LockAcquisition> GetAppLockAsync(
        string key, string ownerToken, int lockTimeoutMs, TimeSpan waitBudget, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var resource = options.KeyPrefix + key;
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            int returnCode;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "sp_getapplock";
                cmd.CommandTimeout = CommandTimeoutSecondsFor(waitBudget, lockTimeoutMs);

                cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = resource });
                cmd.Parameters.Add(new SqlParameter("@LockMode",    SqlDbType.VarChar,  32)  { Value = "Exclusive" });
                cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
                cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int)           { Value = lockTimeoutMs });
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
                    if (!sessions.TryAdd(ownerToken, new SessionEntry(resource, conn)))
                    {
                        // ownerToken collision — vanishingly unlikely with GUIDs, but defensive.
                        await ReleaseInSession(conn, resource, cancellationToken).ConfigureAwait(false);
                        await conn.DisposeAsync().ConfigureAwait(false);
                        throw new InvalidOperationException($"ownerToken '{ownerToken}' already registered.");
                    }
                    // Unfenced: see the remarks on TryAcquireAsync - sp_getapplock has nothing
                    // durable to count acquisitions in, and an invented number would be worse.
                    return LockAcquisition.Unfenced;

                case -1:
                    await conn.DisposeAsync().ConfigureAwait(false);
                    return LockAcquisition.NotAcquired;

                case -2:
                    await conn.DisposeAsync().ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);

                default:
                    await conn.DisposeAsync().ConfigureAwait(false);
                    throw new OrionLockBackendException(
                        key, $"sp_getapplock returned {returnCode} (deadlock victim, validation error, or other backend failure).");
            }
        }
        catch (SqlException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller who cancels while the command is blocked inside sp_getapplock does not get a
            // cancellation from SqlClient: cancelling tears the command down and the driver reports
            // the teardown - "A severe error occurred on the current command." - as a SqlException.
            // The contract says a cancelled acquire raises OperationCanceledException, so the
            // translation belongs here, at the only place that knows both the driver's dialect and
            // the caller's token.
            //
            // It matters beyond the exception type: BackendFaultGuard wraps driver exceptions in
            // OrionLockBackendException and deliberately does NOT wrap cancellation, so without this
            // a cancelled caller would come out of DI holding a backend-fault exception for
            // something that was not a fault at all.
            //
            // Disposing the connection is what returns the session - and its place in SQL Server's
            // lock queue - so a cancelled waiter leaves nothing parked.
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already failing */ }
            throw new OperationCanceledException(cancellationToken);
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
            // Token unknown, or token+key combination does not match what was acquired.
            return false;
        }

        try
        {
            using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Connection is no longer trustworthy — SQL Server has released the session
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
            // Unknown token, or token+key combination does not match — no-op, mirrors Redis/EF Core.
            return;
        }

        // Atomic remove: only succeeds if the entry has not been swapped under us.
        if (!sessions.TryRemove(new KeyValuePair<string, SessionEntry>(ownerToken, session)))
        {
            return;
        }

        try
        {
            await ReleaseInSession(session.Connection, resource, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Connection is dying; SQL Server releases session-scoped locks when the session
            // ends, so disposing the connection below still drops the lock.
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
                try { session.Connection.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Command timeout for an <c>sp_getapplock</c> call, in seconds. The configured
    /// <see cref="SqlServerLockOptions.CommandTimeout"/> bounds the NETWORK round trip; a queued
    /// wait is expected to take as long as the caller's budget, so that budget is added on top.
    /// An infinite lock timeout maps to 0, SqlClient's "no command timeout".
    /// </summary>
    internal int CommandTimeoutSecondsFor(TimeSpan waitBudget, int lockTimeoutMs)
    {
        if (lockTimeoutMs < 0)
        {
            return 0;
        }
        var allowance = options.CommandTimeout.TotalSeconds;
        var total = Math.Ceiling(allowance + Math.Max(0d, waitBudget.TotalSeconds));
        return (int)Math.Clamp(total, 1, int.MaxValue);
    }

    // Helper used by the collision branch in GetAppLockAsync and by ReleaseAsync.
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
        => sessions.TryGetValue(ownerToken, out var s) ? s.Connection : null;
}
