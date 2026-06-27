using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

/// <summary>
/// Behavioural conformance for <see cref="EfCoreSharedExclusiveLockProvider"/> against a REAL relational
/// database (Testcontainers), covering the v0.6.0 reader-writer spec: mutual exclusion, individual reader
/// TTLs, renewal, fencing, and lease-bounded writer fairness. This is the SAME correctness matrix the
/// Redis and PostgreSQL providers run, mirrored onto the provider-portable EF Core backend so every
/// distributed reader-writer provider is verified against one shared set of behavioural facts.
/// </summary>
/// <remarks>
/// The suite is abstract and run twice (PostgreSQL and SQL Server) so the portability claim is proven: the
/// exact same facts pass on two different relational EF Core providers through provider-agnostic EF Core.
/// Determinism: the database honours <c>ExpiresOnUtc</c> against its own server clock and offers no virtual
/// time, so expiry tests use short, explicit leases and poll for the reclaimed state rather than sleeping a
/// single guessed slack period. The few that DO sleep use the established wide margin (1000 ms wait over a
/// 200 ms lease) so a loaded CI runner cannot race the window. Every test uses a fresh Guid key so cases
/// never alias while sharing the one container.
/// </remarks>
public abstract class EfCoreSharedExclusiveLockConformance
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopeFactory;

    protected EfCoreSharedExclusiveLockConformance(RwContainerFixtureBase fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        scopeFactory = fixture.ScopeFactory;
    }

    private EfCoreSharedExclusiveLockProvider NewProvider()
        => new(scopeFactory, new EfCoreSharedExclusiveLockOptions());

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

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

        var tasks = Enumerable.Range(0, 25)
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

        Assert.True(await p.TryAcquireAsync(key, "reader-short", LockMode.Shared, TimeSpan.FromMilliseconds(200), default));
        Assert.True(await p.TryAcquireAsync(key, "reader-long", LockMode.Shared, TimeSpan.FromSeconds(30), default));

        // After the short reader lapses, a writer must STILL be blocked: the long reader holds, and tracking
        // readers individually means the short one's expiry did not free the set. 1000ms is 5x the 200ms
        // short lease; reader-long's 30s lease keeps the negative assertion valid with a wide margin.
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

        await Task.Delay(300);
        Assert.True(await p.TryRenewAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromSeconds(30), default));

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
        await Task.Delay(1000);

        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task ReleaseExpiredWriter_IsNoOp()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(200), default));
        await Task.Delay(1000);

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

        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));

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

        for (var i = 0; i < 15; i++)
        {
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

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task PendingWriterMarker_Expires_SoReadersAreNotBlockedForever()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, Lease, default));

        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(200), default));
        Assert.False(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));

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

        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));
    }
}

/// <summary>Runs the EF Core reader-writer conformance matrix against a real PostgreSQL via Testcontainers.</summary>
public sealed class PostgresEfCoreSharedExclusiveLockTests(PostgresRwContainerFixture fixture)
    : EfCoreSharedExclusiveLockConformance(fixture), IClassFixture<PostgresRwContainerFixture>
{
}

/// <summary>Runs the EF Core reader-writer conformance matrix against a real SQL Server via Testcontainers.</summary>
public sealed class SqlServerEfCoreSharedExclusiveLockTests(SqlServerRwContainerFixture fixture)
    : EfCoreSharedExclusiveLockConformance(fixture), IClassFixture<SqlServerRwContainerFixture>
{
}
