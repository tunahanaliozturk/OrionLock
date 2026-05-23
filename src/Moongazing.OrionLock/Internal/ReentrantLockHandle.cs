namespace Moongazing.OrionLock.Internal;

/// <summary>
/// A nested handle returned for a reentrant (same key, same process) acquisition. Its
/// <see cref="DisposeAsync"/> decrements the reentrancy count; the real backend handle is
/// disposed only when the outermost handle is disposed.
/// </summary>
public sealed class ReentrantLockHandle : IDistributedLockHandle
{
    private readonly ReentrancyRegistry registry;
    private readonly IDistributedLockHandle realHandle;
    private int disposed;

    /// <summary>Creates a nested handle.</summary>
    public ReentrantLockHandle(ReentrancyRegistry registry, string key, IDistributedLockHandle realHandle)
    {
        this.registry = registry;
        Key = key;
        this.realHandle = realHandle;
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public bool IsHeld => realHandle.IsHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => realHandle.LostToken;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (registry.Exit(Key))
        {
            await realHandle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
