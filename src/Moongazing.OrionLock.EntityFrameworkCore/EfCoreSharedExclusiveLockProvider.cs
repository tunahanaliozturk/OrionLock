using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// v0.6.0: a PROVIDER-PORTABLE EF Core <see cref="ISharedExclusiveLockProvider"/> (a distributed
/// reader-writer / shared-exclusive lock). For a given key, any number of <see cref="LockMode.Shared"/>
/// holders may coexist, OR exactly one <see cref="LockMode.Exclusive"/> holder owns it. Unlike the
/// PostgreSQL provider (<c>pg_advisory_xact_lock</c> + <c>clock_timestamp()</c>), this provider uses only
/// provider-agnostic EF Core, so the same reader-writer lock works on SQL Server, PostgreSQL, and any
/// other relational EF Core provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Holds model (mirrors the PostgreSQL provider).</b> Holds are clock-leased rows in
/// <c>OrionLock_RwHolds</c> (<see cref="OrionLockRwHoldRow"/>): a <c>kind='r'</c> row per reader keyed by
/// <c>(Resource, OwnerToken)</c> (readers tracked INDIVIDUALLY, never a bare counter, so one reader's
/// expiry never frees another's); one <c>kind='w'</c> writer row whose <c>OwnerToken</c> is the writer's
/// fencing token; and one <c>kind='pw'</c> pending-writer row, the intent marker that drives writer
/// fairness. Every row carries an explicit <c>ExpiresOnUtc</c>. Consequently
/// <see cref="ISharedExclusiveLockProvider.LeaseDurationIsTtl"/> is <see langword="true"/> (rows are
/// reclaimed by time), unlike the session-scoped exclusive EF Core / advisory backends.
/// </para>
/// <para>
/// <b>Per-resource serialization (the portable substitute for an advisory lock).</b> A relational EF
/// Core provider exposes no portable advisory lock or <c>FOR UPDATE</c>. Instead every transition runs in
/// a <see cref="IsolationLevel.Serializable"/> transaction and FIRST writes the resource's anchor row in
/// <c>OrionLock_RwResources</c> (<see cref="OrionLockRwResourceRow"/>) with an unconditional
/// <c>UPDATE ... SET Token = @new</c>. Two concurrent transitions for the SAME resource therefore both
/// try to write that one row under serializable isolation and the database's own concurrency control
/// forces one to fail with a serialization / update conflict; the loser is retried (bounded, with a
/// short linear backoff) and re-reads fresh state. This makes the prune-evaluate-write sequence race-free
/// per resource on any relational provider, the portable analogue of the PostgreSQL
/// <c>pg_advisory_xact_lock</c>. Distinct resources write distinct anchor rows and never serialize
/// against each other.
/// </para>
/// <para>
/// <b>Live clock, read per transition.</b> Every transition reads the database's current time once via
/// <c>SELECT CURRENT_TIMESTAMP</c> (the live DB clock, the portable analogue of the PostgreSQL
/// <c>clock_timestamp()</c> fix) and bases ALL expiry math on that one value: it prunes
/// <c>ExpiresOnUtc &lt;= now</c> and writes <c>ExpiresOnUtc = now + lease</c>. Using the DB clock, not
/// <see cref="DateTime.UtcNow"/>, keeps every process comparing expiries against ONE authoritative clock
/// with no client-clock-skew hazard. Reading it inside the transition (after the serialization point is
/// taken) rather than at some earlier instant means a hold that lapsed while this transition waited to be
/// serialized is correctly reclaimed.
/// </para>
/// <para>
/// <b>Fencing.</b> The caller-supplied <c>ownerToken</c> is the fencing token. Renew and release are
/// owner-checked (a reader by its <c>(Resource, OwnerToken)</c> row, a writer by <c>OwnerToken</c> on the
/// <c>'w'</c> row), so a stale client can never renew or release another holder's share. Releasing an
/// already-expired share is a safe no-op (the row was already pruned).
/// </para>
/// <para>
/// <b>Writer fairness.</b> A blocked writer plants (or refreshes) the pending-writer row with its own
/// lease TTL, then fails. While that marker is live a NEW reader is refused so in-flight readers drain and
/// the waiting writer proceeds; an EXISTING reader may still refresh its own hold. Granting the writer
/// deletes the marker. The marker carries the writer's lease so a crashed / abandoned writer cannot block
/// readers past that TTL. Writer-preference, not strict FIFO among writers; the exact analogue of the
/// in-memory, Redis, and PostgreSQL pending-writer reservation.
/// </para>
/// <para>
/// <b>Reentrancy.</b> Not modelled, matching every other backend: a reader re-acquiring with a fresh token
/// adds a second row, with its same token refreshes that row; a writer re-acquiring with its own token
/// refreshes its hold.
/// </para>
/// </remarks>
[BackendName("efcore")]
public sealed class EfCoreSharedExclusiveLockProvider : ISharedExclusiveLockProvider
{
    private const string ReaderKind = "r";
    private const string WriterKind = "w";
    private const string PendingWriterKind = "pw";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly EfCoreSharedExclusiveLockOptions options;

    /// <summary>Creates the provider. A scoped <see cref="DbContext"/> is resolved per transition.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="scopeFactory"/> or <paramref name="options"/> is null, or <see cref="EfCoreSharedExclusiveLockOptions.KeyPrefix"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="EfCoreSharedExclusiveLockOptions.MaxSerializationRetries"/> is less than 1.</exception>
    public EfCoreSharedExclusiveLockProvider(IServiceScopeFactory scopeFactory, EfCoreSharedExclusiveLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.KeyPrefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSerializationRetries, 1);

        this.scopeFactory = scopeFactory;
        this.options = options;
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var lease = ToLease(leaseDuration);
        var resource = options.KeyPrefix + key;

        return RunSerializedAsync(
            (ctx, now, ct) => mode == LockMode.Shared
                ? TryAcquireSharedAsync(ctx, resource, ownerToken, now, lease, ct)
                : TryAcquireExclusiveAsync(ctx, resource, ownerToken, now, lease, ct),
            resource, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryRenewAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var lease = ToLease(leaseDuration);
        var resource = options.KeyPrefix + key;
        var kind = mode == LockMode.Shared ? ReaderKind : WriterKind;

        return RunSerializedAsync(
            async (ctx, now, ct) =>
            {
                // Expired rows were already pruned by RunSerializedAsync before this runs, so a renew that
                // raced past expiry correctly reports loss (0 rows) instead of resurrecting a dead share.
                // Compare-and-extend only this owner's still-live row of the requested kind.
                var expires = now + lease;
                var rows = await ctx.Set<OrionLockRwHoldRow>()
                    .Where(x => x.Resource == resource && x.Kind == kind && x.OwnerToken == ownerToken)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresOnUtc, expires), ct)
                    .ConfigureAwait(false);
                return rows > 0;
            },
            resource, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var resource = options.KeyPrefix + key;
        var kind = mode == LockMode.Shared ? ReaderKind : WriterKind;

        await RunSerializedAsync(
            async (ctx, _, ct) =>
            {
                // Expired rows were already pruned by RunSerializedAsync. Owner-checked delete of this
                // caller's own share (a no-op if already pruned/absent): never touches another holder's row,
                // and never touches the pending-writer marker (it was already cleared when the writer was
                // granted, mirroring the other backends, so clearing it here could erase a different
                // writer's freshly planted intent).
                await ctx.Set<OrionLockRwHoldRow>()
                    .Where(x => x.Resource == resource && x.Kind == kind && x.OwnerToken == ownerToken)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
                return true;
            },
            resource, cancellationToken).ConfigureAwait(false);
    }

    // ---- Acquire predicates (mirror the PostgreSQL / Redis logic, evaluated inside the serialized tx) ----

    private static async Task<bool> TryAcquireSharedAsync(
        DbContext ctx, string resource, string ownerToken, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        var holds = ctx.Set<OrionLockRwHoldRow>();

        // Fail if a writer holds the key.
        if (await holds.AnyAsync(x => x.Resource == resource && x.Kind == WriterKind, ct).ConfigureAwait(false))
        {
            return false;
        }

        // Fail if a pending-writer marker is live AND this token is not already a reader (a NEW reader is
        // held off so in-flight readers drain; an EXISTING reader may refresh).
        var pendingWriter = await holds
            .AnyAsync(x => x.Resource == resource && x.Kind == PendingWriterKind, ct).ConfigureAwait(false);
        if (pendingWriter)
        {
            var alreadyReader = await holds
                .AnyAsync(x => x.Resource == resource && x.Kind == ReaderKind && x.OwnerToken == ownerToken, ct)
                .ConfigureAwait(false);
            if (!alreadyReader)
            {
                return false;
            }
        }

        await UpsertHoldAsync(ctx, resource, ReaderKind, ownerToken, now + lease, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> TryAcquireExclusiveAsync(
        DbContext ctx, string resource, string ownerToken, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        var holds = ctx.Set<OrionLockRwHoldRow>();

        // If a DIFFERENT writer holds the key, fail. (Same writer re-acquiring is a refresh.)
        var writerToken = await holds
            .Where(x => x.Resource == resource && x.Kind == WriterKind)
            .Select(x => x.OwnerToken)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (writerToken is not null && writerToken != ownerToken)
        {
            return false;
        }

        // If any reader remains, this writer cannot take the hold yet: plant / refresh the pending-writer
        // marker (first writer wins; a second concurrent writer just keeps retrying without owning the
        // marker) with its own lease TTL, then fail.
        var anyReader = await holds
            .AnyAsync(x => x.Resource == resource && x.Kind == ReaderKind, ct).ConfigureAwait(false);
        if (anyReader)
        {
            var existingPw = await holds
                .Where(x => x.Resource == resource && x.Kind == PendingWriterKind)
                .Select(x => x.OwnerToken)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (existingPw is null || existingPw == ownerToken)
            {
                await UpsertHoldAsync(ctx, resource, PendingWriterKind, ownerToken, now + lease, ct).ConfigureAwait(false);
            }
            return false;
        }

        // Clear path: take (or refresh) the writer row and clear ANY pending-writer marker, so no marker
        // (not even another writer's) survives to deny new readers after this writer releases.
        await UpsertHoldAsync(ctx, resource, WriterKind, ownerToken, now + lease, ct).ConfigureAwait(false);
        await holds
            .Where(x => x.Resource == resource && x.Kind == PendingWriterKind)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return true;
    }

    // ---- Small helpers ----

    // Upsert one hold row at the given expiry. EF Core has no portable native upsert, but the serialized
    // transaction guarantees no concurrent transition for this resource, so a read-then-insert-or-update
    // is race-free here.
    private static async Task UpsertHoldAsync(
        DbContext ctx, string resource, string kind, string ownerToken, DateTime expires, CancellationToken ct)
    {
        var rows = await ctx.Set<OrionLockRwHoldRow>()
            .Where(x => x.Resource == resource && x.Kind == kind && x.OwnerToken == ownerToken)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresOnUtc, expires), ct)
            .ConfigureAwait(false);
        if (rows == 0)
        {
            ctx.Add(new OrionLockRwHoldRow
            {
                Resource = resource,
                Kind = kind,
                OwnerToken = ownerToken,
                ExpiresOnUtc = expires,
            });
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            // Detach so a later transition on a reused (scoped) context never re-tracks this instance.
            ctx.ChangeTracker.Clear();
        }
    }

    // Prune every expired row for this resource against the live DB clock read at the start of the tx.
    // Returns the number of rows pruned (callers ignore it; the typed return avoids CA1859).
    private static Task<int> PruneAsync(DbContext ctx, string resource, DateTime now, CancellationToken ct)
        => ctx.Set<OrionLockRwHoldRow>()
            .Where(x => x.Resource == resource && x.ExpiresOnUtc <= now)
            .ExecuteDeleteAsync(ct);

    // ---- Transaction / serialization / clock ----

    /// <summary>
    /// Runs <paramref name="transition"/> inside a serializable transaction that first takes the
    /// per-resource serialization anchor (so concurrent transitions for the same resource serialize) and
    /// reads the live DB clock. Retries on a serialization / update conflict up to the configured bound.
    /// </summary>
    private async Task<bool> RunSerializedAsync(
        Func<DbContext, DateTime, CancellationToken, Task<bool>> transition,
        string resource,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var scope = scopeFactory.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();
            // A fresh scope yields a fresh context, but be defensive: never inherit tracked state.
            ctx.ChangeTracker.Clear();

            try
            {
                await using var tx = await ctx.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);

                // Serialize all transitions for this resource: an unconditional write of the anchor row
                // under serializable isolation makes concurrent same-resource transitions conflict.
                await TakeResourceAnchorAsync(ctx, resource, cancellationToken).ConfigureAwait(false);

                // Read the live DB clock ONCE, after the anchor is taken, and base all expiry math on it.
                var now = await ReadDbNowUtcAsync(ctx, cancellationToken).ConfigureAwait(false);

                // Prune expired rows for this resource against that live clock BEFORE the transition
                // evaluates any predicate, so an expired writer / reader is reclaimed and never seen as a
                // live hold. This is the same prune-then-evaluate-then-write order the PostgreSQL provider
                // runs; doing it here once covers acquire, renew, and release uniformly.
                await PruneAsync(ctx, resource, now, cancellationToken).ConfigureAwait(false);

                var result = await transition(ctx, now, cancellationToken).ConfigureAwait(false);

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (IsTransientConflict(ex) && attempt < options.MaxSerializationRetries)
            {
                // A concurrent transition for the same resource won the serialization race; re-run with
                // fresh state. A short linear backoff keeps two collided processes from colliding again.
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Take the per-resource serialization anchor: insert the row if it is the first ever touch of this
    // resource, then unconditionally rewrite its token so a concurrent serializable transition for the
    // same resource conflicts on this single row.
    private static async Task TakeResourceAnchorAsync(DbContext ctx, string resource, CancellationToken ct)
    {
        var newToken = Guid.NewGuid().ToString("N");
        var rows = await ctx.Set<OrionLockRwResourceRow>()
            .Where(x => x.Resource == resource)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Token, newToken), ct)
            .ConfigureAwait(false);
        if (rows == 0)
        {
            ctx.Add(new OrionLockRwResourceRow { Resource = resource, Token = newToken });
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            ctx.ChangeTracker.Clear();
        }
    }

    // Read the database server's current time as the live clock for this transition. CURRENT_TIMESTAMP is
    // portable across SQL Server, PostgreSQL, and SQLite. The value is returned with
    // DateTimeKind.Unspecified holding UTC clock numbers: the holds column is mapped as a timestamp
    // WITHOUT time zone, so both the stored ExpiresOnUtc and every parameter compared against it must be
    // Unspecified. Otherwise a Kind=Utc parameter binds as PostgreSQL timestamptz and the
    // timestamp-vs-timestamptz comparison is silently shifted by the session time zone, so an expired row
    // is never pruned. SQL Server ignores Kind, so this is correct there too. The lease math only needs one
    // consistent clock shared by every process; the zone label does not matter as long as it is uniform.
    private static async Task<DateTime> ReadDbNowUtcAsync(DbContext ctx, CancellationToken ct)
    {
        var rows = await ctx.Database
            .SqlQueryRaw<DateTime>("SELECT CURRENT_TIMESTAMP AS Value")
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var value = rows[0];
        // The holds column is mapped as a UTC instant (timestamp WITH time zone on PostgreSQL / datetime2
        // on SQL Server), so both the stored ExpiresOnUtc and every parameter compared against it must be
        // DateTimeKind.Utc; a non-Utc value would bind as a different PostgreSQL type and the comparison
        // would be silently time-zone-shifted (an expired row would then never prune). CURRENT_TIMESTAMP is
        // the database server clock; normalise whatever Kind the provider returns to a UTC instant. Every
        // process reads the same server clock, so this is one consistent comparison frame.
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    private async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var baseDelay = options.SerializationRetryBaseDelay;
        if (baseDelay <= TimeSpan.Zero)
        {
            return;
        }

        // Exponential backoff with full jitter, capped at 64x the base, so a burst of colliding callers on
        // one key spreads out across the retry window instead of all retrying in lockstep (which would keep
        // re-colliding and waste the retry budget). A randomised delay in [0, base * 2^min(attempt, 6)).
        var factor = 1L << Math.Min(attempt, 6);
        var maxTicks = baseDelay.Ticks * factor;
        var jittered = Random.Shared.NextInt64(maxTicks + 1);
        await Task.Delay(TimeSpan.FromTicks(jittered), ct).ConfigureAwait(false);
    }

    // A serialization / update-conflict failure is expected under same-resource contention and is retried.
    // EF Core surfaces it as DbUpdateConcurrencyException or as a DbUpdateException / DbException wrapping
    // the provider's serialization-failure error. Detection is by exception type plus the standard
    // SQLSTATE 40001 (serialization_failure) / 40P01 (deadlock_detected) class and the SQL Server 1205
    // deadlock code, kept deliberately broad so any relational provider's serialization failure retries.
    private static bool IsTransientConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbUpdateConcurrencyException)
            {
                return true;
            }

            var sqlState = TryGetSqlState(e);
            if (sqlState is "40001" or "40P01")
            {
                return true;
            }

            var message = e.Message;
            if (message.Contains("serialization", StringComparison.OrdinalIgnoreCase)
                || message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
                || message.Contains("could not serialize", StringComparison.OrdinalIgnoreCase)
                || message.Contains("1205", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Read the provider-neutral SQLSTATE from a DbException without referencing any provider package.
    private static string? TryGetSqlState(Exception e)
    {
        if (e is System.Data.Common.DbException dbEx)
        {
            // DbException.SqlState exists on net8+ and is set by Npgsql; Microsoft.Data.SqlClient leaves it
            // null and is matched by the numeric-code / message checks instead.
            return dbEx.SqlState;
        }

        return null;
    }

    private static TimeSpan ToLease(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        return leaseDuration;
    }
}
