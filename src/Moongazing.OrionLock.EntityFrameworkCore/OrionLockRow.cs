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

    /// <summary>
    /// How many times this key has been acquired. Incremented by the same UPDATE that takes the row, so
    /// it doubles as the fencing token handed to the acquirer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is never deleted - releasing sets <see cref="OwnerToken"/> to null and leaves the row in
    /// place - so this only ever moves forward, which is what makes it usable as a fencing token.
    /// </para>
    /// <para>
    /// <b>This column is new and needs a migration.</b> If you cannot add it right now, call
    /// <c>builder.Ignore(x =&gt; x.FencingToken)</c> in your own entity configuration: the provider reads
    /// the column name from the EF model, finds nothing mapped, falls back to the pre-fencing SQL and
    /// reports no token. See <c>docs/migrations/orionlock-locks-table.md</c>.
    /// </para>
    /// </remarks>
    public long FencingToken { get; set; }
}
