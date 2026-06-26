using System.Security.Cryptography;
using System.Text;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;
using Npgsql;
using NpgsqlTypes;

namespace Moongazing.OrionLock.Postgres;

/// <summary>
/// PostgreSQL-backed <see cref="ISharedExclusiveLockProvider"/> (v0.5.0): a distributed reader-writer
/// (shared/exclusive) lock. For a given key, any number of <see cref="LockMode.Shared"/> holders may
/// coexist, OR exactly one <see cref="LockMode.Exclusive"/> holder owns it. Every reader-writer
/// transition runs inside a single transaction that first serializes all transitions for the key with
/// <c>pg_advisory_xact_lock</c>, so mutual exclusion, crash-safe TTLs, renewal, fencing, and writer
/// fairness never depend on a read-then-write round-trip race.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a clock-leased table, not advisory locks.</b> The exclusive-only
/// <see cref="PostgresLockProvider"/> uses session-scoped <c>pg_advisory_lock</c>, whose lifetime IS
/// the connection's. That model cannot track readers individually, carry per-share fencing tokens, or
/// reclaim one dead reader at its own expiry, all of which this contract requires. This provider
/// therefore models holds as rows in a table with an explicit <c>expires_at</c> and reclaims them by
/// time, exactly like the Redis sorted-set model. Consequently
/// <see cref="ISharedExclusiveLockProvider.LeaseDurationIsTtl"/> is <see langword="true"/> here,
/// unlike the exclusive Postgres provider.
/// </para>
/// <para>
/// <b>Schema.</b> One table (default <c>orionlock_rw_holds</c>) holds every share as a row:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>kind = 'r'</c> rows are readers, keyed by <c>(resource, owner_token)</c>. Tracked
///     <i>individually</i> (never a bare counter) so one reader's expiry never frees another's. Many
///     reader rows may exist for one resource.
///   </description></item>
///   <item><description>
///     <c>kind = 'w'</c> is the single writer row for a resource: <c>owner_token</c> is the writer's
///     fencing token. Present only while an exclusive holder owns the key.
///   </description></item>
///   <item><description>
///     <c>kind = 'pw'</c> is the single pending-writer row: <c>owner_token</c> is a waiting writer's
///     fencing token, the intent marker that drives writer fairness.
///   </description></item>
/// </list>
/// <para>
/// Every row carries <c>expires_at timestamptz</c>. A composite primary key on
/// <c>(resource, kind, owner_token)</c> makes the writer and pending-writer rows naturally singular
/// (a fixed <c>owner_token</c> per kind would clash; instead the predicates keep at most one live
/// <c>'w'</c> / <c>'pw'</c> per resource) and makes reader upserts a clean
/// <c>ON CONFLICT ... DO UPDATE</c>.
/// </para>
/// <para>
/// <b>Atomicity / clock.</b> Each transition opens a transaction, calls
/// <c>pg_advisory_xact_lock(hash(resource))</c> to serialize concurrent transitions for that one key
/// (the xact-scoped advisory lock auto-releases at COMMIT/ROLLBACK, so it cannot leak), prunes expired
/// rows with <c>DELETE ... WHERE expires_at &lt;= clock_timestamp()</c>, evaluates the same predicates
/// the Redis Lua scripts use, writes, and commits. All expiry math uses the PostgreSQL server clock,
/// so every process compares lease expiries against one authoritative clock with no
/// client-clock-skew hazard. Because the advisory lock is taken before any read, the
/// prune-evaluate-write sequence is race-free: no other transition for the same key can interleave
/// between the read and the write.
/// </para>
/// <para>
/// <b>Why <c>clock_timestamp()</c>, not <c>now()</c>.</b> Every expiry comparison and every written
/// <c>expires_at</c> uses <c>clock_timestamp()</c> (true wall-clock at the moment of evaluation), NOT
/// <c>now()</c> (which is fixed at transaction start). Each transition first takes
/// <c>pg_advisory_xact_lock</c> and may WAIT on it while another transition for the same key runs; that
/// wait can span more than a lease. With <c>now()</c> the prune predicate would still see the stale
/// transaction-start instant after the wait, so a hold that expired DURING the advisory-lock wait would
/// still look live: a legitimate waiter would be falsely refused, and a late renew could extend an
/// already-dead hold. <c>clock_timestamp()</c> advances during the wait, so a hold that lapsed while the
/// transition was blocked is correctly reclaimed and a renew of an expired hold correctly fails. This
/// keeps the single-authoritative-DB-clock property (still the server's clock, never a client clock); it
/// only moves the reading of that clock from transaction-start to evaluation-time.
/// </para>
/// <para>
/// <b>Fencing.</b> The caller-supplied <c>ownerToken</c> is the fencing token. Renew and release are
/// owner-checked in SQL (a reader by <c>(resource, owner_token)</c> row identity, a writer by
/// <c>owner_token</c> equality on the <c>'w'</c> row), so a stale client can never renew or release
/// another holder's share. Releasing an already-expired share is a safe no-op: the row was already
/// pruned, so the guarded delete matches nothing.
/// </para>
/// <para>
/// <b>Writer fairness.</b> When an exclusive acquire is blocked by live readers it plants (or
/// refreshes) the pending-writer row with its own lease TTL, then fails. While that marker is live a
/// NEW reader is refused so in-flight readers drain and the waiting writer proceeds; an EXISTING
/// reader may still refresh its own hold. Granting the writer deletes the marker. The marker carries
/// the writer's own lease so a writer that crashes or abandons its wait cannot block readers past that
/// TTL. This is writer-preference, not strict FIFO among competing writers, and is the exact analogue
/// of the in-memory and Redis pending-writer reservation.
/// </para>
/// <para>
/// <b>Reentrancy / same-instance behaviour.</b> Mirrors the in-memory and Redis providers: no
/// reentrancy is modelled. A reader re-acquiring with a fresh token adds a second row; with its same
/// token, refreshes that one row. A writer re-acquiring exclusive with its own token refreshes its
/// hold (so the core's reuse-one-token-per-blocking-acquire contract holds). A write-on-read or
/// read-on-write nesting on the same key behaves like any other competing holder and can deadlock if
/// used that way.
/// </para>
/// </remarks>
[BackendName("postgres")]
public sealed class PostgresSharedExclusiveLockProvider : ISharedExclusiveLockProvider, IDisposable
{
    private readonly string connectionString;
    private readonly PostgresSharedExclusiveLockOptions options;
    private readonly string table;

    // Set once the idempotent CREATE TABLE has run (or been skipped). Guards the one-time bootstrap so
    // the DDL is attempted at most once per provider instance even under concurrent first calls.
    private readonly SemaphoreSlim bootstrapGate = new(1, 1);
    private volatile bool bootstrapped;

    /// <summary>
    /// This backend honours <c>leaseDuration</c> as a wall-clock TTL (rows carry an explicit
    /// <c>expires_at</c> reclaimed by the server clock), unlike the session-scoped exclusive Postgres
    /// provider. So the default <see cref="ISharedExclusiveLockProvider.LeaseDurationIsTtl"/> of
    /// <see langword="true"/> is correct and is left as-is.
    /// </summary>

    /// <summary>Creates the provider over the given connection string and options.</summary>
    /// <exception cref="ArgumentException">
    /// The connection string is null/whitespace, or <see cref="PostgresSharedExclusiveLockOptions.TableName"/>
    /// is not a valid simple SQL identifier, or <see cref="PostgresSharedExclusiveLockOptions.KeyPrefix"/> is null.
    /// </exception>
    public PostgresSharedExclusiveLockProvider(string connectionString, PostgresSharedExclusiveLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        if (options.KeyPrefix is null)
        {
            throw new ArgumentException(
                $"{nameof(PostgresSharedExclusiveLockOptions)}.{nameof(PostgresSharedExclusiveLockOptions.KeyPrefix)} cannot be null; use string.Empty for no prefix.",
                nameof(options));
        }

        this.connectionString = connectionString;
        this.options = options;
        table = ValidateIdentifier(options.TableName);
    }

    /// <summary>Disposes the one-time-bootstrap gate. The provider holds no per-lock connections of its own (each transition opens and closes its own), so there is nothing else to release.</summary>
    public void Dispose() => bootstrapGate.Dispose();

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var leaseMs = ToLeaseMilliseconds(leaseDuration);

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resource = options.KeyPrefix + key;
        await SerializeKeyAsync(conn, tx, resource, cancellationToken).ConfigureAwait(false);
        await PruneAsync(conn, tx, resource, cancellationToken).ConfigureAwait(false);

        var acquired = mode == LockMode.Shared
            ? await TryAcquireSharedAsync(conn, tx, resource, ownerToken, leaseMs, cancellationToken).ConfigureAwait(false)
            : await TryAcquireExclusiveAsync(conn, tx, resource, ownerToken, leaseMs, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return acquired;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var leaseMs = ToLeaseMilliseconds(leaseDuration);

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resource = options.KeyPrefix + key;
        await SerializeKeyAsync(conn, tx, resource, cancellationToken).ConfigureAwait(false);
        await PruneAsync(conn, tx, resource, cancellationToken).ConfigureAwait(false);

        // Compare-and-extend: UPDATE only this owner's still-live row of the requested kind. Pruning
        // first means a renew that raced past expiry correctly reports loss (0 rows) instead of
        // resurrecting a dead share. A non-positive lease was already rejected by ToLeaseMilliseconds.
        var kind = mode == LockMode.Shared ? "r" : "w";
        var sql =
            $"UPDATE {table} SET expires_at = clock_timestamp() + make_interval(secs => @lease_s) " +
            "WHERE resource = @resource AND kind = @kind AND owner_token = @owner";
        await using var cmd = NewCommand(conn, tx, sql);
        AddText(cmd, "resource", resource);
        AddText(cmd, "kind", kind);
        AddText(cmd, "owner", ownerToken);
        AddLeaseSeconds(cmd, leaseMs);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resource = options.KeyPrefix + key;
        await SerializeKeyAsync(conn, tx, resource, cancellationToken).ConfigureAwait(false);

        // Owner-checked delete of this caller's own share (no-op if already pruned/absent). A reader
        // deletes its 'r' row by (resource, owner); a writer deletes its 'w' row by (resource, owner).
        // Never touches another holder's row, and never touches the pending-writer marker: the marker
        // was already cleared when the writer was granted the hold (mirroring the in-memory / Redis
        // providers), so clearing it here could erase a DIFFERENT writer's freshly planted intent.
        var kind = mode == LockMode.Shared ? "r" : "w";
        var sql = $"DELETE FROM {table} WHERE resource = @resource AND kind = @kind AND owner_token = @owner";
        await using var cmd = NewCommand(conn, tx, sql);
        AddText(cmd, "resource", resource);
        AddText(cmd, "kind", kind);
        AddText(cmd, "owner", ownerToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Prune expired rows opportunistically so a released key does not retain dead reader rows.
        await PruneAsync(conn, tx, resource, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Acquire predicates (mirror the Redis Lua, evaluated inside the serialized transaction) ----

    private async Task<bool> TryAcquireSharedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string resource, string ownerToken, long leaseMs, CancellationToken ct)
    {
        // Fail if a writer holds the key. Fail if a pending-writer marker is live AND this token is not
        // already a reader (a NEW reader is held off so in-flight readers drain; an EXISTING reader may
        // refresh). Otherwise upsert this reader's row at now + lease.
        if (await ExistsAsync(conn, tx, resource, "w", owner: null, ct).ConfigureAwait(false))
        {
            return false;
        }

        var pendingWriter = await ExistsAsync(conn, tx, resource, "pw", owner: null, ct).ConfigureAwait(false);
        if (pendingWriter)
        {
            var alreadyReader = await ExistsAsync(conn, tx, resource, "r", ownerToken, ct).ConfigureAwait(false);
            if (!alreadyReader)
            {
                return false;
            }
        }

        var sql =
            $"INSERT INTO {table} (resource, kind, owner_token, expires_at) " +
            "VALUES (@resource, 'r', @owner, clock_timestamp() + make_interval(secs => @lease_s)) " +
            "ON CONFLICT (resource, kind, owner_token) DO UPDATE SET expires_at = EXCLUDED.expires_at";
        await using var cmd = NewCommand(conn, tx, sql);
        AddText(cmd, "resource", resource);
        AddText(cmd, "owner", ownerToken);
        AddLeaseSeconds(cmd, leaseMs);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryAcquireExclusiveAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string resource, string ownerToken, long leaseMs, CancellationToken ct)
    {
        // If a DIFFERENT writer holds :w, fail outright. (Same writer re-acquiring is a refresh.)
        var writer = await GetWriterTokenAsync(conn, tx, resource, ct).ConfigureAwait(false);
        if (writer is not null && writer != ownerToken)
        {
            return false;
        }

        // If any reader remains, this writer cannot take the hold yet: plant / refresh the
        // pending-writer marker (first writer wins; a second concurrent writer just keeps retrying
        // without owning the marker) with its own lease TTL, then fail.
        if (await ExistsAsync(conn, tx, resource, "r", owner: null, ct).ConfigureAwait(false))
        {
            var existingPw = await GetPendingWriterTokenAsync(conn, tx, resource, ct).ConfigureAwait(false);
            if (existingPw is null || existingPw == ownerToken)
            {
                var pwSql =
                    $"INSERT INTO {table} (resource, kind, owner_token, expires_at) " +
                    "VALUES (@resource, 'pw', @owner, clock_timestamp() + make_interval(secs => @lease_s)) " +
                    "ON CONFLICT (resource, kind, owner_token) DO UPDATE SET expires_at = EXCLUDED.expires_at";
                await using var pwCmd = NewCommand(conn, tx, pwSql);
                AddText(pwCmd, "resource", resource);
                AddText(pwCmd, "owner", ownerToken);
                AddLeaseSeconds(pwCmd, leaseMs);
                await pwCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return false;
        }

        // Clear path: take (or refresh) the writer row and clear ANY pending-writer marker, so no
        // marker (not even another writer's) survives to deny new readers after this writer releases.
        var setSql =
            $"INSERT INTO {table} (resource, kind, owner_token, expires_at) " +
            "VALUES (@resource, 'w', @owner, clock_timestamp() + make_interval(secs => @lease_s)) " +
            "ON CONFLICT (resource, kind, owner_token) DO UPDATE SET expires_at = EXCLUDED.expires_at";
        await using (var setCmd = NewCommand(conn, tx, setSql))
        {
            AddText(setCmd, "resource", resource);
            AddText(setCmd, "owner", ownerToken);
            AddLeaseSeconds(setCmd, leaseMs);
            await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var delPwSql = $"DELETE FROM {table} WHERE resource = @resource AND kind = 'pw'";
        await using (var delCmd = NewCommand(conn, tx, delPwSql))
        {
            AddText(delCmd, "resource", resource);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    // ---- Small SQL helpers ----

    // Prune every expired row for this resource. Run inside the serialized transaction so a renew/
    // acquire never sees a dead share. clock_timestamp() is the server clock read at THIS instant (not
    // transaction-start now()), so a hold that lapsed while this transition waited on the advisory lock
    // is correctly reclaimed here. Still one authoritative server clock; only the read instant moves.
    private async Task PruneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string resource, CancellationToken ct)
    {
        var sql = $"DELETE FROM {table} WHERE resource = @resource AND expires_at <= clock_timestamp()";
        await using var cmd = NewCommand(conn, tx, sql);
        AddText(cmd, "resource", resource);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Serialize all transitions for one resource: an xact-scoped advisory lock auto-released at
    // COMMIT/ROLLBACK. This is what makes prune -> evaluate -> write race-free per key.
    private static async Task SerializeKeyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string resource, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Bigint) { Value = HashKey(resource) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> ExistsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string resource, string kind, string? owner, CancellationToken ct)
    {
        var sql = owner is null
            ? $"SELECT 1 FROM {table} WHERE resource = @resource AND kind = @kind LIMIT 1"
            : $"SELECT 1 FROM {table} WHERE resource = @resource AND kind = @kind AND owner_token = @owner LIMIT 1";
        await using var cmd = NewCommand(conn, tx, sql);
        AddText(cmd, "resource", resource);
        AddText(cmd, "kind", kind);
        if (owner is not null)
        {
            AddText(cmd, "owner", owner);
        }

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    private Task<string?> GetWriterTokenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string resource, CancellationToken ct)
        => GetSingleTokenAsync(conn, tx, resource, "w", ct);

    private Task<string?> GetPendingWriterTokenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string resource, CancellationToken ct)
        => GetSingleTokenAsync(conn, tx, resource, "pw", ct);

    private async Task<string?> GetSingleTokenAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string resource, string kind, CancellationToken ct)
    {
        var sql = $"SELECT owner_token FROM {table} WHERE resource = @resource AND kind = @kind LIMIT 1";
        await using var cmd = NewCommand(conn, tx, sql);
        AddText(cmd, "resource", resource);
        AddText(cmd, "kind", kind);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string s ? s : null;
    }

    // ---- Connection / bootstrap ----

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await EnsureTableAsync(conn, ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (bootstrapped || !options.AutoCreateTable)
        {
            return;
        }

        await bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (bootstrapped)
            {
                return;
            }

            // Idempotent. The composite PK keeps reader upserts clean and makes the writer / pending
            // rows singular per resource via the acquire predicates. The expires_at index keeps the
            // per-resource prune cheap.
            var ddl =
                $"CREATE TABLE IF NOT EXISTS {table} (" +
                "resource text NOT NULL, " +
                "kind text NOT NULL, " +
                "owner_token text NOT NULL, " +
                "expires_at timestamptz NOT NULL, " +
                $"CONSTRAINT {table}_pkey PRIMARY KEY (resource, kind, owner_token)); " +
                $"CREATE INDEX IF NOT EXISTS {table}_expiry_idx ON {table} (resource, expires_at);";
            await using var cmd = new NpgsqlCommand(ddl, conn) { CommandTimeout = CommandTimeoutSeconds() };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            bootstrapped = true;
        }
        finally
        {
            bootstrapGate.Release();
        }
    }

    private NpgsqlCommand NewCommand(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
        => new(sql, conn, tx) { CommandTimeout = CommandTimeoutSeconds() };

    // Npgsql's CommandTimeout is whole seconds, where 0 means "no timeout" (infinite). A naive
    // (int)TotalSeconds truncates a positive sub-second timeout (e.g. 500ms -> 0), silently turning a
    // bounded timeout into an UNbounded one. So round a positive timeout up to at least 1 second (the
    // smallest finite value this seconds-granularity API can express) and never let it collapse to 0.
    // A non-positive configured timeout is treated as a deliberate "no timeout" (0 -> infinite), which
    // matches Npgsql's own native semantics rather than inventing a different meaning.
    private int CommandTimeoutSeconds()
    {
        var seconds = options.CommandTimeout.TotalSeconds;
        if (seconds <= 0)
        {
            return 0;
        }

        return (int)Math.Min(int.MaxValue, Math.Ceiling(seconds));
    }

    private static void AddText(NpgsqlCommand cmd, string name, string value)
        => cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });

    // The lease is passed as seconds (double) into make_interval(secs => ...). Milliseconds map
    // exactly onto a double fraction of seconds, so a sub-second lease is preserved precisely.
    private static void AddLeaseSeconds(NpgsqlCommand cmd, long leaseMs)
        => cmd.Parameters.Add(new NpgsqlParameter("lease_s", NpgsqlDbType.Double) { Value = leaseMs / 1000.0 });

    /// <summary>
    /// Converts a lease <see cref="TimeSpan"/> to whole milliseconds, rejecting non-positive leases and
    /// never truncating a positive lease to zero. Rounds up so every positive lease maps to at least
    /// 1 ms; a non-positive lease is a caller error rather than a silently immediately-expired hold.
    /// Mirrors the Redis provider's lease normalisation so the two backends reject the same inputs.
    /// </summary>
    private static long ToLeaseMilliseconds(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        return (long)Math.Ceiling(leaseDuration.TotalMilliseconds);
    }

    // Hash the resource to a stable 64-bit advisory-lock key, matching the exclusive provider's scheme.
    // Collisions only over-serialize (two distinct keys would share one xact lock), never corrupt; the
    // table predicates still key on the exact resource string, so correctness never depends on the hash.
    internal static long HashKey(string resource)
    {
        var bytes = Encoding.UTF8.GetBytes(resource);
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToInt64(hash, 0);
    }

    // A table name is interpolated into SQL (PostgreSQL cannot parameterise an identifier), so it must
    // be a strict simple identifier to foreclose injection: a letter or underscore, then letters,
    // digits, or underscores, at most 63 bytes (Postgres' NAMEDATALEN-1 identifier limit).
    private static string ValidateIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 63)
        {
            throw new ArgumentException($"Table name '{name}' exceeds the 63-character PostgreSQL identifier limit.", nameof(name));
        }

        var first = name[0];
        if (!(char.IsAsciiLetter(first) || first == '_'))
        {
            throw new ArgumentException($"Table name '{name}' must start with an ASCII letter or underscore.", nameof(name));
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                throw new ArgumentException(
                    $"Table name '{name}' may contain only ASCII letters, digits, and underscores.", nameof(name));
            }
        }

        return name;
    }
}
