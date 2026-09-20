namespace Moongazing.OrionLock.Providers;

/// <summary>
/// What a waiter should do while it waits, handed to
/// <see cref="IDistributedLockProvider.WaitForAcquireAsync"/> so a backend that cannot block or
/// subscribe - and a backend whose subscription dropped - can fall back to polling on the caller's
/// own terms rather than on a figure it invented.
/// </summary>
/// <remarks>
/// <para>
/// A backend that blocks server-side (SQL Server <c>sp_getapplock</c>, PostgreSQL
/// <c>pg_advisory_lock</c>) or subscribes to a release signal (Redis, etcd, Consul, ZooKeeper)
/// ignores this for the happy path; it matters only where the wait degrades back to polling.
/// </para>
/// <para>
/// <see cref="BackoffCeiling"/> null - the default - means a flat <see cref="RetryInterval"/>,
/// which is exactly what every waiter did before this type existed. Set it and the poll becomes
/// exponential with full jitter between <see cref="RetryInterval"/> and the ceiling, so N waiters
/// that arrive together stop waking on the same tick forever.
/// </para>
/// </remarks>
/// <param name="RetryInterval">
/// Floor of the fallback poll: the shortest a waiter ever sleeps between attempts.
/// Non-positive values are treated as "as fast as the scheduler allows".
/// </param>
/// <param name="BackoffCeiling">
/// Upper bound of the exponential backoff, or <see langword="null"/> for a flat
/// <paramref name="RetryInterval"/> with no backoff and no jitter.
/// </param>
public readonly record struct LockWaitPolicy(TimeSpan RetryInterval, TimeSpan? BackoffCeiling = null)
{
    /// <summary>The library default: poll every 250 ms, flat, exactly as every waiter did before.</summary>
    public static LockWaitPolicy Default => new(TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Projects onto the backoff options the shipped poll helper takes. A null ceiling - or one
    /// below the floor - collapses the jitter window onto the floor, which is a flat
    /// <see cref="RetryInterval"/> to the millisecond.
    /// </summary>
    internal WaitForAcquireOptions ToPollOptions()
    {
        // WaitForAcquireOptions rejects a non-positive delay, and a caller asking for a zero
        // interval means "spin", not "throw". One tick is the smallest thing Task.Delay can be
        // handed; it rounds to a yield, which is what a zero interval did before.
        var floor = RetryInterval > TimeSpan.Zero ? RetryInterval : TimeSpan.FromTicks(1);
        var ceiling = BackoffCeiling is { } declared && declared > floor ? declared : floor;
        return new WaitForAcquireOptions { InitialDelay = floor, MaxDelay = ceiling };
    }
}
