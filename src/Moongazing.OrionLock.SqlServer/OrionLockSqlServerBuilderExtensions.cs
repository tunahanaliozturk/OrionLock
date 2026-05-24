using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.SqlServer;

/// <summary>Registers the SQL Server (<c>sp_getapplock</c>) OrionLock backend.</summary>
public static class OrionLockSqlServerBuilderExtensions
{
    /// <summary>
    /// Uses SQL Server <c>sp_getapplock</c> as the OrionLock backend. The provider opens a
    /// dedicated <see cref="Microsoft.Data.SqlClient.SqlConnection"/> per active lock and
    /// holds it for the lifetime of the handle.
    /// </summary>
    public static OrionLockBuilder UseSqlServer(
        this OrionLockBuilder builder,
        string connectionString,
        Action<SqlServerLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new SqlServerLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            _ => new SqlServerLockProvider(connectionString, options));

        return builder;
    }
}
