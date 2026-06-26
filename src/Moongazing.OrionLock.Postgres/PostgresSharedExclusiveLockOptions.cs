namespace Moongazing.OrionLock.Postgres;

/// <summary>
/// Configuration for the PostgreSQL reader-writer (shared/exclusive) OrionLock backend
/// (<see cref="PostgresSharedExclusiveLockProvider"/>). Kept separate from
/// <see cref="PostgresLockOptions"/> so the exclusive-only advisory-lock fast path and the
/// reader-writer path can be tuned and namespaced independently.
/// </summary>
public sealed class PostgresSharedExclusiveLockOptions
{
    /// <summary>
    /// Prefix prepended to every reader-writer lock key (the logical <c>resource</c> column value).
    /// Default empty. Distinct from <see cref="PostgresLockOptions.KeyPrefix"/> so a reader-writer
    /// hold and an exclusive-only advisory hold on the same logical key name never alias.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Unquoted PostgreSQL table name backing the reader-writer holds. Default
    /// <c>orionlock_rw_holds</c>. Must be a valid simple identifier (letters, digits, underscore;
    /// not starting with a digit); it is validated and embedded as a constant identifier rather than
    /// parameterised, because PostgreSQL does not allow a table name to be a query parameter.
    /// </summary>
    public string TableName { get; set; } = "orionlock_rw_holds";

    /// <summary>
    /// When <see langword="true"/> (the default), the provider issues an idempotent
    /// <c>CREATE TABLE IF NOT EXISTS</c> the first time it touches the backend, so a fresh database
    /// works with no migration step. Set to <see langword="false"/> if the schema is created out of
    /// band (a migration, least-privilege deployment) and the provider's role lacks DDL rights.
    /// </summary>
    public bool AutoCreateTable { get; set; } = true;

    /// <summary>
    /// Timeout applied to every command (schema bootstrap, acquire, renew, release). Bounds network
    /// hangs, not lock contention; contention is handled by the OrionLock retry loop above the
    /// provider. Default 30 seconds.
    /// </summary>
    /// <remarks>
    /// Npgsql expresses command timeouts in whole seconds, where 0 means no timeout. A positive
    /// sub-second value is therefore rounded UP to 1 second (the smallest finite value the underlying
    /// API can carry) rather than truncated to 0, so a sub-second timeout is honoured as a real (short)
    /// timeout instead of silently becoming infinite. A non-positive value is treated as a deliberate
    /// "no timeout" (Npgsql's native 0 = infinite). Use <see cref="TimeSpan.Zero"/> only when you really
    /// want commands to wait indefinitely.
    /// </remarks>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
