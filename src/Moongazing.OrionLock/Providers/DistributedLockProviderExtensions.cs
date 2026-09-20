namespace Moongazing.OrionLock.Providers;

/// <summary>
/// Polling helpers that compose blocking-acquire semantics on top of the single-shot
/// <see cref="IDistributedLockProvider.TryAcquireAsync"/> primitive.
/// </summary>
public static class DistributedLockProviderExtensions
{
    /// <summary>
    /// Poll <see cref="IDistributedLockProvider.TryAcquireAsync"/> with exponential
    /// backoff + jitter until acquisition succeeds or <paramref name="acquireTimeout"/>
    /// elapses. Returns <see langword="true"/> on success, <see langword="false"/> on
    /// timeout. Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="provider">The lock backend.</param>
    /// <param name="key">Lock key.</param>
    /// <param name="ownerToken">Caller-supplied owner identity (typically a Guid N).</param>
    /// <param name="leaseDuration">TTL applied on success - the lock is released after this if not renewed.</param>
    /// <param name="acquireTimeout">Maximum time to wait for acquisition. Use <see cref="Timeout.InfiniteTimeSpan"/> to block until acquired or cancellation.</param>
    /// <param name="options">Backoff options. Null defaults to <see cref="WaitForAcquireOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the lock was acquired before timeout; <see langword="false"/> on timeout.</returns>
    /// <remarks>
    /// <para>
    /// Backoff schedule: each unsuccessful try sleeps for a duration randomly chosen in
    /// <c>[InitialDelay, InitialDelay * 2^attempts]</c>, capped at <c>MaxDelay</c>. The
    /// random jitter reduces thundering-herd behaviour when many waiters race for the
    /// same key.
    /// </para>
    /// <para>
    /// The helper never sleeps PAST the deadline - if the next backoff would overshoot,
    /// it sleeps just enough to reach the deadline, returns <see langword="false"/>, and
    /// the caller can decide whether to retry.
    /// </para>
    /// </remarks>
    public static async Task<bool> WaitForAcquireAsync(
        this IDistributedLockProvider provider,
        string key,
        string ownerToken,
        TimeSpan leaseDuration,
        TimeSpan acquireTimeout,
        WaitForAcquireOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        // Polls the UNFENCED single attempt, which is what this helper has always done and what its
        // callers' test doubles substitute. PollUntilAcquiredAsync below is the same loop over the
        // fenced overload, for the provider contract that has to carry the token through.
        var acquisition = await PollUntilAcquiredAsync(
            provider,
            key,
            ownerToken,
            leaseDuration,
            acquireTimeout,
            options,
            attempt: async ct => await provider.TryAcquireAsync(key, ownerToken, leaseDuration, ct).ConfigureAwait(false)
                ? LockAcquisition.Unfenced
                : LockAcquisition.NotAcquired,
            cancellationToken).ConfigureAwait(false);
        return acquisition.Acquired;
    }

    /// <summary>
    /// The poll loop behind <see cref="IDistributedLockProvider.WaitForAcquireAsync"/>'s default
    /// implementation: repeated single attempts with exponential-ceiling jitter until one wins or
    /// the budget runs out, carrying the winning attempt's fencing token out with it.
    /// </summary>
    /// <remarks>
    /// Shared with the public <c>WaitForAcquireAsync</c> extension above, which supplies the
    /// unfenced attempt instead. One loop rather than two, because the deadline clamping and the
    /// never-call-past-the-deadline gate are the parts that are easy to get subtly wrong twice.
    /// </remarks>
    public static Task<LockAcquisition> PollUntilAcquiredAsync(
        this IDistributedLockProvider provider,
        string key,
        string ownerToken,
        TimeSpan leaseDuration,
        TimeSpan acquireTimeout,
        WaitForAcquireOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollUntilAcquiredAsync(
            provider, key, ownerToken, leaseDuration, acquireTimeout, options, attempt: null, cancellationToken);

    // Same loop, with the single attempt supplied: null means TryAcquireFencedAsync, which is what a
    // backend wants, while the public WaitForAcquireAsync extension above supplies the unfenced one
    // its callers' test doubles substitute.
    internal static async Task<LockAcquisition> PollUntilAcquiredAsync(
        IDistributedLockProvider provider,
        string key,
        string ownerToken,
        TimeSpan leaseDuration,
        TimeSpan acquireTimeout,
        WaitForAcquireOptions? options = null,
        Func<CancellationToken, Task<LockAcquisition>>? attempt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        var opts = options ?? WaitForAcquireOptions.Default;
        opts.ValidateAndNormalise();
        attempt ??= ct => provider.TryAcquireFencedAsync(key, ownerToken, leaseDuration, ct);

        var deadline = acquireTimeout == Timeout.InfiniteTimeSpan
            ? (DateTime?)null
            : DateTime.UtcNow + acquireTimeout;

        var attempts = 0;
        var rng = opts.RandomFactory();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-check the deadline BEFORE every attempt. The previous iteration's
            // clipped delay can leave just enough time for one final unnecessary
            // attempt that runs past the requested timeout; this gate ensures the
            // helper never makes a call after the deadline.
            if (deadline is not null && DateTime.UtcNow >= deadline.Value)
            {
                return LockAcquisition.NotAcquired;
            }

            var acquisition = await attempt(cancellationToken).ConfigureAwait(false);
            if (acquisition.Acquired)
            {
                return acquisition;
            }

            if (deadline is not null && DateTime.UtcNow >= deadline.Value)
            {
                return LockAcquisition.NotAcquired;
            }

            var delay = ComputeJitteredDelay(opts, attempts, rng);
            if (deadline is not null)
            {
                var remaining = deadline.Value - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return LockAcquisition.NotAcquired;
                }
                if (delay > remaining)
                {
                    delay = remaining;
                }
            }
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            attempts++;
        }
    }

    /// <summary>
    /// Exponential ceiling with full jitter, used by every retry loop in the library rather than
    /// only by this helper. With <c>MaxDelay == InitialDelay</c> the jitter window collapses and
    /// the result is a flat <c>InitialDelay</c>, which is the pre-v2.1 behaviour.
    /// </summary>
    internal static TimeSpan ComputeJitteredDelay(WaitForAcquireOptions opts, int attempts, Random rng)
    {
        // Exponential ceiling: InitialDelay * 2^attempts, capped at MaxDelay.
        var ceiling = opts.InitialDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempts, 30));
        var capped = Math.Min(ceiling, opts.MaxDelay.TotalMilliseconds);
        // Random in [InitialDelay, capped] to avoid thundering herd. NextDouble * (max - min)
        // + min - lock keeps the rng instance local to this attempt loop so a parallel
        // call's WaitForAcquire run does not share state.
        var floor = opts.InitialDelay.TotalMilliseconds;
        var jittered = floor + rng.NextDouble() * (capped - floor);
        return TimeSpan.FromMilliseconds(jittered);
    }
}

/// <summary>
/// Backoff configuration for <see cref="DistributedLockProviderExtensions.WaitForAcquireAsync"/>.
/// </summary>
public sealed class WaitForAcquireOptions
{
    /// <summary>
    /// A fresh default-configured instance: 25 ms initial, 2 s cap, system random. Each access
    /// returns a new instance so there is no shared mutable global state - callers may freely set
    /// the properties on the returned object without affecting any other caller.
    /// </summary>
    public static WaitForAcquireOptions Default => new();

    /// <summary>Initial backoff lower bound. Default 25 ms.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Upper bound on per-iteration backoff. Default 2 seconds.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Factory for the random number generator used to pick jitter. Default returns a
    /// fresh <see cref="Random"/> per call so tests can pin a seeded generator. The
    /// resulting <see cref="Random"/> is NOT shared across
    /// <see cref="DistributedLockProviderExtensions.WaitForAcquireAsync(IDistributedLockProvider, string, string, TimeSpan, TimeSpan, WaitForAcquireOptions, CancellationToken)"/>
    /// invocations.
    /// </summary>
    public Func<Random> RandomFactory { get; set; } = () => Random.Shared;

    internal void ValidateAndNormalise()
    {
        if (InitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialDelay), InitialDelay, "InitialDelay must be positive.");
        }
        if (MaxDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDelay), MaxDelay, "MaxDelay must be positive.");
        }
        if (MaxDelay < InitialDelay)
        {
            throw new ArgumentException(
                $"WaitForAcquireOptions: MaxDelay ({MaxDelay}) must be >= InitialDelay ({InitialDelay}).");
        }
    }
}
