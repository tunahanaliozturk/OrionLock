namespace Moongazing.OrionLock.Internal;

/// <summary>
/// v0.3.25 safe-invocation helpers for <see cref="ILockEventObserver"/>. Each wrapper
/// swallows observer exceptions so an audit-side fault can never disrupt the
/// load-bearing lock path - the same fate model used by every Orion observer hook
/// since v0.2.18.
/// </summary>
internal static class LockEventObserverSafeInvoke
{
    internal static void SafeOnAcquired(this ILockEventObserver? observer, string key, double durationMs)
    {
        if (observer is null or NullLockEventObserver)
        {
            return;
        }
        try
        {
            observer.OnAcquired(key, durationMs);
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // observer is observability, not load-bearing
        }
    }

    internal static void SafeOnAcquireTimedOut(this ILockEventObserver? observer, string key, double waitMs)
    {
        if (observer is null or NullLockEventObserver)
        {
            return;
        }
        try
        {
            observer.OnAcquireTimedOut(key, waitMs);
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // observer is observability, not load-bearing
        }
    }

    internal static void SafeOnLeaseLost(this ILockEventObserver? observer, string key)
    {
        if (observer is null or NullLockEventObserver)
        {
            return;
        }
        try
        {
            observer.OnLeaseLost(key);
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // observer is observability, not load-bearing
        }
    }

    internal static void SafeOnReleased(this ILockEventObserver? observer, string key)
    {
        if (observer is null or NullLockEventObserver)
        {
            return;
        }
        try
        {
            observer.OnReleased(key);
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // observer is observability, not load-bearing
        }
    }
}
