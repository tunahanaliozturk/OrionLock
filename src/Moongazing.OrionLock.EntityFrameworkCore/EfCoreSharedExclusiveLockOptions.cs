namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// v0.6.0: configuration for the EF Core reader-writer (shared/exclusive) OrionLock backend
/// (<see cref="EfCoreSharedExclusiveLockProvider"/>). Kept separate from the exclusive-only EF Core
/// lock-table backend so the reader-writer path can be tuned independently.
/// </summary>
public sealed class EfCoreSharedExclusiveLockOptions
{
    /// <summary>
    /// Prefix prepended to every reader-writer lock key (the logical <c>Resource</c> column value).
    /// Default empty. Lets a reader-writer hold and an exclusive-only lock-table hold on the same logical
    /// key name be namespaced apart.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// How many times a single transition is retried when its serializable transaction loses a
    /// serialization / deadlock conflict against a concurrent transition for the SAME resource. The
    /// per-resource serialization is implemented by a contended row write under <c>Serializable</c>
    /// isolation, so a conflict is expected and benign under contention and the transition simply re-runs.
    /// Default 64. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// This bounds RETRIES of an internally-serialized transition, not lock contention between distinct
    /// holders. Lock contention (a writer waiting on readers) is handled by the OrionLock retry loop above
    /// the provider, exactly as for the other backends. A conflict here means several callers happened to
    /// drive a transition for the same key at the same instant; one commits and the others re-run. The
    /// default is generous because a burst of many simultaneous holders of ONE key (for example a fleet of
    /// readers acquiring at once) makes every reader's transition serialize behind the others, so the
    /// later arrivals can need several re-runs each before they win the anchor write. The retries use
    /// exponential backoff with jitter (see <see cref="SerializationRetryBaseDelay"/>) so a large burst
    /// drains instead of livelocking.
    /// </remarks>
    public int MaxSerializationRetries { get; set; } = 64;

    /// <summary>
    /// Base delay for the serialization-conflict backoff. Each retry waits a randomised value in
    /// <c>[0, base * 2^min(attempt, 6))</c> (exponential backoff with full jitter, capped at 64x the
    /// base), so a burst of colliding callers spreads out instead of all retrying in lockstep. Default
    /// 4 ms. Non-positive disables the backoff (immediate retry).
    /// </summary>
    public TimeSpan SerializationRetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(4);
}
