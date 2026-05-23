namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// Persistent row backing <see cref="EfCoreLockProvider"/>. One row per lock key in the
/// <c>OrionLock_Locks</c> table.
/// </summary>
public sealed class OrionLockRow
{
    /// <summary>The lock key (primary key).</summary>
    public string Key { get; set; } = default!;

    /// <summary>The current owner token, or null when the lock is free.</summary>
    public string? OwnerToken { get; set; }

    /// <summary>The lease deadline. A row whose deadline has passed is free for any caller.</summary>
    public DateTime ExpiresOnUtc { get; set; }
}
