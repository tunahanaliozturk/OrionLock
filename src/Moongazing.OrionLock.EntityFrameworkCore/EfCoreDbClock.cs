using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// The ONE authoritative clock every EF Core lock provider in this package does its lease arithmetic
/// against: the database server's clock, never <see cref="DateTime.UtcNow"/> on the application host.
/// </summary>
/// <remarks>
/// <para>
/// Expiry is a comparison between a value one host WROTE and a value another host READS. If each host
/// supplies its own <see cref="DateTime.UtcNow"/>, two hosts whose clocks differ by more than the lease
/// disagree about whether a row is expired, and BOTH can hold the same key - mutual exclusion is lost for
/// as long as the drift exceeds the renewal interval. Reading the instant from the database means every
/// participant compares against the same clock and drift between application hosts becomes irrelevant.
/// </para>
/// <para>
/// The expression must be the real wall clock AT THE INSTANT OF EVALUATION, not transaction-start time.
/// On PostgreSQL <c>CURRENT_TIMESTAMP</c> is frozen for the whole transaction, so a hold that lapsed while
/// this statement waited to be serialized would still look live; <c>clock_timestamp()</c> advances.
/// SQL Server's <c>SYSUTCDATETIME()</c> likewise advances. Every other relational provider - SQLite
/// included - gets <c>CURRENT_TIMESTAMP</c>, which on those providers IS the live server clock and is
/// still a single authoritative clock shared by every application host. There is deliberately no path
/// back to the client clock: a provider this package does not recognise still reads its SERVER's time.
/// </para>
/// </remarks>
internal static class EfCoreDbClock
{
    /// <summary>
    /// Provider-aware LIVE wall-clock SQL expression for <paramref name="ctx"/>'s database provider,
    /// selected off <c>Database.ProviderName</c> so no provider package is referenced (this package
    /// depends only on EntityFrameworkCore.Relational). PostgreSQL is matched first so its
    /// transaction-start <c>CURRENT_TIMESTAMP</c> is never used for expiry math.
    /// </summary>
    /// <remarks>
    /// Internal (not private) so the regression suite can assert the per-provider selection directly: the
    /// staleness this guards against is otherwise masked end-to-end because Npgsql defers <c>BEGIN</c>,
    /// pinning the transaction timestamp only when the first statement runs.
    /// </remarks>
    internal static string LiveClockExpression(DbContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var provider = ctx.Database.ProviderName;
        if (provider is null)
        {
            return "CURRENT_TIMESTAMP";
        }

        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            return "clock_timestamp()";
        }

        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return "SYSUTCDATETIME()";
        }

        return "CURRENT_TIMESTAMP";
    }

    /// <summary>
    /// Reads the database server's current time as a UTC instant. All lease arithmetic in this package
    /// starts from this value.
    /// </summary>
    internal static async Task<DateTime> ReadUtcNowAsync(DbContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sql = "SELECT " + LiveClockExpression(ctx) + " AS Value";
        var rows = await ctx.Database
            .SqlQueryRaw<DateTime>(sql)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var value = rows[0];
        // Expiry columns are mapped as UTC instants (timestamp WITH time zone on PostgreSQL / datetime2
        // on SQL Server), so both the stored value and every parameter compared against it must be
        // DateTimeKind.Utc; a non-Utc value would bind as a different PostgreSQL type and the comparison
        // would be silently time-zone-shifted (an expired row would then never be reclaimed). Normalise
        // whatever Kind the provider hands back. Every process reads the same server clock, so this is
        // one consistent comparison frame regardless of the zone label.
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
