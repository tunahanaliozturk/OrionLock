using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Npgsql;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

/// <summary>
/// v0.6.0 PR #53 review-finding regression tests for <see cref="EfCoreSharedExclusiveLockProvider"/>:
/// (1) a hold that expires DURING the per-resource anchor-serialization wait is reclaimed on PostgreSQL
/// (proves the live <c>clock_timestamp()</c> read, not transaction-start <c>CURRENT_TIMESTAMP</c>);
/// (2) concurrent FIRST-use anchor insertion does not throw on a unique / PK violation; (3) the provider
/// resolves the CONFIGURED context type, not just the ambient <see cref="DbContext"/> registration.
/// </summary>
public sealed class EfCoreRwReviewFindingTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    // ---- Finding 3: the configured context type is used, not the ambient DbContext registration --------

    [Fact]
    public async Task ConfiguredContextType_IsUsed_EvenWhenAnotherDbContextIsRegistered()
    {
        // Two SQLite in-memory databases, one per context type. Only ConfiguredRwContext has the schema.
        // The ambient DbContext registration points at the OTHER context (DecoyRwContext, no schema). If the
        // provider resolved the ambient DbContext it would fault on the missing table; resolving the
        // configured ConfiguredRwContext makes the transition succeed.
        using var configuredConn = new SqliteConnection("Filename=:memory:");
        using var decoyConn = new SqliteConnection("Filename=:memory:");
        configuredConn.Open();
        decoyConn.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<ConfiguredRwContext>(o => o.UseSqlite(configuredConn), ServiceLifetime.Scoped);
        sc.AddDbContext<DecoyRwContext>(o => o.UseSqlite(decoyConn), ServiceLifetime.Scoped);
        // Ambient DbContext resolves to the DECOY, so resolving it would hit a database with no holds table.
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<DecoyRwContext>());
        await using var services = sc.BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ConfiguredRwContext>()
                .Database.EnsureCreatedAsync();
            // Intentionally do NOT create the decoy schema; the configured path must never touch it.
        }

        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var provider = new EfCoreSharedExclusiveLockProvider(
            scopeFactory, new EfCoreSharedExclusiveLockOptions(), typeof(ConfiguredRwContext));

        var key = NewKey();
        Assert.True(await provider.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await provider.TryAcquireAsync(key, "writer-2", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public void Constructor_Rejects_NonDbContextType()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentException>(() => new EfCoreSharedExclusiveLockProvider(
            scopeFactory, new EfCoreSharedExclusiveLockOptions(), typeof(string)));
    }

    // ---- Finding 2: concurrent first-use anchor insertion does not throw (SQLite smoke) ----------------

    [Fact]
    public async Task ConcurrentFirstUse_OnFreshKey_DoesNotThrow_AllReadersAcquire()
    {
        // A burst of readers on a brand-new key: every one is a FIRST-use acquirer that must insert the
        // per-resource anchor row. On a real concurrent backend one insert wins and the others would hit a PK
        // violation without the retry-the-update guard. SQLite serialises writers so it cannot reproduce the
        // race, but it still exercises the first-use code path and asserts no caller throws. The
        // PostgreSQL / SQL Server conformance suite drives the same shape under true concurrency.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<ConfiguredRwContext>(o => o.UseSqlite(conn), ServiceLifetime.Scoped);
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<ConfiguredRwContext>());
        await using var services = sc.BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ConfiguredRwContext>()
                .Database.EnsureCreatedAsync();
        }

        var provider = new EfCoreSharedExclusiveLockProvider(
            services.GetRequiredService<IServiceScopeFactory>(),
            new EfCoreSharedExclusiveLockOptions(), typeof(ConfiguredRwContext));

        var key = NewKey();
        var tasks = Enumerable.Range(0, 20)
            .Select(i => provider.TryAcquireAsync(key, $"reader-{i}", LockMode.Shared, Lease, default))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");
}

/// <summary>The context the lock provider is CONFIGURED against in the finding-3 test (has the schema).</summary>
public sealed class ConfiguredRwContext(DbContextOptions<ConfiguredRwContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());
    }
}

/// <summary>A second registered context (no schema) that the provider must NOT resolve in the finding-3 test.</summary>
public sealed class DecoyRwContext(DbContextOptions<DecoyRwContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());
    }
}

/// <summary>
/// Finding 1, PostgreSQL: the provider must base expiry math on a LIVE wall clock that advances during a
/// transaction (<c>clock_timestamp()</c>), not transaction-start <c>CURRENT_TIMESTAMP</c>, so a hold that
/// lapses while a transition waits on the per-resource anchor is still reclaimed. Two facts cover this:
/// <list type="bullet">
/// <item>A DISCRIMINATOR at the layer the fix lives: the exact SQL the provider selects for PostgreSQL
/// (<see cref="EfCoreSharedExclusiveLockProvider.LiveClockExpression"/>) ADVANCES across a delay inside one
/// transaction, whereas transaction-start <c>CURRENT_TIMESTAMP</c> would not. This is the true RED/GREEN
/// guard: reverting the PostgreSQL branch to <c>CURRENT_TIMESTAMP</c> fails it. A pure end-to-end reclaim
/// test cannot discriminate because Npgsql DEFERS <c>BEGIN</c>, so the transaction timestamp pins only when
/// the anchor statement actually runs (after the wait), accidentally masking the stale-clock bug.</item>
/// <item>An END-TO-END guard: a hold that expires while a later transition is parked on the anchor is
/// reclaimed and a renew of the expired hold fails.</item>
/// </list>
/// </summary>
public sealed class PostgresLiveClockTests(PostgresRwContainerFixture fixture)
    : IClassFixture<PostgresRwContainerFixture>
{
    private readonly PostgresRwContainerFixture fixture = fixture;

    [Fact]
    public async Task ProviderClockExpression_AdvancesWithinTransaction_UnlikeCurrentTimestamp()
    {
        await using var scope = fixture.ScopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RwLockDbContext>();

        // The provider's chosen expression for this PostgreSQL context must be the live wall clock. Asserting
        // the selection AND its runtime behaviour pins finding 1 at the layer the fix operates.
        var expr = EfCoreSharedExclusiveLockProvider.LiveClockExpression(ctx);
        Assert.Equal("clock_timestamp()", expr);

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Pin the transaction timestamp on a first statement, then delay, then read both clocks. The live
        // expression must have advanced past the now-pinned CURRENT_TIMESTAMP; if the provider used
        // CURRENT_TIMESTAMP it would be frozen and a hold expiring during a wait would never prune.
        await using (var pin = conn.CreateCommand())
        {
            pin.Transaction = tx;
            pin.CommandText = "SELECT 1";
            await pin.ExecuteScalarAsync();
        }

        await Task.Delay(700);

        await using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = $"SELECT {expr}, CURRENT_TIMESTAMP";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var live = reader.GetDateTime(0);
        var transactionStart = reader.GetDateTime(1);

        // The live clock has moved well past the frozen transaction-start time (delay was 700ms; assert a
        // conservative 300ms floor so a loaded runner cannot make this flap).
        Assert.True(
            (live - transactionStart) > TimeSpan.FromMilliseconds(300),
            $"live clock did not advance past CURRENT_TIMESTAMP: live={live:O} txStart={transactionStart:O}");
    }

    [Fact]
    public async Task HoldExpiringDuringAnchorWait_IsReclaimed_AndStaleRenewFails()
    {
        var provider = new EfCoreSharedExclusiveLockProvider(
            fixture.ScopeFactory, new EfCoreSharedExclusiveLockOptions());

        var key = "k-" + Guid.NewGuid().ToString("N");
        var lease = TimeSpan.FromMilliseconds(500);

        // A writer takes the hold with a short lease. This also creates the per-resource anchor row, so the
        // blocker below contends on an EXISTING row (an UPDATE lock), exactly the path a real waiter hits.
        Assert.True(await provider.TryAcquireAsync(key, "writer-1", LockMode.Exclusive, lease, default));

        // Hold the anchor row's write lock on a raw connection so any later transition for this resource
        // blocks at TakeResourceAnchorAsync until we release it.
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        await using (var lockCmd = blocker.CreateCommand())
        {
            lockCmd.Transaction = blockerTx;
            lockCmd.CommandText =
                "UPDATE \"OrionLock_RwResources\" SET \"Token\" = @t WHERE \"Resource\" = @r";
            lockCmd.Parameters.AddWithValue("t", Guid.NewGuid().ToString("N"));
            lockCmd.Parameters.AddWithValue("r", key);
            Assert.Equal(1, await lockCmd.ExecuteNonQueryAsync());
        }

        // Start the waiter: it blocks on the anchor row lock held by the blocker.
        var waiter = provider.TryAcquireAsync(key, "reader-1", LockMode.Shared, TimeSpan.FromSeconds(30), default);

        // Let the writer's 500ms lease lapse while the waiter is parked on the anchor. 1500ms is a 3x margin.
        await Task.Delay(1500);
        Assert.False(waiter.IsCompleted, "waiter must still be blocked on the held anchor row");

        // Release the anchor. The waiter proceeds, reads the LIVE clock (now well past the writer's expiry),
        // prunes the dead writer, and acquires the shared hold.
        await blockerTx.CommitAsync();
        await blocker.CloseAsync();

        Assert.True(await waiter);

        // The writer's hold expired during the wait and was pruned, so renewing it must fail.
        Assert.False(await provider.TryRenewAsync(
            key, "writer-1", LockMode.Exclusive, TimeSpan.FromSeconds(30), default));
    }
}

/// <summary>
/// Finding 1, SQL Server: the provider must select <c>SYSUTCDATETIME()</c> (the live UTC server clock).
/// SQL Server's <c>CURRENT_TIMESTAMP</c> is already live (it is not transaction-fixed as on PostgreSQL) but
/// returns LOCAL server time; <c>SYSUTCDATETIME()</c> is the live UTC equivalent the expiry math needs.
/// </summary>
public sealed class SqlServerLiveClockTests(SqlServerRwContainerFixture fixture)
    : IClassFixture<SqlServerRwContainerFixture>
{
    private readonly SqlServerRwContainerFixture fixture = fixture;

    [Fact]
    public async Task ProviderClockExpression_IsLiveUtc()
    {
        await using var scope = fixture.ScopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RwLockDbContext>();

        var expr = EfCoreSharedExclusiveLockProvider.LiveClockExpression(ctx);
        Assert.Equal("SYSUTCDATETIME()", expr);

        // Round-trip the live UTC clock and confirm it tracks wall-clock UTC within a generous skew window.
        var before = DateTime.UtcNow;
        // Concatenation (not interpolation) avoids EF1002; expr is a constant the provider selected, not input.
        var rows = await ctx.Database
            .SqlQueryRaw<DateTime>("SELECT " + expr + " AS Value")
            .ToListAsync();
        var after = DateTime.UtcNow;
        var dbNow = DateTime.SpecifyKind(rows[0], DateTimeKind.Utc);

        Assert.InRange(dbNow, before - TimeSpan.FromSeconds(5), after + TimeSpan.FromSeconds(5));
    }
}
