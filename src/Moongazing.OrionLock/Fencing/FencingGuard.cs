namespace Moongazing.OrionLock.Fencing;

/// <summary>
/// Thrown when a write presents a fencing token LOWER than the highest one the resource has already
/// accepted for that key - i.e. when a holder that had already been superseded came back. A token equal
/// to the highest is the current holder writing again and is not an error.
/// </summary>
public sealed class FencingTokenRegressedException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public FencingTokenRegressedException(string key, long presented, long highestSeen)
        : base($"Fencing token {presented} for '{key}' is lower than the highest already accepted "
            + $"({highestSeen}); the caller no longer holds the lock it thinks it holds.")
    {
        Key = key;
        Presented = presented;
        HighestSeen = highestSeen;
    }

    /// <summary>The resource key the rejected write was for.</summary>
    public string Key { get; }

    /// <summary>The token the write presented.</summary>
    public long Presented { get; }

    /// <summary>The highest token already accepted for <see cref="Key"/>.</summary>
    public long HighestSeen { get; }
}

/// <summary>
/// The resource side of the fencing pattern for an <em>in-process</em> resource: remembers the highest
/// token accepted per key and rejects anything that does not beat it.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately the whole feature. If the resource you are protecting is a database, the check
/// belongs IN the database - one <c>WHERE</c> clause on the same statement that does the write, so the
/// comparison and the write are one atomic step. See <c>docs/fencing-tokens.md</c> for that SQL. An
/// in-process guard cannot help you there, because two application instances would each keep their own
/// idea of the highest token.
/// </para>
/// <para>
/// Use this when the resource genuinely lives in this process (an in-memory cache, a file this process
/// owns, an outbound client whose ordering you control) and for tests that exercise the pattern.
/// Instances are thread-safe.
/// </para>
/// </remarks>
public sealed class FencingGuard
{
    private readonly Dictionary<string, long> highest = [];
    // Plain object, not System.Threading.Lock: this package still targets net8.0.
    private readonly object gate = new();

    /// <summary>
    /// Accepts <paramref name="token"/> for <paramref name="key"/> unless it is LOWER than the highest
    /// already accepted, recording it as the new highest when it is higher. Returns false, without
    /// recording anything, only for a token below the high-water mark.
    /// </summary>
    /// <remarks>
    /// A token equal to the mark is accepted, and must be. The token identifies an ACQUISITION, not a
    /// write, and it is stable for the whole hold - so a critical section that touches the resource more
    /// than once presents the same number each time, and every one of those calls comes from the current
    /// holder. Rejecting the repeat would fail perfectly ordinary code with a "someone superseded you"
    /// error when nobody had. What fencing rejects is a token from a holder that has been overtaken, and
    /// that token is strictly lower.
    /// </remarks>
    public bool TryAccept(string key, long token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (gate)
        {
            if (highest.TryGetValue(key, out var seen) && token < seen)
            {
                return false;
            }
            highest[key] = token;
            return true;
        }
    }

    /// <summary>
    /// <see cref="TryAccept"/>, but throws <see cref="FencingTokenRegressedException"/> instead of
    /// returning false. Use this at the top of a write path so a stale holder fails loudly rather than
    /// falling into an <c>if</c> nobody wrote.
    /// </summary>
    public void Accept(string key, long token)
    {
        if (!TryAccept(key, token))
        {
            long seen;
            lock (gate)
            {
                seen = highest[key];
            }
            throw new FencingTokenRegressedException(key, token, seen);
        }
    }

    /// <summary>The highest token accepted for <paramref name="key"/>, or null if none yet.</summary>
    public long? HighestAccepted(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (gate)
        {
            return highest.TryGetValue(key, out var seen) ? seen : null;
        }
    }
}

/// <summary>Fencing conveniences over <see cref="IDistributedLockHandle"/>.</summary>
public static class FencingExtensions
{
    /// <summary>
    /// Returns <see cref="IDistributedLockHandle.FencingToken"/>, throwing when the backend did not mint
    /// one.
    /// </summary>
    /// <remarks>
    /// Call this wherever the correctness of the code below depends on fencing. The failure mode it
    /// prevents is quiet: a <see langword="null"/> token flows into the resource check, the check
    /// compares against nothing, and the protection is gone while every test still passes. Failing at
    /// acquire time instead says plainly that this backend cannot give you what the code assumes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The backend minted no token for this acquisition.</exception>
    public static long RequireFencingToken(this IDistributedLockHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return handle.FencingToken
            ?? throw new InvalidOperationException(
                $"The backend holding '{handle.Key}' does not mint fencing tokens, so this acquisition "
                + "has none. Either use a backend that does (see docs/fencing-tokens.md for the table) "
                + "or stop depending on a token here.");
    }
}
