using System.Diagnostics;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.SqlServer;
using Moongazing.OrionLock.Tests.Containers;

namespace Moongazing.OrionLock.SqlServer.Tests;

/// <summary>
/// The arithmetic that decides whether a SQL Server round trip is a poll or a wait. No container:
/// a class that takes the container fixture starts one even for tests that never touch it, and
/// these facts hold on a machine with no Docker at all.
/// </summary>
public sealed class SqlServerWaitBudgetTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static SqlServerLockProvider NewProviderWithoutServer(TimeSpan? commandTimeout = null)
        => new("Server=does-not-matter;", new SqlServerLockOptions
        {
            CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30),
        });

    [Fact]
    public void The_remaining_budget_becomes_the_sp_getapplock_lock_timeout()
    {
        Assert.Equal(5000, SqlServerLockProvider.LockTimeoutMsFor(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, SqlServerLockProvider.LockTimeoutMsFor(TimeSpan.Zero));
    }

    [Fact]
    public void An_infinite_budget_becomes_the_sp_getapplock_wait_forever_sentinel()
        => Assert.Equal(-1, SqlServerLockProvider.LockTimeoutMsFor(Timeout.InfiniteTimeSpan));

    [Fact]
    public void A_budget_beyond_int_range_saturates_rather_than_wrapping_into_wait_forever()
    {
        // A wrapped negative would read as -1 to sp_getapplock, which is "block forever" - the one
        // outcome a bounded wait must never turn into.
        var absurd = TimeSpan.FromDays(365);

        Assert.Equal(int.MaxValue, SqlServerLockProvider.LockTimeoutMsFor(absurd));
    }

    [Fact]
    public void The_command_timeout_covers_the_wait_on_top_of_the_configured_allowance()
    {
        // Leaving the command timeout at the configured value would have SqlClient abort a
        // legitimate queued wait as if the network had hung.
        var sut = NewProviderWithoutServer(TimeSpan.FromSeconds(30));

        Assert.Equal(90, sut.CommandTimeoutSecondsFor(TimeSpan.FromSeconds(60), lockTimeoutMs: 60_000));
        // A single-shot try waits for nothing, so it keeps the configured allowance alone.
        Assert.Equal(30, sut.CommandTimeoutSecondsFor(TimeSpan.Zero, lockTimeoutMs: 0));
        // "Wait forever" cannot be covered by any finite command timeout; 0 is SqlClient's infinite.
        Assert.Equal(0, sut.CommandTimeoutSecondsFor(TimeSpan.Zero, lockTimeoutMs: -1));
    }

    [Fact]
    public async Task The_wait_validates_its_key_and_owner_exactly_as_the_single_shot_try_does()
    {
        var sut = NewProviderWithoutServer();

        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            "", "owner-1", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.WaitForAcquireAsync(
            "k", "", Lease, TimeSpan.FromSeconds(1), LockWaitPolicy.Default, default));
    }

}

/// <summary>
/// v2.1: the SQL Server backend waits in SQL Server's own application-lock queue instead of asking
/// it again every retry interval. <c>sp_getapplock</c> has always taken a <c>@LockTimeout</c>;
/// OrionLock passed 0, which threw the queue away and made every waiter a poller.
/// </summary>
/// <remarks>
/// The arithmetic facts below need no server, because they are what decides whether the round trip
/// is a poll or a wait. The behaviour facts need a real SQL Server and run in CI.
/// </remarks>
public sealed class SqlServerBlockingWaitTests : IClassFixture<SqlServerContainerFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private readonly SqlServerContainerFixture fx;

    public SqlServerBlockingWaitTests(SqlServerContainerFixture fx) => this.fx = fx;

    private SqlServerLockProvider NewProvider() => new(fx.ConnectionString, new SqlServerLockOptions());

    [DockerFact]
    public async Task A_waiter_is_handed_the_lock_the_moment_the_holder_releases()
    {
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

        // The waiter parks in SQL Server's queue for up to 30 s. It must come back in roughly the
        // 300 ms the holder takes to release, NOT after a poll interval.
        var sw = Stopwatch.StartNew();
        var waiter = sut.WaitForAcquireAsync(key, "waiter", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        await Task.Delay(300);
        await sut.ReleaseAsync(key, "holder", default);

        Assert.True((await waiter).Acquired);
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 250, 10_000);

        await sut.ReleaseAsync(key, "waiter", default);
    }

    [DockerFact]
    public async Task A_wait_that_outlives_its_budget_returns_false_without_taking_the_lock()
    {
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

        var sw = Stopwatch.StartNew();
        var acquired = await sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromMilliseconds(700), LockWaitPolicy.Default, default);
        sw.Stop();

        Assert.False(acquired.Acquired);
        Assert.InRange(sw.ElapsedMilliseconds, 500, 10_000);

        await sut.ReleaseAsync(key, "holder", default);
    }

    [DockerFact]
    public async Task A_cancelled_waiter_leaves_no_session_parked_in_the_queue()
    {
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

        using var cts = new CancellationTokenSource(400);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));

        // If the cancelled waiter had left its session (and its place in the queue) behind, the
        // lock would be handed to a connection nobody owns and this next waiter would never win.
        await sut.ReleaseAsync(key, "holder", default);
        Assert.True((await sut.WaitForAcquireAsync(
            key, "next", Lease, TimeSpan.FromSeconds(10), LockWaitPolicy.Default, default)).Acquired);

        await sut.ReleaseAsync(key, "next", default);
    }

    [DockerFact]
    public async Task The_whole_wait_is_one_round_trip_through_the_blocking_acquire()
    {
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";
        var counting = new CountingSqlProvider(sut);
        var holder = new DistributedLock(counting);
        var waiter = new DistributedLock(counting);
        var options = new DistributedLockOptions
        {
            LeaseDuration = Lease,
            WaitTimeout = TimeSpan.FromSeconds(30),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            AutoRenew = false,
        };

        var held = await holder.TryAcquireAsync(key, options);
        Assert.NotNull(held);

        var acquire = waiter.AcquireAsync(key, options);
        await Task.Delay(500);
        // 500 ms at a 50 ms retry interval is ten polls under the old behaviour. Under the new one
        // it is still the single wait the first refusal started.
        Assert.Equal(1, counting.WaitCalls);
        Assert.Equal(1, counting.TryCalls);

        await held!.DisposeAsync();
        await using var won = await acquire;
        Assert.True(won.IsHeld);
    }

    /// <summary>Counts what the core asks of the backend, forwarding everything unchanged.</summary>
    private sealed class CountingSqlProvider(IDistributedLockProvider inner) : IDistributedLockProvider
    {
        private int tryCalls;
        private int waitCalls;

        public int TryCalls => Volatile.Read(ref tryCalls);
        public int WaitCalls => Volatile.Read(ref waitCalls);

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref tryCalls);
            return inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken);
        }

        public Task<LockAcquisition> WaitForAcquireAsync(
            string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
            LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref waitCalls);
            return inner.WaitForAcquireAsync(key, ownerToken, leaseDuration, maxWait, waitPolicy, cancellationToken);
        }

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => inner.ReleaseAsync(key, ownerToken, cancellationToken);

        public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;
    }
}
