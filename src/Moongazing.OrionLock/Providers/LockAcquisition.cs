namespace Moongazing.OrionLock.Providers;

/// <summary>
/// The outcome of one single-shot acquire attempt, including the fencing token the backend minted
/// for it. Returned by <see cref="IDistributedLockProvider.TryAcquireFencedAsync"/>.
/// </summary>
/// <param name="Acquired">True when this attempt took the lock.</param>
/// <param name="FencingToken">
/// The token this acquisition minted, or <see langword="null"/> when the backend cannot produce one
/// that is strictly increasing per key. A backend MUST return <see langword="null"/> rather than a
/// number that is only nearly monotonic: callers trust a token, and an almost-monotonic one is worse
/// than none at all.
/// </param>
public readonly record struct LockAcquisition(bool Acquired, long? FencingToken)
{
    /// <summary>The attempt lost the race; nothing was taken and no token was minted.</summary>
    public static LockAcquisition NotAcquired => default;

    /// <summary>The lock was taken, but this backend cannot mint a monotonic fencing token.</summary>
    public static LockAcquisition Unfenced => new(true, null);

    /// <summary>The lock was taken and the backend minted <paramref name="fencingToken"/> for it.</summary>
    public static LockAcquisition Fenced(long fencingToken) => new(true, fencingToken);
}
