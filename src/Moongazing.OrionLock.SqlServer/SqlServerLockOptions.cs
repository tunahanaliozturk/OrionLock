namespace Moongazing.OrionLock.SqlServer;

/// <summary>Configuration for the SQL Server <c>sp_getapplock</c> OrionLock backend.</summary>
public sealed class SqlServerLockOptions
{
    /// <summary>
    /// Prefix prepended to every lock key. Default empty. The combined length of
    /// <see cref="KeyPrefix"/> and the supplied key must not exceed 240 characters
    /// (SQL Server <c>sp_getapplock</c> @Resource is <c>nvarchar(255)</c>; the
    /// remaining 15 characters are a safety margin).
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Timeout applied to every command (<c>sp_getapplock</c>, <c>SELECT 1</c>,
    /// <c>sp_releaseapplock</c>). Bounds network hangs, not lock contention —
    /// contention is handled by the OrionLock retry loop above the provider.
    /// Default 30 seconds.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
