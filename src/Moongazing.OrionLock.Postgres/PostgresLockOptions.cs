namespace Moongazing.OrionLock.Postgres;

/// <summary>Configuration for the PostgreSQL <c>pg_try_advisory_lock</c> OrionLock backend.</summary>
public sealed class PostgresLockOptions
{
    /// <summary>
    /// Prefix prepended to every lock key before hashing. Default empty. Postgres advisory
    /// locks use a 64-bit integer key, so this prefix is for namespacing logically only;
    /// there is no hard length limit on the post-prefix string the way SQL Server has.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Timeout applied to every command (acquire, renew probe, release). Bounds network
    /// hangs, not lock contention; contention is handled by the OrionLock retry loop above
    /// the provider. Default 30 seconds.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
