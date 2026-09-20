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
    public void The_provider_really_implements_the_wait_member_rather_than_inheriting_the_poll()
    {
        // WaitForAcquireAsync is a DEFAULT interface method, so a provider whose signature drifts
        // out of step with the contract still compiles - it just stops overriding anything and
        // silently falls back to polling. That is not hypothetical: it happened to this provider
        // during the rebase onto fencing tokens, when the member's return type changed from bool to
        // LockAcquisition and nothing failed to build. The interface map is the only thing that
        // tells the difference between an override and the default.
        var map = typeof(SqlServerLockProvider).GetInterfaceMap(typeof(Moongazing.OrionLock.Providers.IDistributedLockProvider));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == "WaitForAcquireAsync");

        Assert.True(index >= 0, "the contract no longer declares WaitForAcquireAsync");
        Assert.Equal(typeof(SqlServerLockProvider), map.TargetMethods[index].DeclaringType);
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
    public async Task ZZZ_DIAGNOSTIC_applock_timeout_fidelity()
    {
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        await using (var info = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await info.OpenAsync();
            using var c = info.CreateCommand();
            c.CommandText = "SELECT @@VERSION, DB_NAME(), @@LOCK_TIMEOUT, @@SPID, SERVERPROPERTY('EngineEdition'), cpu_count, scheduler_count FROM sys.dm_os_sys_info";
            using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                sb.Append(inv, $"VERSION={r.GetString(0).Replace('\n', ' ').Replace('\r', ' ')}\n");
                sb.Append(inv, $"DB={r.GetString(1)} LOCK_TIMEOUT={r.GetInt32(2)} SPID={r.GetInt16(3)} EngineEdition={r.GetValue(4)} cpu_count={r.GetValue(5)} schedulers={r.GetValue(6)}\n");
            }
        }

        const int Budget = 700;
        // Both real failures happened on a saturated runner. Saturate it deliberately.
        using var hog = new CancellationTokenSource();
        var hogs = Enumerable.Range(0, Environment.ProcessorCount * 3)
            .Select(_ => Task.Factory.StartNew(
                () => { var s = new SpinWait(); while (!hog.IsCancellationRequested) { s.SpinOnce(); } },
                TaskCreationOptions.LongRunning))
            .ToArray();
        sb.Append(inv, $"HOG threads={hogs.Length} cores={Environment.ProcessorCount}\n");

        var since = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
        {
            var key = $"diag-{Guid.NewGuid():N}";
            using var sut = NewProvider();
            Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));
            var t0 = since.ElapsedMilliseconds;

            if (i % 2 == 0)
            {
                // Raw batch: the server's own account of the wait, byte-identical parameters.
                await using var w = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
                await w.OpenAsync();

                var wallStart = DateTime.UtcNow;
                var monoSw = Stopwatch.StartNew();
                using var wc = w.CreateCommand();
                wc.CommandTimeout = 60;
                wc.CommandText = """
                    SET NOCOUNT ON;
                    DECLARE @rc int;
                    DECLARE @t0 datetime2(7) = SYSUTCDATETIME();
                    DECLARE @w0 bigint = ISNULL((SELECT SUM(wait_time_ms) FROM sys.dm_exec_session_wait_stats
                                                 WHERE session_id = @@SPID AND wait_type LIKE 'LCK%'), 0);
                    EXEC @rc = sp_getapplock @Resource = @res, @LockMode = 'Exclusive',
                         @LockOwner = 'Session', @LockTimeout = @lt, @DbPrincipal = 'public';
                    SELECT @rc,
                           DATEDIFF(millisecond, @t0, SYSUTCDATETIME()),
                           ISNULL((SELECT SUM(wait_time_ms) FROM sys.dm_exec_session_wait_stats
                                   WHERE session_id = @@SPID AND wait_type LIKE 'LCK%'), 0) - @w0,
                           @@SPID;
                    """;
                wc.Parameters.AddWithValue("@res", key);
                wc.Parameters.AddWithValue("@lt", Budget);
                using var wr = await wc.ExecuteReaderAsync();
                await wr.ReadAsync();
                monoSw.Stop();
                var wallMs = (long)(DateTime.UtcNow - wallStart).TotalMilliseconds;
                sb.Append(inv, $"i={i} t0={t0} RAW rc={wr.GetInt32(0)} serverMs={wr.GetInt32(1)} lckWaitMs={wr.GetInt64(2)} spid={wr.GetInt16(3)} monoMs={monoSw.ElapsedMilliseconds} wallMs={wallMs}\n");
            }
            else
            {
                var wallStart = DateTime.UtcNow;
                var monoSw = Stopwatch.StartNew();
                var got = await sut.WaitForAcquireAsync(key, "waiter", Lease, TimeSpan.FromMilliseconds(Budget), LockWaitPolicy.Default, default);
                monoSw.Stop();
                var wallMs = (long)(DateTime.UtcNow - wallStart).TotalMilliseconds;
                sb.Append(inv, $"i={i} t0={t0} PROV acquired={got.Acquired} monoMs={monoSw.ElapsedMilliseconds} wallMs={wallMs}\n");
            }

            await sut.ReleaseAsync(key, "holder", default);
        }

        await hog.CancelAsync();
        await Task.WhenAll(hogs);

        Assert.Fail(sb.ToString());
    }

    [DockerFact(Skip = "diag round 2")]
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

    [DockerFact(Skip = "diag round 2")]
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

    [DockerFact(Skip = "diag round 2")]
    public async Task A_cancelled_waiter_is_told_it_was_cancelled_not_handed_a_driver_error()
    {
        // Cancelling a command blocked inside sp_getapplock tears the command down, and SqlClient
        // reports the teardown - "A severe error occurred on the current command." - as a
        // SqlException. The contract says a cancelled acquire raises OperationCanceledException, and
        // through DI it is worse than a wrong type: BackendFaultGuard wraps driver exceptions in
        // OrionLockBackendException and does not wrap cancellation, so the caller would be told the
        // backend failed for something they asked for.
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True((await sut.TryAcquireAsync(key, "holder", Lease, default)));

        using var cts = new CancellationTokenSource(400);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotType<Microsoft.Data.SqlClient.SqlException>(thrown);

        await sut.ReleaseAsync(key, "holder", default);
    }

    [DockerFact(Skip = "diag round 2")]
    public async Task A_cancelled_blocking_acquire_through_the_full_stack_still_reports_cancellation()
    {
        // The same fact where a consumer meets it: through DistributedLock, which wraps the provider
        // in BackendFaultGuard. A driver exception escaping the provider would arrive here dressed
        // as OrionLockBackendException.
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";
        var holder = new DistributedLock(sut);
        var waiter = new DistributedLock(sut);
        var options = new DistributedLockOptions
        {
            LeaseDuration = Lease,
            WaitTimeout = TimeSpan.FromSeconds(30),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            AutoRenew = false,
        };

        await using var held = await holder.TryAcquireAsync(key, options);
        Assert.NotNull(held);

        using var cts = new CancellationTokenSource(400);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.AcquireAsync(key, options, cts.Token));
    }

    [DockerFact(Skip = "diag round 2")]
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

    [DockerFact(Skip = "diag round 2")]
    public async Task The_whole_wait_is_one_command_through_the_blocking_acquire()
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

        // Count the WAITER only. The gate acquire above goes through the same decorator, and
        // counting it made this assert read 2 and look like the provider issuing a second command -
        // which is exactly the misreading the number exists to prevent. The scaffolding does not get
        // to be part of the measurement.
        counting.Reset();

        var acquire = waiter.AcquireAsync(key, options);
        await Task.Delay(500);
        // 500 ms at a 50 ms retry interval is ten polls under the old behaviour. Under the new one
        // it is one refused attempt and the single wait that refusal started, still open.
        //
        // Both counts are reported together on failure. A bare Assert.Equal here says only
        // "expected 1, actual 2" and leaves the reader unable to tell a second sp_getapplock command
        // from a second attempt - which is exactly the ambiguity that made the first CI failure of
        // this test look like a provider bug when it was the harness counting its own gate acquire.
        Assert.True(
            counting.WaitCalls == 1 && counting.TryCalls == 1,
            $"the waiter should cost one refused attempt and one open wait, but made "
            + $"{counting.TryCalls} TryAcquireAsync call(s) and {counting.WaitCalls} "
            + $"WaitForAcquireAsync call(s). More than one wait means the blocking sp_getapplock "
            + "returned early and the core asked again - if that is real, the documented "
            + "one-command-per-wait figure is wrong and both it and this test have to change.");

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

        /// <summary>Zeroes the ledger so a test can exclude its own scaffolding from the count.</summary>
        public void Reset()
        {
            Volatile.Write(ref tryCalls, 0);
            Volatile.Write(ref waitCalls, 0);
        }

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
