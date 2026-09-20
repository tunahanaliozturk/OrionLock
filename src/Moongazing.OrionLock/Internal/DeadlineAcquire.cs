using System.Diagnostics;

namespace Moongazing.OrionLock;

/// <summary>
/// v0.5.0: shared poll-until-deadline helper behind the <see cref="ISharedExclusiveLock"/>
/// acquire-or-give-up-by-deadline overloads. Polls a single-shot try-acquire on
/// <see cref="DistributedLockOptions.RetryInterval"/> until it succeeds or <c>deadline</c> elapses,
/// returning <see langword="null"/> on expiry rather than throwing
/// <see cref="LockAcquisitionTimeoutException"/>. Cancellation still surfaces as
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal static class DeadlineAcquire
{
    /// <summary>
    /// Repeatedly invokes <paramref name="tryOnce"/> until it returns a non-null handle or
    /// <paramref name="deadline"/> lapses. A non-positive deadline performs exactly one attempt. The
    /// inter-attempt delay is clamped to the time left so a full <see cref="DistributedLockOptions.RetryInterval"/>
    /// near the deadline cannot overshoot the caller's budget by up to one interval, matching the
    /// blocking <see cref="SharedExclusiveLock"/> acquire loop.
    /// </summary>
    public static async Task<IDistributedLockHandle?> TryAcquireUntilDeadlineAsync(
        Func<string, DistributedLockOptions?, CancellationToken, Task<IDistributedLockHandle?>> tryOnce,
        string key,
        TimeSpan deadline,
        DistributedLockOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tryOnce);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var elapsed = Stopwatch.StartNew();
        // v2.1: the sleep is jittered rather than flat. With no RetryBackoffCeiling the window
        // collapses onto RetryInterval, so this is the same flat interval as before; set a ceiling
        // and waiters that arrived together stop waking on the same tick for the whole drain.
        var pollOptions = options.ToWaitPolicy().ToPollOptions();
        var rng = pollOptions.RandomFactory();
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = await tryOnce(key, options, cancellationToken).ConfigureAwait(false);
            if (handle is not null)
            {
                return handle;
            }

            var remaining = deadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var backoff = Providers.DistributedLockProviderExtensions.ComputeJitteredDelay(pollOptions, attempts, rng);
            var delay = backoff < remaining ? backoff : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            attempts++;
        }
    }
}
