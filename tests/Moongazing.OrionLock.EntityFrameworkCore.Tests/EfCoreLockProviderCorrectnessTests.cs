using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.EntityFrameworkCore;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

/// <summary>
/// Regression tests for the three correctness defects in <see cref="EfCoreLockProvider"/>, each written so
/// it runs on SQLite alone - no container needed - while still failing against the pre-fix provider:
/// <list type="number">
/// <item>lease arithmetic done on the application host's clock instead of the database's, which lets two
/// hosts with clock drift hold the same key;</item>
/// <item>table and column identifiers spliced into raw SQL unquoted, which is only legal on SQLite;</item>
/// <item><c>catch (DbUpdateException)</c> around raw SQL, which never matches the provider's own
/// <see cref="DbException"/> and so let a first-use primary-key violation escape the provider.</item>
/// </list>
/// </summary>
public sealed class EfCoreLockProviderCorrectnessTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    // ---- Finding 1: expiry is decided by the DATABASE clock -------------------------------------------

    [Fact]
    public async Task LeaseExpiry_IsStampedInTheDatabaseClockFrame_NotTheHostClock()
    {
        // The DB clock is pushed an hour ahead of this host's clock. Whichever clock the provider actually
        // uses is then unambiguous from the stored expiry: a provider doing its arithmetic on
        // DateTime.UtcNow stamps ~30s ahead of the host, one reading the database stamps ~1h30s ahead.
        // That offset is exactly the NTP drift that makes the pre-fix provider hand the same key to two
        // hosts at once, reproduced deterministically in one process.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var services = BuildServices(conn, new ShiftDbClockInterceptor("+1 hour"));

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());
        var key = NewKey();
        var hostNow = DateTime.UtcNow;

        Assert.True(await provider.TryAcquireAsync(key, "owner-1", Lease, default));

        var expiresAfterAcquire = await ReadExpiryAsync(services, key);
        Assert.True(
            expiresAfterAcquire - hostNow > TimeSpan.FromMinutes(50),
            $"Acquire stamped {expiresAfterAcquire:O}, only {expiresAfterAcquire - hostNow} past the host "
            + "clock: the expiry was computed on the host clock, not the database clock.");

        // Renew must read the same authoritative clock, otherwise every renewal walks the expiry back into
        // the host's frame and undoes the guarantee one heartbeat later.
        Assert.True(await provider.TryRenewAsync(key, "owner-1", Lease, default));
        var expiresAfterRenew = await ReadExpiryAsync(services, key);
        Assert.True(
            expiresAfterRenew - DateTime.UtcNow > TimeSpan.FromMinutes(50),
            $"Renew stamped {expiresAfterRenew:O}: the renewal expiry was computed on the host clock.");
    }

    [Fact]
    public void LiveClockExpression_NeverFallsBackToTheClientClock()
    {
        // The fallback for an unrecognised provider is still a SERVER clock read, not DateTime.UtcNow.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var services = BuildServices(conn);
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        Assert.Equal("CURRENT_TIMESTAMP", EfCoreDbClock.LiveClockExpression(ctx));
    }

    // ---- Finding 2: identifiers are delimited for the active provider ---------------------------------

    [Fact]
    public async Task ReservedWordIdentifiers_AreDelimited_SoTheSqlIsValid()
    {
        // The shipped mapping happens to use names SQLite accepts bare, which is why the unquoted SQL
        // passed for so long. Map the same row onto names that are RESERVED WORDS and the pre-fix SQL is a
        // syntax error on SQLite too - the same failure the shipped names produce on SQL Server (where
        // `Key` is reserved) and PostgreSQL (which case-folds the bare table name).
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<ReservedWordLockContext>(o => o.UseSqlite(conn));
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<ReservedWordLockContext>());
        await using var services = sc.BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReservedWordLockContext>()
                .Database.EnsureCreatedAsync();
        }

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());
        var key = NewKey();

        Assert.True(await provider.TryAcquireAsync(key, "owner-1", Lease, default));
        Assert.False(await provider.TryAcquireAsync(key, "owner-2", Lease, default));
        Assert.True(await provider.TryRenewAsync(key, "owner-1", Lease, default));
        await provider.ReleaseAsync(key, "owner-1", default);
        Assert.True(await provider.TryAcquireAsync(key, "owner-2", Lease, default));
    }

    // ---- Finding 3: a first-use primary-key violation is absorbed, not thrown -------------------------

    [Fact]
    public async Task FirstUseInsert_LosingThePrimaryKeyRace_ReturnsFalse_InsteadOfThrowing()
    {
        // Two callers can both pass `WHERE NOT EXISTS` on a brand-new key under READ COMMITTED; the loser
        // gets the provider's own DbException. SQLite serialises writers so it cannot produce that race on
        // its own - the interceptor raises the real driver error the loser would see, which is the point:
        // pre-fix, `catch (DbUpdateException)` did not match it and it escaped TryAcquireAsync.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var services = BuildServices(conn, new FailInsertInterceptor(
            new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: OrionLock_Locks.Key'.", 19, 1555)));

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());

        Assert.False(await provider.TryAcquireAsync(NewKey(), "owner-1", Lease, default));
    }

    [Fact]
    public async Task FirstUseInsert_FailingForAnyOtherReason_StillThrows()
    {
        // The catch must stay narrow: swallowing every DbException would turn a broken schema or a denied
        // permission into a silent "lock unavailable" that the caller retries forever.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var services = BuildServices(conn, new FailInsertInterceptor(
            new SqliteException("SQLite Error 1: 'no such table: OrionLock_Locks'.", 1)));

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<SqliteException>(
            () => provider.TryAcquireAsync(NewKey(), "owner-1", Lease, default));
    }

    [Theory]
    // Every one of these is SQLSTATE class 23 but NOT a unique violation. Matching the class treated all
    // of them as "someone else got there first", so TryAcquireAsync returned false and the caller retried
    // forever against a schema that can never accept the row. They have to reach the caller.
    [InlineData("23502", "null value in column \"OwnerToken\" violates not-null constraint")]
    [InlineData("23503", "insert or update on table \"OrionLock_Locks\" violates foreign key constraint \"fk_tenant\"")]
    [InlineData("23514", "new row for relation \"OrionLock_Locks\" violates check constraint \"ck_key_format\"")]
    [InlineData("23001", "restrict violation")]
    public async Task FirstUseInsert_HittingSomeOtherConstraint_StillThrows(string sqlState, string message)
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var services = BuildServices(conn, new FailInsertInterceptor(
            new FakeDbException(message, sqlState)));

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<FakeDbException>(
            () => provider.TryAcquireAsync(NewKey(), "owner-1", Lease, default));
    }

    [Fact]
    public async Task FirstUseInsert_HittingTheActualUniqueViolation_IsStillTreatedAsContention()
    {
        // The narrowing must not go so far that the case it exists for stops working: 23505 is the one
        // SQLSTATE that really does mean another acquirer inserted the row first.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var services = BuildServices(conn, new FailInsertInterceptor(
            new FakeDbException(
                "duplicate key value violates unique constraint \"PK_OrionLock_Locks\"", "23505")));

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());

        Assert.False(await provider.TryAcquireAsync(NewKey(), "owner-1", Lease, default));
    }

    [Fact]
    public async Task FirstUseInsert_HittingSqlServersDuplicateKeyNumber_IsTreatedAsContention()
    {
        // SQL Server reports no SQLSTATE, so it is matched on its own error numbers and wording.
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var services = BuildServices(conn, new FailInsertInterceptor(
            new FakeDbException(
                "Violation of PRIMARY KEY constraint 'PK_OrionLock_Locks'. Cannot insert duplicate key "
                + "in object 'dbo.OrionLock_Locks'.",
                sqlState: null)));

        var provider = new EfCoreLockProvider(services.GetRequiredService<IServiceScopeFactory>());

        Assert.False(await provider.TryAcquireAsync(NewKey(), "owner-1", Lease, default));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    private static ServiceProvider BuildServices(SqliteConnection conn, IInterceptor? interceptor = null)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LockTestDbContext>(o =>
        {
            o.UseSqlite(conn);
            if (interceptor is not null)
            {
                o.AddInterceptors(interceptor);
            }
        });
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<LockTestDbContext>());
        var services = sc.BuildServiceProvider();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LockTestDbContext>().Database.EnsureCreated();
        return services;
    }

    private static async Task<DateTime> ReadExpiryAsync(IServiceProvider services, string key)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<LockTestDbContext>();
        var row = await ctx.Set<OrionLockRow>().AsNoTracking().SingleAsync(x => x.Key == key);
        return DateTime.SpecifyKind(row.ExpiresOnUtc, DateTimeKind.Utc);
    }
}

/// <summary>
/// Moves the database's clock by a SQLite datetime modifier, so the server clock and this host's clock
/// provably disagree and the provider's choice between them is observable.
/// </summary>
internal sealed class ShiftDbClockInterceptor(string modifier) : DbCommandInterceptor
{
    private const string ClockSql = "SELECT CURRENT_TIMESTAMP AS Value";

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Shift(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Shift(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Shift(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandText.Contains(ClockSql, StringComparison.Ordinal))
        {
            command.CommandText = $"SELECT datetime(CURRENT_TIMESTAMP, '{modifier}') AS Value";
        }
    }
}

/// <summary>Raises a chosen driver error on the provider's first-use INSERT.</summary>
internal sealed class FailInsertInterceptor(DbException error) : DbCommandInterceptor
{
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Throw(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Throw(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Throw(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandText.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase))
        {
            throw error;
        }
    }
}

/// <summary>Maps the lock row onto identifiers that are reserved words in SQLite.</summary>
public sealed class ReservedWordLockContext(DbContextOptions<ReservedWordLockContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new ReservedWordLockRowConfiguration());
    }
}

internal sealed class ReservedWordLockRowConfiguration : IEntityTypeConfiguration<OrionLockRow>
{
    public void Configure(EntityTypeBuilder<OrionLockRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Group");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasColumnName("Index").HasMaxLength(200).IsRequired();
        builder.Property(x => x.OwnerToken).HasColumnName("Order").HasMaxLength(64);
        builder.Property(x => x.ExpiresOnUtc).HasColumnName("Limit");
    }
}

/// <summary>
/// A <see cref="DbException"/> with a chosen SQLSTATE. <see cref="DbException.SqlState"/> is virtual and
/// provider-supplied, and no provider this test project references raises the class-23 codes that are NOT
/// unique violations, so the distinction the provider has to draw is only reachable through a fake.
/// </summary>
internal sealed class FakeDbException(string message, string? sqlState) : DbException(message)
{
    public override string? SqlState { get; } = sqlState;
}
