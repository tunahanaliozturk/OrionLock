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
}
