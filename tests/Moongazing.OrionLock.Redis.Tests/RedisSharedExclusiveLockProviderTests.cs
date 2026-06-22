using System.Diagnostics;
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
/// <remarks>
/// CI resilience: under the three-TFM, six-container parallel load on a single runner, the first
/// <c>ConnectionMultiplexer.ConnectAsync</c> after the container starts can blip (slow first
/// handshake) and the Docker daemon can transiently fault the start, either of which would fail the
/// whole class. The start and the connect+PING warm-up are therefore retried with exponential backoff
/// under a generous budget; see <see cref="RedisContainerStartup"/>.
/// </remarks>
public sealed class RedisContainerFixture : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder().Build();

    /// <summary>The connection multiplexer to the running Redis, valid for the fixture's lifetime.</summary>
    public IConnectionMultiplexer Mux { get; private set; } = default!;

    public async Task InitializeAsync()
        => Mux = await RedisContainerStartup.StartAndConnectAsync(container).ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        await Mux.DisposeAsync();
        await container.DisposeAsync();
    }
}

/// <summary>
/// Starts a Redis container and opens a verified <see cref="IConnectionMultiplexer"/>, retrying both the
/// container start and the connect+PING warm-up with exponential backoff under an overall budget.
/// </summary>
/// <remarks>
/// This is the single biggest lever against the flaky CI suite: a slow first connection on a loaded
/// runner is retried rather than failing an entire Redis test class. The container's own module
/// readiness probe (an in-container PING) is left in place; this only adds tolerance for transient
/// faults during start and the first out-of-container connection.
/// </remarks>
internal static class RedisContainerStartup
{
    /// <summary>
    /// Starts <paramref name="container"/>, connects a multiplexer, and confirms liveness with a real
    /// <c>PING</c>, retrying the whole sequence on transient failure until it succeeds or the budget is
    /// exhausted, after which the last error is rethrown. Any partially-built multiplexer from a failed
    /// attempt is disposed before the next try so connections are not leaked.
    /// </summary>
    public static async Task<IConnectionMultiplexer> StartAndConnectAsync(RedisContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Redis itself comes up quickly; the budget exists to ride out daemon/handshake blips under load.
        var budget = TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(10);
        var attempt = 0;
        Exception? last = null;

        while (sw.Elapsed < budget)
        {
            attempt++;
            using var cts = new CancellationTokenSource(budget - sw.Elapsed);
            // Concrete type (not the interface) keeps the analyzer happy (CA1859); it upcasts to
            // IConnectionMultiplexer on return at no cost.
            ConnectionMultiplexer? mux = null;
            try
            {
                await container.StartAsync(cts.Token).ConfigureAwait(false);
                mux = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString()).ConfigureAwait(false);
                await mux.GetDatabase().PingAsync().ConfigureAwait(false);
                return mux;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                last = ex;
                if (mux is not null)
                {
                    await mux.DisposeAsync().ConfigureAwait(false);
                }

                var remaining = budget - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var wait = delay < remaining ? delay : remaining;
                await Task.Delay(wait).ConfigureAwait(false);
                delay = delay + delay < maxDelay ? delay + delay : maxDelay;
            }
        }

        throw new InvalidOperationException(
            $"Redis container '{container.Name}' did not become connectable within {budget.TotalSeconds:N0}s after {attempt} attempt(s).",
            last);
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
        // tracking readers individually means the short one's expiry did not free the set. The 1000ms
        // wait (5x the 200ms short lease) guarantees the short reader has gone on a loaded runner, while
        // reader-long's 30s lease keeps the negative assertion valid with a wide margin.
        await Task.Delay(1000);
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

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(1000), default));

        // Renew to a long lease well before the original would lapse: at 300ms in there is ~700ms of
        // slack before the 1000ms lease expires, so a loaded CI runner cannot let the renew race the
        // expiry (the renewal needs far more slack than the original lease, per the de-flake pattern).
        await Task.Delay(300);
        Assert.True(await p.TryRenewAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromSeconds(30), default));

        // Past the ORIGINAL 1000 ms lease, the renewal has kept the reader alive, so a writer is still blocked.
        await Task.Delay(1100);
        Assert.False(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Renew_Writer_KeepsHoldAlivePastOriginalLease()
    {
        var p = NewProvider();
        var key = NewKey();

        Assert.True(await p.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(1000), default));

        // Renew at 300ms in, ~700ms before the 1000ms lease would lapse, so the renew cannot race the
        // expiry on a loaded runner.
        await Task.Delay(300);
        Assert.True(await p.TryRenewAsync(key, "writer-1", LockMode.Exclusive, TimeSpan.FromSeconds(30), default));

        await Task.Delay(1100);
        // Past the ORIGINAL 1000 ms lease, the renewal kept the writer alive, so a reader is still blocked.
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

        Assert.True(await p.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(200), default));
        await Task.Delay(1000); // lease (200ms) lapses with a wide 5x margin for a loaded runner

        // Releasing the already-expired share must not throw and must leave the key acquirable.
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
