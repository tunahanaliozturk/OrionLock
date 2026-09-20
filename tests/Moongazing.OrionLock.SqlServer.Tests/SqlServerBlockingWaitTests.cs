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

    [Fact]
    public async Task A_round_that_gives_up_early_is_re_issued_with_what_is_left_of_the_budget()
    {
        // sp_getapplock's @LockTimeout is enforced against SQL Server's own lock-wait accounting,
        // not wall clock, and on a saturated host that accounting runs ahead of it: CI measured a
        // 700 ms @LockTimeout returning -1 after 213 ms of real time while
        // sys.dm_exec_session_wait_stats credited the same wait with 3.5 seconds. A caller who
        // sized a budget gets the budget, so the provider keeps the clock itself.
        var handed = new List<TimeSpan>();
        var sw = Stopwatch.StartNew();

        var result = await SqlServerLockProvider.WaitWithinBudgetAsync(
            TimeSpan.FromMilliseconds(400),
            new LockWaitPolicy(TimeSpan.FromMilliseconds(20)).ToPollOptions(),
            remaining =>
            {
                handed.Add(remaining);
                return Task.FromResult(LockAcquisition.NotAcquired);
            },
            default);
        sw.Stop();

        Assert.False(result.Acquired);
        Assert.True(handed.Count > 1, $"one round only: the early give-up was reported as the whole wait ({handed.Count} round(s))");
        Assert.InRange(sw.ElapsedMilliseconds, 350, 5_000);
        // Each round is handed what is LEFT, never the original figure again - otherwise the last
        // round could park past the caller's deadline.
        Assert.True(handed[^1] < handed[0], $"the budget handed to each round did not shrink: {handed[0]} then {handed[^1]}");
    }

    [Fact]
    public async Task A_round_that_wins_ends_the_wait_there_and_then()
    {
        var attempts = 0;

        var result = await SqlServerLockProvider.WaitWithinBudgetAsync(
            TimeSpan.FromSeconds(30),
            new LockWaitPolicy(TimeSpan.FromMilliseconds(1)).ToPollOptions(),
            _ => Task.FromResult(++attempts == 2 ? LockAcquisition.Unfenced : LockAcquisition.NotAcquired),
            default);

        Assert.True(result.Acquired);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// A generator that always draws the top of the jitter window, so
    /// <c>ComputeJitteredDelay</c> returns exactly the exponential ceiling for each round and the
    /// whole sequence is a fixed, checkable series rather than something timed and hoped for.
    /// </summary>
    private sealed class TopOfTheJitterWindow : Random
    {
        public override double NextDouble() => 1.0;
    }

    [Fact]
    public async Task The_retry_delay_follows_the_whole_policy_not_just_its_floor()
    {
        // This loop runs exactly when a server is refusing early and repeatedly - which is exactly
        // when every waiter retrying on the same flat tick is a thundering herd. Reading only
        // RetryInterval and ignoring BackoffCeiling gave callers who had configured exponential
        // jitter a synchronised flat retry instead. LockWaitPolicy was ignored outright by this
        // provider before the budget fix; honouring half of it is the state to avoid.
        var backoff = new LockWaitPolicy(
            TimeSpan.FromMilliseconds(20), BackoffCeiling: TimeSpan.FromMilliseconds(400)).ToPollOptions();
        backoff.RandomFactory = () => new TopOfTheJitterWindow();

        var at = new List<long>();
        var clock = Stopwatch.StartNew();

        await SqlServerLockProvider.WaitWithinBudgetAsync(
            TimeSpan.FromMilliseconds(800),
            backoff,
            _ =>
            {
                at.Add(clock.ElapsedMilliseconds);
                return Task.FromResult(LockAcquisition.NotAcquired);
            },
            default);
        clock.Stop();

        // Top of the window every time, so the delays are exactly 20, 40, 80, 160 - doubling to the
        // 400 ms ceiling. A flat RetryInterval makes every one of these 20.
        var expected = new[] { 20, 40, 80, 160 };
        Assert.True(at.Count >= expected.Length + 1, $"only {at.Count} rounds in 800ms, expected at least {expected.Length + 1}");

        for (var i = 0; i < expected.Length; i++)
        {
            var gap = at[i + 1] - at[i];
            Assert.True(
                gap >= expected[i] - 5,
                $"gap {i} was {gap}ms but the policy asks for {expected[i]}ms: the delay is being taken from the retry interval alone, "
                + $"so a configured BackoffCeiling buys nothing. Gaps: [{string.Join(", ", at.Zip(at.Skip(1), (a, b) => b - a))}]");
            Assert.True(
                gap < expected[i] + 200,
                $"gap {i} was {gap}ms, far past the {expected[i]}ms the policy asks for. Gaps: [{string.Join(", ", at.Zip(at.Skip(1), (a, b) => b - a))}]");
        }
    }

    [Fact]
    public async Task No_round_is_issued_once_the_budget_is_gone()
    {
        // The contract this whole fix exists to restore is "returns false WITHOUT taking the lock".
        // A round issued after the deadline can still WIN, and a lock handed to a caller who has
        // already stopped waiting - and who may by then have taken the other branch - is worse than
        // the early give-up: early is a wasted wait, late is a lock nobody is holding on purpose.
        // A retry interval longer than the budget is what exposes it: the delay gets clipped to the
        // remainder, and the loop then came back round for one more attempt with nothing left.
        var budget = TimeSpan.FromMilliseconds(200);
        var dispatchedWithNothingLeft = 0;
        var clock = Stopwatch.StartNew();

        var result = await SqlServerLockProvider.WaitWithinBudgetAsync(
            budget,
            new LockWaitPolicy(TimeSpan.FromMilliseconds(500)).ToPollOptions(),
            remaining =>
            {
                // Judged on the LOOP's own figure - the budget it had when it dispatched this round
                // - and not on a clock this fake reads for itself. Between the deadline check and
                // the call the thread can be descheduled, so a round the loop legitimately started
                // can still EXECUTE after the deadline; a fake that timestamps its own invocation
                // reports that as a violation and goes red for preemption rather than for the bug.
                // What the loop can actually promise is that it never STARTS a round having seen
                // the budget gone, and this parameter is exactly what it saw.
                if (remaining <= TimeSpan.Zero)
                {
                    // A real sp_getapplock round can win here. Say so, so the assertion below is
                    // about the lock and not only about the bookkeeping.
                    Interlocked.Increment(ref dispatchedWithNothingLeft);
                    return Task.FromResult(LockAcquisition.Unfenced);
                }
                return Task.FromResult(LockAcquisition.NotAcquired);
            },
            default);
        clock.Stop();

        Assert.False(result.Acquired);
        Assert.True(
            Volatile.Read(ref dispatchedWithNothingLeft) == 0,
            $"{dispatchedWithNothingLeft} round(s) were dispatched with none of the {budget.TotalMilliseconds}ms budget left, and one of them took the lock");
        // The floor is the real assertion: the wait lasted its budget. The ceiling is only a
        // runaway guard - a correct loop still overshoots by one round plus whatever the scheduler
        // adds, and neither of those is boundable on a shared runner, so a tight ceiling here would
        // go red for slowness rather than for overshoot.
        Assert.InRange(clock.ElapsedMilliseconds, 190, 5_000);
    }

    [Fact]
    public async Task The_last_sliver_of_a_budget_is_not_spun_on()
    {
        // Task.Delay truncates its delay to whole milliseconds, so a sub-millisecond remainder
        // sleeps for NOTHING and the loop comes straight back round with the budget still
        // technically positive. Measured on this loop before the guard: 828 rounds inside half a
        // millisecond, every one of them a fresh connection and an sp_getapplock against the
        // server. It is the same defect the Redis waiter had, and the same shape of answer: a
        // sliver too small to sleep on is the budget ending.
        //
        // The sliver is CONSTRUCTED rather than waited for: the first round consumes the budget
        // down to half a millisecond, which is an ordinary thing for a real sp_getapplock round to
        // do. Left purely to chance the sliver turns up in roughly one run in ten, and a regression
        // test that mostly does not run is not one. The construction still misses when the spin to
        // the mark overshoots the deadline, so the scenario is repeated - a miss costs an iteration,
        // not the test.
        //
        // Asserted as a RATE, not as a count. A count would be a proxy the machine can violate: a
        // Task.Delay that comes back a millisecond or two early leaves real budget behind, and the
        // round the loop then dispatches is correct, not a spin. What is never correct is issuing
        // rounds faster than the interval the caller configured. Elapsed time is the denominator,
        // so a slow machine stretches the allowance instead of failing - the same shape as the
        // Redis spin assertion, and for the same reason.
        var budget = TimeSpan.FromMilliseconds(200);
        var interval = TimeSpan.FromMilliseconds(500);
        var sliverStartsAt = budget - TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var rounds = 0;
            var clock = Stopwatch.StartNew();

            var result = await SqlServerLockProvider.WaitWithinBudgetAsync(
                budget,
                new LockWaitPolicy(interval).ToPollOptions(),
                async _ =>
                {
                    if (Interlocked.Increment(ref rounds) == 1)
                    {
                        await Task.Delay(sliverStartsAt - TimeSpan.FromMilliseconds(40));
                        // The last stretch is spun rather than slept, because Task.Delay cannot land
                        // on a sub-millisecond mark and landing on it is the whole point. The margin covers the
                        // ~15ms granularity of the delay itself, which would otherwise sail past the mark.
                        while (clock.Elapsed < sliverStartsAt)
                        {
                            Thread.SpinWait(10);
                        }
                    }
                    return LockAcquisition.NotAcquired;
                },
                default);
            clock.Stop();

            // The first round is owed nothing, every later one owes a full interval.
            var allowed = 1 + (int)Math.Ceiling(clock.Elapsed / interval);
            var observed = Volatile.Read(ref rounds);

            Assert.False(result.Acquired);
            Assert.True(
                observed <= allowed,
                $"{observed} rounds in {clock.ElapsedMilliseconds}ms at a {interval.TotalMilliseconds}ms retry interval: "
                + $"at most {allowed} can be spaced by that interval, so the rest were a spin on the server - "
                + "the remainder after the last sleep was too small to sleep on again.");
        }
    }

    [Fact]
    public async Task A_zero_budget_is_still_the_single_shot_try_it_always_was()
    {
        // The core never calls the wait with nothing left, but "wait up to zero" means "ask once",
        // not "ask nothing" - and asking nothing would silently drop an acquire that was free.
        var attempts = 0;

        var result = await SqlServerLockProvider.WaitWithinBudgetAsync(
            TimeSpan.Zero,
            LockWaitPolicy.Default.ToPollOptions(),
            _ => { attempts++; return Task.FromResult(LockAcquisition.NotAcquired); },
            default);

        Assert.False(result.Acquired);
        Assert.Equal(1, attempts);
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

    [DockerFact]
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
