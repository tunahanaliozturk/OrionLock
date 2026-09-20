namespace Moongazing.OrionLock;

/// <summary>
/// A held distributed lock. Dispose to release. While alive, a background watchdog renews the
/// lease (when <see cref="DistributedLockOptions.AutoRenew"/> is set); if renewal fails,
/// <see cref="IsHeld"/> becomes false and <see cref="LostToken"/> is cancelled.
/// </summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>The lock key this handle holds.</summary>
    string Key { get; }

    /// <summary>True while the lease is held; false once released or lost.</summary>
    bool IsHeld { get; }

    /// <summary>Cancelled if the lease is lost while the handle is alive.</summary>
    CancellationToken LostToken { get; }

    /// <summary>
    /// The fencing token this acquisition minted, or <see langword="null"/> when the backend cannot
    /// produce one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LostToken"/> covers the case where OrionLock <em>knows</em> the lease is gone. Fencing
    /// covers the case it cannot know about: the process paused long enough for the lease to expire,
    /// someone else acquired, and this one woke up still believing it holds the lock. The token is
    /// strictly increasing per key across acquisitions and across processes; pass it to the resource you
    /// are protecting and have the resource reject any write carrying a token lower than the highest it
    /// has seen. See <c>docs/fencing-tokens.md</c> for the worked example, including the SQL.
    /// </para>
    /// <para>
    /// <see langword="null"/> is honest, not a failure: it means this backend has nothing it can make
    /// strictly monotonic, and OrionLock will not fabricate a counter that looks like a token but is not
    /// one. A caller that requires a token should say so with
    /// <see cref="Fencing.FencingExtensions.RequireFencingToken"/> rather than silently passing
    /// <see langword="null"/> down to the resource.
    /// </para>
    /// </remarks>
    long? FencingToken => null;

    /// <summary>
    /// The lease the backend is actually honouring for this hold, which is not always the
    /// <see cref="DistributedLockOptions.LeaseDuration"/> that was asked for.
    /// </summary>
    /// <remarks>
    /// <see cref="Timeout.InfiniteTimeSpan"/> means the hold is not bounded by a wall clock at all: it
    /// lives for the backend session (PostgreSQL advisory locks, SQL Server <c>sp_getapplock</c>,
    /// ZooKeeper ephemeral znodes), so it survives until release or session loss however long that is.
    /// Otherwise it is the wall-clock TTL after which the backend reclaims the key if renewal stops -
    /// on etcd, rounded up to a whole second. This is the value that decides how long another process
    /// waits to take over after this one crashes, so it is exposed rather than left to be inferred from
    /// the backend's documentation.
    /// </remarks>
    TimeSpan EffectiveLeaseDuration { get; }
}
