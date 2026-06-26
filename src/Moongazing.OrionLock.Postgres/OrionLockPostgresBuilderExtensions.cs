using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Postgres;

/// <summary>Registers the PostgreSQL (<c>pg_try_advisory_lock</c>) OrionLock backend.</summary>
public static class OrionLockPostgresBuilderExtensions
{
    /// <summary>
    /// Uses PostgreSQL <c>pg_try_advisory_lock</c> as the OrionLock backend. The provider opens
    /// a dedicated <see cref="Npgsql.NpgsqlConnection"/> per active lock and holds it for the
    /// lifetime of the handle.
    /// </summary>
    public static OrionLockBuilder UsePostgres(
        this OrionLockBuilder builder,
        string connectionString,
        Action<PostgresLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new PostgresLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            _ => new PostgresLockProvider(connectionString, options));

        return builder;
    }

    /// <summary>
    /// Adds the PostgreSQL distributed reader-writer (shared/exclusive) backend (v0.5.0):
    /// <see cref="PostgresSharedExclusiveLockProvider"/> as <see cref="ISharedExclusiveLockProvider"/>
    /// plus the composed <see cref="ISharedExclusiveLock"/>. Additive to the exclusive-only
    /// <see cref="UsePostgres(OrionLockBuilder, string, Action{PostgresLockOptions})"/> registration;
    /// both <see cref="IDistributedLockProvider"/> and <see cref="ISharedExclusiveLockProvider"/> can
    /// then resolve against PostgreSQL. Uses <c>TryAddSingleton</c>, matching the exclusive backend, so
    /// a consumer-supplied implementation registered earlier wins.
    /// </summary>
    /// <remarks>
    /// Unlike the exclusive-only advisory-lock backend, the reader-writer provider models holds as
    /// clock-leased rows in a table (default <c>orionlock_rw_holds</c>), so a per-reader fencing token
    /// and an explicit expiry can be tracked. By default the provider idempotently creates that table
    /// on first use; set <see cref="PostgresSharedExclusiveLockOptions.AutoCreateTable"/> to
    /// <see langword="false"/> to manage the schema out of band.
    /// </remarks>
    public static OrionLockBuilder UsePostgresSharedExclusive(
        this OrionLockBuilder builder,
        string connectionString,
        Action<PostgresSharedExclusiveLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new PostgresSharedExclusiveLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<ISharedExclusiveLockProvider>(
            _ => new PostgresSharedExclusiveLockProvider(connectionString, options));
        builder.Services.TryAddSingleton<ISharedExclusiveLock>(
            sp => new SharedExclusiveLock(sp.GetRequiredService<ISharedExclusiveLockProvider>()));

        return builder;
    }
}
