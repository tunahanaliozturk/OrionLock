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
}
