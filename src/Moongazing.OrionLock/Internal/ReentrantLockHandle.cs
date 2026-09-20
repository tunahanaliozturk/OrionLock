namespace Moongazing.OrionLock.Internal;

/// <summary>
/// A nested handle returned for a reentrant (same key, same process) acquisition. Its
/// <see cref="DisposeAsync"/> decrements the reentrancy count; the real backend handle is
/// disposed only when the outermost handle is disposed.
/// </summary>
internal sealed class ReentrantLockHandle : IDistributedLockHandle
{
    private readonly ReentrancyRegistry registry;
    private readonly ReentrancyRegistry.Entry entry;
    private int disposed;

    /// <summary>Creates a nested handle over a registry entry.</summary>
    public ReentrantLockHandle(ReentrancyRegistry registry, ReentrancyRegistry.Entry entry)
    {
        this.registry = registry;
        this.entry = entry;
    }

    /// <inheritdoc />
    public string Key => entry.Key;

    /// <inheritdoc />
    public bool IsHeld => entry.RealHandle.IsHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => entry.RealHandle.LostToken;

    /// <inheritdoc />
    /// <remarks>
    /// A reentrant handle is the SAME hold seen from deeper in the call stack, not a second one, so it
    /// reports the outer acquisition's token verbatim. Minting a fresh number here would make one
    /// critical section present two tokens to the resource, and the resource would reject whichever
    /// write happened to carry the lower one.
    /// </remarks>
    public long? FencingToken => entry.RealHandle.FencingToken;

    /// <inheritdoc />
    /// <remarks>A nested handle rides the outermost hold's lease; it takes none of its own.</remarks>
    public TimeSpan EffectiveLeaseDuration => entry.RealHandle.EffectiveLeaseDuration;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (registry.Exit(entry))
        {
            await entry.RealHandle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
