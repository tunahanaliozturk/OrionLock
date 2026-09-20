using Moongazing.OrionLock.Diagnostics;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// Tracks, per <see cref="DistributedLock"/> instance, which keys are currently held <em>and by which
/// logical flow</em>, so that a re-acquisition from the flow that already owns the key collapses into a
/// counted nested handle instead of a second backend call. Process-local by design - reentrancy must not
/// cross process boundaries.
/// </summary>
/// <remarks>
/// Ownership matters: <see cref="IDistributedLock"/> is registered as a singleton, so without an owner
/// identity a registry keyed on the lock key alone hands a nested handle to ANY in-process caller of the
/// same key - two unrelated requests would both believe they were inside the critical section while the
/// backend was consulted once. See <see cref="EnsureOwnerScope"/> for how the owner identity is
/// established.
/// </remarks>
internal sealed class ReentrancyRegistry
{
    /// <summary>
    /// One live hold: the real backend handle, the flow that owns it, and the nesting count.
    /// Nested handles keep a reference to their own entry, so a later hold of the same key by the same
    /// flow (after this one was lost or released) can never be decremented by this one's handles.
    /// </summary>
    internal sealed class Entry
    {
        public required string Key { get; init; }
        public required object Owner { get; init; }
        public required IDistributedLockHandle RealHandle { get; init; }
        public int Count;
        // v0.3.26: high-water mark of Count over this key's hold lifetime. Recorded as
        // a histogram sample on the final Exit so operators see the DEEPEST nesting
        // reached per hold, not just the instantaneous depth gauge.
        public int MaxCount = 1;
    }

    // Flow-local owner identity. AsyncLocal flows DOWN into everything the establishing frame
    // subsequently awaits (so a re-entry deeper in the same critical section still matches) and never
    // sideways into an unrelated flow such as another in-flight HTTP request.
    private static readonly AsyncLocal<object?> currentOwner = new();

    // All access is serialised by the gate, so a plain Dictionary is enough.
    private readonly Dictionary<(string Key, object Owner), Entry> held = [];
    private readonly object gate = new();

    /// <summary>
    /// Returns the calling flow's owner identity, creating one if this flow does not have one yet.
    /// </summary>
    /// <remarks>
    /// MUST be called from a SYNCHRONOUS frame of the caller (i.e. from a non-<c>async</c> method
    /// entry point). An <c>async</c> method's state machine saves and restores the ambient
    /// <see cref="ExecutionContext"/> around its body, so an <see cref="AsyncLocal{T}"/> assigned inside
    /// one is discarded the moment it returns and the caller's later re-entry would never see it.
    /// Assigned from a plain method it behaves like <c>Activity.Current</c>: the caller's flow keeps the
    /// value across its own awaits for the rest of its lifetime.
    /// </remarks>
    public static object EnsureOwnerScope() => currentOwner.Value ??= new object();

    /// <summary>
    /// If <paramref name="key"/> is already held <em>by <paramref name="owner"/></em> on a lease that is
    /// still live, increments its count and returns a nested handle. Otherwise returns null and the
    /// caller must acquire the backend lock, then call <see cref="Register"/>.
    /// </summary>
    public IDistributedLockHandle? TryEnter(string key, object owner)
    {
        lock (gate)
        {
            if (!held.TryGetValue((key, owner), out var entry))
            {
                return null;
            }

            // The watchdog can surrender the lease (renewal failure / grace exhausted) while the flow is
            // still running. Handing out a nested handle over a dead lease would put the caller inside
            // the critical section with nothing holding the key at the backend, so fall through and let
            // it contend for a real lease instead.
            if (!entry.RealHandle.IsHeld)
            {
                return null;
            }

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
            return new ReentrantLockHandle(this, entry);
        }
    }

    /// <summary>Records a freshly acquired backend handle and returns the outermost nested handle.</summary>
    public IDistributedLockHandle Register(string key, object owner, IDistributedLockHandle realHandle)
    {
        lock (gate)
        {
            var entry = new Entry { Key = key, Owner = owner, RealHandle = realHandle, Count = 1 };
            // Replaces any entry left behind by a lost lease for the same flow and key. That entry's
            // outstanding handles still Exit against their own Entry object, so the dead hold is
            // released exactly once and this new one is untouched.
            held[(key, owner)] = entry;
            return new ReentrantLockHandle(this, entry);
        }
    }

    /// <summary>
    /// Decrements the count for <paramref name="entry"/>. Returns true when the count reaches zero,
    /// meaning the caller must dispose the real backend handle.
    /// </summary>
    public bool Exit(Entry entry)
    {
        lock (gate)
        {
            entry.Count--;
            if (entry.Count > 0)
            {
                // v0.3.17: a non-terminal Exit corresponds to a nested handle's
                // dispose; decrement the depth gauge to match the IncrementReentrancyDepth
                // in TryEnter.
                OrionLockDiagnostics.DecrementReentrancyDepth();
                return false;
            }

            var mapKey = (entry.Key, entry.Owner);
            // Only unpublish if this entry is still the current hold for the key+owner: a lease lost
            // mid-flow can already have been superseded by a fresh Register.
            if (held.TryGetValue(mapKey, out var current) && ReferenceEquals(current, entry))
            {
                held.Remove(mapKey);
            }
            // v0.3.26: record the deepest nesting reached for this hold. A
            // MaxCount of 1 (no re-entry) still emits so operators see the full
            // distribution - the p99 reveals how deep real-world re-entry goes.
            OrionLockDiagnostics.RecordReentrancyMaxDepth(entry.MaxCount);
            return true;
        }
    }
}
