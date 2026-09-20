namespace Moongazing.OrionLock.Redis;

/// <summary>
/// The one lease-to-milliseconds conversion every Redis provider in this package uses.
/// </summary>
internal static class RedisLease
{
    /// <summary>
    /// Converts a lease <see cref="TimeSpan"/> to the integer-millisecond <c>PX</c> / score value Redis
    /// takes, rejecting non-positive leases and never truncating a positive lease to zero.
    /// </summary>
    /// <remarks>
    /// A raw <c>(long)leaseDuration.TotalMilliseconds</c> FLOORS a positive sub-millisecond lease to
    /// <c>0</c>, and zero is not a short lease in Redis - it is destructive. <c>PEXPIRE key 0</c> DELETES
    /// the key, so the first renewal of a sub-millisecond lock would drop the lock outright and hand it
    /// to anyone waiting; <c>SET ... PX 0</c> is rejected outright as an invalid expire time. The raw cast
    /// would also silently accept zero and negative durations. Rounding up with
    /// <see cref="Math.Ceiling(double)"/> guarantees every positive lease maps to at least <c>1</c> ms,
    /// and the guard makes a non-positive lease a caller error rather than a silently broken lock. Every
    /// lease-to-ms conversion in the package - exclusive and shared/exclusive, acquire and renew, and
    /// (transitively, via <c>ARGV[2]</c>) the pending-writer marker TTL - routes through here so the
    /// normalization cannot be bypassed.
    /// </remarks>
    /// <param name="leaseDuration">The requested lease duration.</param>
    /// <returns>The lease length in whole milliseconds, always &gt;= 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="leaseDuration"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    internal static long ToMilliseconds(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        return (long)Math.Ceiling(leaseDuration.TotalMilliseconds);
    }
}
