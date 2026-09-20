using System.Diagnostics;
using Moongazing.OrionLock.Postgres;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Tests.Containers;

namespace Moongazing.OrionLock.Postgres.Tests;

/// <summary>
/// The arithmetic that turns the caller's remaining budget into a bounded <c>pg_advisory_lock</c>.
/// No container: a class that takes the container fixture starts one even for tests that never
/// touch it, and these facts hold on a machine with no Docker at all.
/// </summary>
public sealed class PostgresWaitBudgetTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static PostgresLockProvider NewProviderWithoutServer(TimeSpan? commandTimeout = null)
        => new("Host=does-not-matter;", new PostgresLockOptions
        {
            CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30),
        });

    [Fact]
    public void The_remaining_budget_becomes_the_statement_timeout()
        => Assert.Equal(5000, PostgresLockProvider.StatementTimeoutMsFor(TimeSpan.FromSeconds(5)));

    [Fact]
    public void An_infinite_budget_becomes_the_no_limit_statement_timeout()
        => Assert.Equal(0, PostgresLockProvider.StatementTimeoutMsFor(Timeout.InfiniteTimeSpan));

    [Fact]
    public void A_zero_budget_never_collapses_into_the_no_limit_sentinel()
    {
        // statement_timeout = 0 means "no limit" in PostgreSQL, so rounding a sub-millisecond
        // budget down to 0 would turn the shortest possible wait into an unbounded one.
        Assert.Equal(1, PostgresLockProvider.StatementTimeoutMsFor(TimeSpan.Zero));
        Assert.Equal(1, PostgresLockProvider.StatementTimeoutMsFor(TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void A_budget_beyond_int_range_saturates_rather_than_wrapping()
        => Assert.Equal(int.MaxValue, PostgresLockProvider.StatementTimeoutMsFor(TimeSpan.FromDays(365)));

    [Fact]
    public void The_command_timeout_covers_the_wait_on_top_of_the_configured_allowance()
    {
        var sut = NewProviderWithoutServer(TimeSpan.FromSeconds(30));

        Assert.Equal(90, sut.CommandTimeoutSecondsFor(TimeSpan.FromSeconds(60)));
        Assert.Equal(0, sut.CommandTimeoutSecondsFor(Timeout.InfiniteTimeSpan));
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
        var map = typeof(PostgresLockProvider).GetInterfaceMap(typeof(Moongazing.OrionLock.Providers.IDistributedLockProvider));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == "WaitForAcquireAsync");

        Assert.True(index >= 0, "the contract no longer declares WaitForAcquireAsync");
        Assert.Equal(typeof(PostgresLockProvider), map.TargetMethods[index].DeclaringType);
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
/// v2.1: the PostgreSQL backend blocks on <c>pg_advisory_lock</c> instead of asking
/// <c>pg_try_advisory_lock</c> again every retry interval.
/// </summary>
public sealed class PostgresBlockingWaitTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private readonly PostgresContainerFixture fx;

    public PostgresBlockingWaitTests(PostgresContainerFixture fx) => this.fx = fx;

    private PostgresLockProvider NewProvider() => new(fx.ConnectionString, new PostgresLockOptions());

    [DockerFact]
    public async Task A_waiter_is_handed_the_lock_the_moment_the_holder_releases()
    {
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

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

        // statement_timeout cancels the blocked statement; that is a budget expiry, not a fault.
        Assert.False(acquired.Acquired);
        Assert.InRange(sw.ElapsedMilliseconds, 500, 10_000);

        await sut.ReleaseAsync(key, "holder", default);
    }

    [DockerFact]
    public async Task A_granted_wait_leaves_the_session_without_the_waits_statement_timeout()
    {
        // The connection outlives the wait - the renew probe and the release run on it - so a
        // leftover statement_timeout from a short wait would start cancelling them.
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True((await sut.WaitForAcquireAsync(
            key, "owner", Lease, TimeSpan.FromMilliseconds(400), LockWaitPolicy.Default, default)).Acquired);

        var conn = sut.GetSessionForTesting("owner");
        Assert.NotNull(conn);
        await using var cmd = new Npgsql.NpgsqlCommand("SHOW statement_timeout", conn);
        var effective = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal("0", effective);

        await sut.ReleaseAsync(key, "owner", default);
    }

    [DockerFact]
    public async Task A_cancelled_waiter_leaves_no_backend_parked_in_the_queue()
    {
        using var sut = NewProvider();
        var key = $"blocking-wait-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

        using var cts = new CancellationTokenSource(400);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));

        await sut.ReleaseAsync(key, "holder", default);
        Assert.True((await sut.WaitForAcquireAsync(
            key, "next", Lease, TimeSpan.FromSeconds(10), LockWaitPolicy.Default, default)).Acquired);

        await sut.ReleaseAsync(key, "next", default);
    }
}
