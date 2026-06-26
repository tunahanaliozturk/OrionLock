using Moongazing.OrionLock;
using Moongazing.OrionLock.Postgres;

namespace Moongazing.OrionLock.Postgres.Tests;

/// <summary>
/// Behavioural conformance for <see cref="PostgresSharedExclusiveLockProvider"/> against a REAL
/// PostgreSQL (Testcontainers), covering the v0.5.0 reader-writer spec: mutual exclusion, individual
/// reader TTLs, renewal, fencing, and lease-bounded writer fairness. This is the same correctness
/// matrix the Redis provider runs, mirrored onto the relational backend so both distributed
/// reader-writer providers are verified against one shared set of behavioural facts.
/// </summary>
/// <remarks>
/// Determinism: PostgreSQL honours <c>expires_at</c> against its own server clock (<c>now()</c>) and
/// offers no virtual time, so the expiry tests use short, explicit leases and then poll for the
/// reclaimed state rather than sleeping a single fixed slack period. The few that DO sleep use the
/// established 5x margin (1000 ms wait over a 200 ms lease) so a loaded CI runner cannot race the
/// window. Every test uses a fresh Guid key so cases never alias each other while sharing the one
/// container from <see cref="PostgresContainerFixture"/>.
/// </remarks>
public sealed class PostgresSharedExclusiveLockProviderTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private readonly string connectionString;

    public PostgresSharedExclusiveLockProviderTests(PostgresContainerFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        connectionString = fixture.ConnectionString;
    }

    private PostgresSharedExclusiveLockProvider NewProvider()
        => new(connectionString, new PostgresSharedExclusiveLockOptions());

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns true or the timeout lapses. Awaits a lease-expiry
    /// reclaim deterministically instead of sleeping a guessed slack period.
    /// </summary>
    private static async Task<bool> EventuallyAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await probe();
    }

    // ---- Mutual exclusion -------------------------------------------------------------------

    [Fact]
    public async Task ManyReaders_AcquireConcurrently()
    {
        var p = NewProvider();
        var key = NewKey();

        var tasks = Enumerable.Range(0, 50)
            .Select(i => p.TryAcquireAsync(key, $"reader-{i}", LockMode.Shared, Lease, default))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
    }

    [Fact]
    public async Task Writer_Blocked_WhileReadersHeld_ThenAcquires_AfterTheyRelease()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
        Assert.True(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));

        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync(key, "reader-2", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Readers_Blocked_WhileWriterHeld_ThenAcquire_AfterItReleases()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task SecondWriter_Blocked_WhileWriterHeld()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryAcquireAsync(key, "writer-2", LockMode.Exclusive, Lease, default));
    }

    // ---- Crash safety / TTL -----------------------------------------------------------------

    [Fact]
    public async Task Writer_Reclaimed_AfterItsLeaseExpires()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(200), default));

        var reclaimed = await EventuallyAsync(
            () => p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default),
            TimeSpan.FromSeconds(5));
        Assert.True(reclaimed);
    }

    [Fact]
    public async Task Reader_Reclaimed_AfterItsLeaseExpires()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(200), default));

        var reclaimed = await EventuallyAsync(
            () => p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default),
            TimeSpan.FromSeconds(5));
        Assert.True(reclaimed);
    }

    [Fact]
    public async Task OneReaderExpiry_DoesNotFreeAnotherReader()
    {
        var p = NewProvider();
        var key = NewKey();

        // reader-short expires quickly; reader-long stays well alive.
        Assert.True(await p.TryAcquireAsync(key, "reader-short", LockMode.Shared, TimeSpan.FromMilliseconds(200), default));
        Assert.True(await p.TryAcquireAsync(key, "reader-long", LockMode.Shared, TimeSpan.FromSeconds(30), default));

        // After the short reader lapses, a writer must STILL be blocked: the long reader holds, and
        // tracking readers individually means the short one's expiry did not free the set. The 1000ms
        // wait (5x the 200ms short lease) guarantees the short reader has gone on a loaded runner, while
        // reader-long's 30s lease keeps the negative assertion valid with a wide margin.
        await Task.Delay(1000);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync(key, "reader-long", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    // ---- Renewal ----------------------------------------------------------------------------

    [Fact]
    public async Task Renew_Reader_KeepsHoldAlivePastOriginalLease()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(1000), default));

        // Renew to a long lease well before the original would lapse: at 300ms in there is ~700ms of
        // slack before the 1000ms lease expires, so a loaded CI runner cannot let the renew race the
        // expiry (the renewal needs far more slack than the original lease, per the de-flake pattern).
        await Task.Delay(300);
        Assert.True(await p.TryRenewAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromSeconds(30), default));

        // Past the ORIGINAL 1000 ms lease, the renewal kept the reader alive, so a writer is still blocked.
        await Task.Delay(1100);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Renew_Writer_KeepsHoldAlivePastOriginalLease()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(1000), default));

        await Task.Delay(300);
        Assert.True(await p.TryRenewAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromSeconds(30), default));

        await Task.Delay(1100);
        Assert.False(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task Renew_Reader_OnlyForHolder()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
        Assert.True(await p.TryRenewAsync(key, "reader-1", LockMode.Shared, Lease, default));
        Assert.False(await p.TryRenewAsync(key, "reader-2", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task Renew_Writer_OnlyForHolder()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.True(await p.TryRenewAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryRenewAsync(key, "writer-2", LockMode.Exclusive, Lease, default));
    }

    // ---- Fencing ----------------------------------------------------------------------------

    [Fact]
    public async Task StaleToken_CannotReleaseAnotherReadersShare()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        await p.ReleaseAsync(key, "reader-bogus", LockMode.Shared, default);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task StaleToken_CannotReleaseTheWritersShare()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync(key, "writer-bogus", LockMode.Exclusive, default);
        Assert.False(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task StaleToken_CannotRenewAnotherHoldersShare()
    {
        var p = NewProvider();
        var key = NewKey();

        // Assert the initial acquire succeeded: otherwise the negative renew assertions below would pass
        // vacuously (a stale token cannot renew a hold that was never taken), masking a regression that
        // broke acquire entirely.
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryRenewAsync(key, "writer-bogus", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
        Assert.False(await p.TryRenewAsync(key, "reader-bogus", LockMode.Shared, Lease, default));
    }

    // ---- Release of an expired share is a no-op ---------------------------------------------

    [Fact]
    public async Task ReleaseExpiredReader_IsNoOp()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(200), default));
        await Task.Delay(1000); // lease (200ms) lapses with a wide 5x margin for a loaded runner

        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task ReleaseExpiredWriter_IsNoOp()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(200), default));
        await Task.Delay(1000); // lease (200ms) lapses with a wide 5x margin for a loaded runner

        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
    }

    // ---- Writer fairness (pending-writer marker, no starvation) -----------------------------

    [Fact]
    public async Task PendingWriter_BlocksNewReaders_SoWriterIsNotStarved()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        // Writer fails (reader present) but plants its pending-writer marker.
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // A NEW reader is now refused so the in-flight reader can drain.
        Assert.False(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));

        // The in-flight reader drains; the waiting writer then wins.
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task ContinuousReaderStream_DoesNotStarveWaitingWriter()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-seed", LockMode.Shared, Lease, default));
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // A continuous stream of NEW readers (fresh tokens) keeps arriving. Every one must be refused
        // while the pending-writer marker is live, so the reader stream cannot starve the writer.
        for (var i = 0; i < 25; i++)
        {
            // Re-plant intent each round (mirrors the core's per-poll retry), then prove a new reader is
            // still locked out.
            Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
            Assert.False(await p.TryAcquireAsync(key, $"reader-stream-{i}", LockMode.Shared, Lease, default));
        }

        await p.ReleaseAsync(key, "reader-seed", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task ExistingReader_MayRefreshOwnLease_WhileWriterPending()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // The reader the writer is waiting on may re-acquire / refresh its own share even though a
        // pending-writer marker is live (it is part of the drain set, not a new arrival).
        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task PendingWriterMarker_Expires_SoReadersAreNotBlockedForever()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        // Writer plants intent with a SHORT lease, then abandons its wait (never retries).
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(200), default));

        // New readers are briefly held off...
        Assert.False(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));

        // ...but once the pending-writer marker lapses, new readers proceed again (writer crashed/gave up).
        var unblocked = await EventuallyAsync(
            () => p.TryAcquireAsync(key, "reader-3", LockMode.Shared, Lease, default),
            TimeSpan.FromSeconds(5));
        Assert.True(unblocked);
    }

    [Fact]
    public async Task GrantingExclusive_ClearsPendingMarker_SoLaterReaderSucceeds()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // After the writer releases, no stale pending-writer marker may survive to deny a new reader.
        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));
    }
}
