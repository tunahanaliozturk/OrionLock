using System.Collections.Concurrent;
using Moongazing.OrionLock.Diagnostics;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// Tracks, per <see cref="DistributedLock"/> instance, which keys are currently held so a
/// re-acquisition of the same key collapses into a counted nested handle instead of a second
/// backend call. Process-local by design — reentrancy must not cross process boundaries.
/// </summary>
internal sealed class ReentrancyRegistry
{
    private sealed class Entry
    {
        public required IDistributedLockHandle RealHandle { get; init; }
        public int Count;
        // v0.3.26: high-water mark of Count over this key's hold lifetime. Recorded as
        // a histogram sample on the final Exit so operators see the DEEPEST nesting
        // reached per hold, not just the instantaneous depth gauge.
        public int MaxCount = 1;
    }

    private readonly ConcurrentDictionary<string, Entry> held = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>
    /// If <paramref name="key"/> is already held, increments its count and returns a nested handle.
    /// Otherwise returns null and the caller must acquire the backend lock, then call
    /// <see cref="Register"/>.
    /// </summary>
    public IDistributedLockHandle? TryEnter(string key)
    {
        lock (gate)
        {
            if (held.TryGetValue(key, out var entry))
            {
                entry.Count++;
                // v0.3.26: track the high-water mark for the max-depth histogram.
                if (entry.Count > entry.MaxCount)
                {
                    entry.MaxCount = entry.Count;
                }
                // v0.3.17: only nested re-entries increment the depth gauge. The
                // outermost entry's depth is established by Register and is reset on
                // the final Exit; depth therefore answers 'how many NESTED handles are
                // outstanding right now', not 'how many keys are held'.
                OrionLockDiagnostics.IncrementReentrancyDepth();
                return new ReentrantLockHandle(this, key, entry.RealHandle);
            }
            return null;
        }
    }

    /// <summary>Records a freshly acquired backend handle and returns the outermost nested handle.</summary>
    public IDistributedLockHandle Register(string key, IDistributedLockHandle realHandle)
    {
        lock (gate)
        {
            var entry = new Entry { RealHandle = realHandle, Count = 1 };
            held[key] = entry;
            return new ReentrantLockHandle(this, key, realHandle);
        }
    }

    /// <summary>
    /// Decrements the count for <paramref name="key"/>. Returns true when the count reaches zero,
    /// meaning the caller must dispose the real backend handle.
    /// </summary>
    public bool Exit(string key)
    {
        lock (gate)
        {
            if (held.TryGetValue(key, out var entry))
            {
                entry.Count--;
                if (entry.Count <= 0)
                {
                    held.TryRemove(key, out _);
                    // v0.3.26: record the deepest nesting reached for this hold. A
                    // MaxCount of 1 (no re-entry) still emits so operators see the full
                    // distribution - the p99 reveals how deep real-world re-entry goes.
                    OrionLockDiagnostics.RecordReentrancyMaxDepth(entry.MaxCount);
                    return true;
                }
                // v0.3.17: a non-terminal Exit corresponds to a nested handle's
                // dispose; decrement the depth gauge to match the IncrementReentrancyDepth
                // in TryEnter.
                OrionLockDiagnostics.DecrementReentrancyDepth();
            }
            return false;
        }
    }
}
