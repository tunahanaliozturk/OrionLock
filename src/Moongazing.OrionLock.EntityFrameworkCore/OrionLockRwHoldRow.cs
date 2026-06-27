namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// v0.6.0: one row per reader-writer hold backing
/// <see cref="EfCoreSharedExclusiveLockProvider"/>. Each hold (a reader, the single writer, or the
/// single pending-writer marker) is an individual row with its own <see cref="ExpiresOnUtc"/>, so a
/// reader is tracked individually (never a bare counter) and one reader's expiry never frees another's.
/// </summary>
/// <remarks>
/// The composite key <c>(Resource, Kind, OwnerToken)</c> makes reader rows naturally many-per-resource
/// while the acquire predicates keep at most one live writer (<c>w</c>) and one pending-writer
/// (<c>pw</c>) row per resource. This mirrors the PostgreSQL <c>orionlock_rw_holds</c> schema exactly,
/// but the provider drives it through provider-agnostic EF Core so it works on SQL Server, PostgreSQL,
/// and any other relational EF Core provider rather than only PostgreSQL.
/// </remarks>
public sealed class OrionLockRwHoldRow
{
    /// <summary>The logical resource (lock key with any configured prefix). Part of the composite key.</summary>
    public string Resource { get; set; } = default!;

    /// <summary>
    /// Hold kind: <c>r</c> (a reader), <c>w</c> (the single writer), or <c>pw</c> (the single
    /// pending-writer marker). Part of the composite key.
    /// </summary>
    public string Kind { get; set; } = default!;

    /// <summary>
    /// The hold owner's fencing token. For a reader this distinguishes one reader row from another; for
    /// the writer and pending-writer rows it is the (single) holder's fencing token. Part of the
    /// composite key.
    /// </summary>
    public string OwnerToken { get; set; } = default!;

    /// <summary>The lease deadline (UTC). A row whose deadline has passed is pruned and treated as free.</summary>
    public DateTime ExpiresOnUtc { get; set; }
}
