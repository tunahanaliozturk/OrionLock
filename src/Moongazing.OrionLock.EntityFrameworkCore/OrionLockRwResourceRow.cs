namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// v0.6.0: one row per resource, the per-resource serialization anchor for
/// <see cref="EfCoreSharedExclusiveLockProvider"/>. Every reader-writer transition first writes this
/// row (an unconditional <c>UPDATE</c> of <see cref="Token"/>) inside a serializable transaction; two
/// concurrent transitions for the same resource therefore conflict on this single row and one is forced
/// to retry, giving per-resource serialization on any relational EF Core provider without provider-
/// specific lock hints (<c>FOR UPDATE</c>, <c>pg_advisory_xact_lock</c>, <c>sp_getapplock</c>).
/// </summary>
/// <remarks>
/// This is the portable substitute for the PostgreSQL provider's <c>pg_advisory_xact_lock</c>: rather
/// than a backend-native advisory lock, a contended row write under <c>Serializable</c> isolation makes
/// the database's own concurrency control serialize the prune-evaluate-write sequence per resource. The
/// <see cref="Token"/> column carries no meaning of its own; rewriting it is purely the conflict point.
/// </remarks>
public sealed class OrionLockRwResourceRow
{
    /// <summary>The logical resource (lock key with any configured prefix). Primary key.</summary>
    public string Resource { get; set; } = default!;

    /// <summary>
    /// An opaque token rewritten on every transition. Its only purpose is to make the row write a real
    /// change so concurrent serializable transitions for the same resource conflict and serialize.
    /// </summary>
    public string Token { get; set; } = default!;
}
