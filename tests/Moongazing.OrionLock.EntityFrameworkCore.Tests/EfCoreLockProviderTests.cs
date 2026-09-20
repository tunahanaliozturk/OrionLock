using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.EntityFrameworkCore;
using Moongazing.OrionLock.Tests.Containers;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

public sealed class LockTestDbContext(DbContextOptions<LockTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());
}

/// <summary>
/// The provider's behaviour contract, written once and driven against every relational provider the
/// package claims to support. Plain methods rather than a test base class so the SQLite suite can run them
/// as ordinary facts while the PostgreSQL / SQL Server suites gate the same assertions behind Docker.
/// </summary>
internal static class EfCoreLockProviderScenarios
{
    public static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    public static async Task AcquireBlocksSecondOwner(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();
        Assert.True(await p.TryAcquireAsync(k, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync(k, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    public static async Task AcquireSucceedsAfterLeaseExpires(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();
        // The expiry instant now comes from the DATABASE clock, and SQLite's CURRENT_TIMESTAMP has
        // whole-second resolution. A 2.5s gap against a 200ms lease guarantees the server-side second
        // counter has advanced past the stored expiry on every provider, however the boundary falls.
        await p.TryAcquireAsync(k, "owner-1", TimeSpan.FromMilliseconds(200), default);
        await Task.Delay(2500);
        Assert.True(await p.TryAcquireAsync(k, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    public static async Task RenewExtendsForOwnerAndRejectsNonOwner(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();
        await p.TryAcquireAsync(k, "owner-1", TimeSpan.FromSeconds(2), default);
        Assert.True(await p.TryRenewAsync(k, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync(k, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    public static async Task ReleaseOnlyForOwner(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();
        await p.TryAcquireAsync(k, "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync(k, "owner-2", default);
        Assert.False(await p.TryAcquireAsync(k, "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync(k, "owner-1", default);
        Assert.True(await p.TryAcquireAsync(k, "owner-3", TimeSpan.FromSeconds(30), default));
    }

    public static async Task ExactlyOneWinnerAcrossParallelCallers(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();
        // Every one of these is a FIRST-use acquirer on a brand-new key, so each races to INSERT the row.
        // On a real concurrent backend one insert wins and the rest hit a primary-key violation, which the
        // provider must absorb as "you lost" rather than letting the driver's DbException escape.
        var tasks = Enumerable.Range(0, 20)
            .Select(i => p.TryAcquireAsync(k, $"owner-{i}", TimeSpan.FromSeconds(30), default))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));
    }

    public static async Task FencingTokenStrictlyIncreasesAcrossAcquisitions(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();

        var tokens = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var taken = await p.TryAcquireFencedAsync(k, $"owner-{i}", TimeSpan.FromSeconds(30), default);
            Assert.True(taken.Acquired);
            tokens.Add(Assert.NotNull(taken.FencingToken));
            await p.ReleaseAsync(k, $"owner-{i}", default);
        }

        // First use inserts the row at 1; every later acquisition bumps the column in the same UPDATE
        // that takes it. Release leaves the row in place, so the counter never restarts.
        Assert.Equal([1L, 2L, 3L, 4L], tokens);
    }

    public static async Task FencingTokenIsUniqueAcrossParallelAcquirers(IServiceScopeFactory f)
    {
        var p = new EfCoreLockProvider(f);
        var k = NewKey();
        var tokens = new List<long>();

        for (var round = 0; round < 5; round++)
        {
            // Twenty callers race; exactly one wins, and the one that wins must be handed a number no
            // previous winner was given. A token read in a query separate from the UPDATE could see a
            // later holder's value here.
            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(i => p.TryAcquireFencedAsync(k, $"r{round}-owner-{i}", TimeSpan.FromSeconds(30), default)));

            var winners = results.Where(r => r.Acquired).ToArray();
            var winner = Assert.Single(winners);
            tokens.Add(Assert.NotNull(winner.FencingToken));

            var winnerIndex = Array.IndexOf(results, winner);
            await p.ReleaseAsync(k, $"r{round}-owner-{winnerIndex}", default);
        }

        Assert.Equal(tokens.OrderBy(t => t).Distinct().ToList(), tokens);
    }

    public static async Task FencingIsSkippedWhenTheColumnIsNotMapped(IServiceScopeFactory f)
    {
        // The escape hatch for a database that has not had the column added yet: Ignore() the property
        // and the provider emits the pre-fencing SQL and reports no token, rather than failing every
        // acquire against a column the database does not have.
        var p = new EfCoreLockProvider(f);
        var k = NewKey();

        var taken = await p.TryAcquireFencedAsync(k, "owner-1", TimeSpan.FromSeconds(30), default);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
        Assert.False((await p.TryAcquireFencedAsync(k, "owner-2", TimeSpan.FromSeconds(30), default)).Acquired);
    }
}

/// <summary>
/// A context whose model deliberately drops <see cref="OrionLockRow.FencingToken"/>, standing in for a
/// consumer whose database predates the column.
/// </summary>
public sealed class UnfencedLockTestDbContext(DbContextOptions<UnfencedLockTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());
        modelBuilder.Entity<OrionLockRow>().Ignore(x => x.FencingToken);
    }
}

public sealed class EfCoreLockProviderTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection connection = default!;
    private IServiceProvider services = default!;

    public Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<LockTestDbContext>(o => o.UseSqlite(connection));
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<LockTestDbContext>());
        services = sc.BuildServiceProvider();

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LockTestDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        connection?.Dispose();
        (services as IDisposable)?.Dispose();
    }

    private IServiceScopeFactory Factory => services.GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
        => EfCoreLockProviderScenarios.AcquireBlocksSecondOwner(Factory);

    [Fact]
    public Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
        => EfCoreLockProviderScenarios.AcquireSucceedsAfterLeaseExpires(Factory);

    [Fact]
    public Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
        => EfCoreLockProviderScenarios.RenewExtendsForOwnerAndRejectsNonOwner(Factory);

    [Fact]
    public Task Release_ShouldOnlyReleaseForOwner()
        => EfCoreLockProviderScenarios.ReleaseOnlyForOwner(Factory);

    [Fact]
    public Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
        => EfCoreLockProviderScenarios.ExactlyOneWinnerAcrossParallelCallers(Factory);

    [Fact]
    public Task FencingToken_ShouldStrictlyIncrease_AcrossAcquisitions()
        => EfCoreLockProviderScenarios.FencingTokenStrictlyIncreasesAcrossAcquisitions(Factory);

    [Fact]
    public Task FencingToken_ShouldBeUnique_AcrossParallelAcquirers()
        => EfCoreLockProviderScenarios.FencingTokenIsUniqueAcrossParallelAcquirers(Factory);
}

/// <summary>
/// The same provider against a model that has no <c>FencingToken</c> column, which is what an upgraded
/// consumer sees before they have run the migration.
/// </summary>
public sealed class EfCoreUnfencedLockProviderTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection connection = default!;
    private IServiceProvider services = default!;

    public Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<UnfencedLockTestDbContext>(o => o.UseSqlite(connection));
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<UnfencedLockTestDbContext>());
        services = sc.BuildServiceProvider();

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<UnfencedLockTestDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        connection?.Dispose();
        (services as IDisposable)?.Dispose();
    }

    [Fact]
    public Task Acquire_ShouldStillWork_AndReportNoToken()
        => EfCoreLockProviderScenarios.FencingIsSkippedWhenTheColumnIsNotMapped(
            services.GetRequiredService<IServiceScopeFactory>());
}

/// <summary>
/// The same contract on a real PostgreSQL. This is the suite that catches a dialect bug: an unquoted
/// <c>OrionLock_Locks</c> case-folds to <c>orionlock_locks</c> here and the relation does not exist, so
/// every one of these fails against the pre-fix SQL while SQLite passes it.
/// </summary>
public sealed class PostgresEfCoreLockProviderTests(PostgresRwContainerFixture fixture)
    : IClassFixture<PostgresRwContainerFixture>
{
    [DockerFact]
    public Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
        => EfCoreLockProviderScenarios.AcquireBlocksSecondOwner(fixture.ScopeFactory);

    [DockerFact]
    public Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
        => EfCoreLockProviderScenarios.AcquireSucceedsAfterLeaseExpires(fixture.ScopeFactory);

    [DockerFact]
    public Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
        => EfCoreLockProviderScenarios.RenewExtendsForOwnerAndRejectsNonOwner(fixture.ScopeFactory);

    [DockerFact]
    public Task Release_ShouldOnlyReleaseForOwner()
        => EfCoreLockProviderScenarios.ReleaseOnlyForOwner(fixture.ScopeFactory);

    [DockerFact]
    public Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
        => EfCoreLockProviderScenarios.ExactlyOneWinnerAcrossParallelCallers(fixture.ScopeFactory);

    [DockerFact]
    public Task FencingToken_ShouldStrictlyIncrease_AcrossAcquisitions()
        => EfCoreLockProviderScenarios.FencingTokenStrictlyIncreasesAcrossAcquisitions(fixture.ScopeFactory);

    [DockerFact]
    public Task FencingToken_ShouldBeUnique_AcrossParallelAcquirers()
        => EfCoreLockProviderScenarios.FencingTokenIsUniqueAcrossParallelAcquirers(fixture.ScopeFactory);
}

/// <summary>
/// The same contract on a real SQL Server, where <c>Key</c> is a RESERVED WORD: the pre-fix unquoted
/// <c>WHERE Key = @p</c> is a syntax error, so this suite is what proves the identifiers are delimited.
/// </summary>
public sealed class SqlServerEfCoreLockProviderTests(SqlServerRwContainerFixture fixture)
    : IClassFixture<SqlServerRwContainerFixture>
{
    [DockerFact]
    public Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
        => EfCoreLockProviderScenarios.AcquireBlocksSecondOwner(fixture.ScopeFactory);

    [DockerFact]
    public Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
        => EfCoreLockProviderScenarios.AcquireSucceedsAfterLeaseExpires(fixture.ScopeFactory);

    [DockerFact]
    public Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
        => EfCoreLockProviderScenarios.RenewExtendsForOwnerAndRejectsNonOwner(fixture.ScopeFactory);

    [DockerFact]
    public Task Release_ShouldOnlyReleaseForOwner()
        => EfCoreLockProviderScenarios.ReleaseOnlyForOwner(fixture.ScopeFactory);

    [DockerFact]
    public Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
        => EfCoreLockProviderScenarios.ExactlyOneWinnerAcrossParallelCallers(fixture.ScopeFactory);

    [DockerFact]
    public Task FencingToken_ShouldStrictlyIncrease_AcrossAcquisitions()
        => EfCoreLockProviderScenarios.FencingTokenStrictlyIncreasesAcrossAcquisitions(fixture.ScopeFactory);

    [DockerFact]
    public Task FencingToken_ShouldBeUnique_AcrossParallelAcquirers()
        => EfCoreLockProviderScenarios.FencingTokenIsUniqueAcrossParallelAcquirers(fixture.ScopeFactory);
}
