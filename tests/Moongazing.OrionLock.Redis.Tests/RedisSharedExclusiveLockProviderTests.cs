using Moongazing.OrionLock;
using Moongazing.OrionLock.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// One real Redis container (Testcontainers) shared by every test in
/// <see cref="RedisSharedExclusiveLockProviderTests"/>. A single shared container is both faster and
/// far less exposed to transient Docker-daemon blips than creating a fresh container per test; the
/// tests stay isolated from each other because each uses a unique Guid key, not container isolation.
/// </summary>
public sealed class RedisContainerFixture : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder().Build();

    /// <summary>The connection multiplexer to the running Redis, valid for the fixture's lifetime.</summary>
    public IConnectionMultiplexer Mux { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        Mux = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Mux.DisposeAsync();
        await container.DisposeAsync();
    }
}

/// <summary>
/// Behavioural conformance for <see cref="RedisSharedExclusiveLockProvider"/> against a REAL Redis
/// (Testcontainers), covering the v0.4.2 spec: mutual exclusion, individual reader TTLs, renewal,
/// fencing, and lease-bounded writer fairness.
/// </summary>
/// <remarks>
/// Determinism: Redis honours <c>PX</c> TTLs against its own wall clock and offers no virtual time,
/// so the expiry tests use short, explicit leases and then poll for the reclaimed state rather than
/// sleeping a fixed slack period. Every test uses a fresh Guid key so cases never alias each other
/// while sharing the one container from <see cref="RedisContainerFixture"/>.
/// </remarks>
public sealed class RedisSharedExclusiveLockProviderTests : IClassFixture<RedisContainerFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private readonly IConnectionMultiplexer mux;

    public RedisSharedExclusiveLockProviderTests(RedisContainerFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        mux = fixture.Mux;
    }

    private RedisSharedExclusiveLockProvider NewProvider()
        => new(mux, new RedisSharedExclusiveLockOptions());

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns true or the timeout lapses. Used to await a
    /// lease-expiry reclaim deterministically instead of sleeping a guessed slack period.
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

        // A writer cannot get in while either reader is held.
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // Last reader drains; the writer now wins.
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

        // The writer never renews; once its lease lapses a reader reclaims the key.
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

        // The reader never renews; once its lease lapses a writer reclaims the key.
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
        // tracking readers individually means the short one's expiry did not free the set.
        await Task.Delay(400);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // Once the long reader releases too, the writer finally wins.
        await p.ReleaseAsync(key, "reader-long", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    // ---- Renewal ----------------------------------------------------------------------------

    [Fact]
    public async Task Renew_Reader_KeepsHoldAlivePastOriginalLease()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(400), default));

        // Renew to a long lease before the original would lapse.
        await Task.Delay(150);
        Assert.True(await p.TryRenewAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromSeconds(30), default));

        // Past the ORIGINAL 400 ms lease, the renewal has kept the reader alive, so a writer is still blocked.
        await Task.Delay(400);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Renew_Writer_KeepsHoldAlivePastOriginalLease()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(400), default));

        await Task.Delay(150);
        Assert.True(await p.TryRenewAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromSeconds(30), default));

        await Task.Delay(400);
        // Renewal kept the writer alive past its original lease, so a reader is still blocked.
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

        // A stale client releasing with the WRONG token must not remove reader-1's share, so a writer
        // is still blocked.
        await p.ReleaseAsync(key, "reader-bogus", LockMode.Shared, default);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // The real owner releasing does free it.
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task StaleToken_CannotReleaseTheWritersShare()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // Wrong-token release is a no-op; the writer still holds, so a reader stays blocked.
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

        // Assert the initial acquire succeeded: otherwise the negative renew assertions below would
        // pass vacuously (a stale token cannot renew a hold that was never taken in the first place),
        // masking a regression that broke acquire entirely.
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

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(150), default));
        await Task.Delay(300); // lease lapses

        // Releasing the already-expired share must not throw and must leave the key acquirable.
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task ReleaseExpiredWriter_IsNoOp()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(150), default));
        await Task.Delay(300); // lease lapses

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

        // One in-flight reader, then a writer plants intent.
        Assert.True(await p.TryAcquireAsync(key, "reader-seed", LockMode.Shared, Lease, default));
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // A continuous stream of NEW readers (fresh tokens) keeps arriving. Every one must be refused
        // while the pending-writer marker is live, so the reader stream cannot starve the writer.
        for (var i = 0; i < 25; i++)
        {
            // Re-plant intent each round, mirroring the core's per-poll retry, then prove a new reader
            // is still locked out.
            Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
            Assert.False(await p.TryAcquireAsync(key, $"reader-stream-{i}", LockMode.Shared, Lease, default));
        }

        // The seed reader finally drains; the writer (same token) gets in on its next attempt.
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

        // Writer plants intent, reader drains, writer wins.
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        await p.ReleaseAsync(key, "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));

        // After the writer releases, no stale pending-writer marker may survive to deny a new reader.
        await p.ReleaseAsync(key, "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync(key, "reader-2", LockMode.Shared, Lease, default));
    }
}
