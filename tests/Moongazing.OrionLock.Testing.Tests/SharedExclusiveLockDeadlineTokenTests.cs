using System.Collections.Concurrent;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

/// <summary>
/// Regression tests for the TryAcquire-with-deadline owner-token identity (PR #52, FINDING 2): a
/// deadline-retried acquire must use ONE stable owner token across every poll attempt, exactly like the
/// blocking <see cref="SharedExclusiveLock.AcquireExclusiveAsync"/> loop. A fresh token per attempt would
/// make each retry a different logical acquirer, break fencing identity, and orphan a partially
/// succeeded attempt's pending-writer reservation under a token no later retry can clear.
/// </summary>
public sealed class SharedExclusiveLockDeadlineTokenTests
{
    /// <summary>
    /// Wraps an inner provider and records the owner token of every <see cref="TryAcquireAsync"/> call so a
    /// test can assert how many DISTINCT tokens a single deadline-retry acquire used.
    /// </summary>
    private sealed class TokenRecordingProvider(ISharedExclusiveLockProvider inner) : ISharedExclusiveLockProvider
    {
        private readonly ISharedExclusiveLockProvider inner = inner;

        public ConcurrentQueue<string> AcquireTokens { get; } = new();

        public Task<bool> TryAcquireAsync(
            string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            AcquireTokens.Enqueue(ownerToken);
            return inner.TryAcquireAsync(key, ownerToken, mode, leaseDuration, cancellationToken);
        }

        public Task<bool> TryRenewAsync(
            string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => inner.TryRenewAsync(key, ownerToken, mode, leaseDuration, cancellationToken);

        public Task ReleaseAsync(string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
            => inner.ReleaseAsync(key, ownerToken, mode, cancellationToken);

        public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;
    }

    private static DistributedLockOptions Fast() => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(30),
        WaitTimeout = TimeSpan.FromMilliseconds(200),
        RetryInterval = TimeSpan.FromMilliseconds(10),
        AutoRenew = false,
    };

    [Fact]
    public async Task DeadlineRetriedExclusive_UsesOneStableToken_AcrossAllAttempts()
    {
        var recorder = new TokenRecordingProvider(new InMemorySharedExclusiveLockProvider());
        var locker = new SharedExclusiveLock(recorder);

        // A reader holds the key, so the exclusive deadline-acquire must retry several times before giving
        // up. Each retry is a fresh TryAcquireAsync call into the recorder.
        await using var reader = await locker.AcquireSharedAsync("k", Fast());

        // Drop the reader-setup token(s) so the queue records ONLY the deadline-retried exclusive acquire.
        recorder.AcquireTokens.Clear();

        var result = await locker.TryAcquireExclusiveAsync("k", TimeSpan.FromMilliseconds(120), Fast());
        Assert.Null(result); // reader never drained, so the deadline lapses

        // The whole point: every attempt of this one acquire call carried the SAME owner token. With the
        // pre-fix per-attempt token, this set would have one entry per poll attempt (several distinct
        // Guids); with the fix it is exactly one.
        Assert.True(recorder.AcquireTokens.Count > 1, "expected the deadline acquire to retry more than once");
        var distinct = recorder.AcquireTokens.Distinct().Count();
        Assert.Equal(1, distinct);
    }

    [Fact]
    public async Task DeadlineRetriedExclusive_SameToken_ClearsItsOwnReservation_LettingNewReaderInAfterRelease()
    {
        // End-to-end behavioural consequence of the stable token: a deadline-retried writer that drains a
        // reader and wins clears ITS OWN pending-writer reservation (keyed by owner token). A per-attempt
        // token would orphan that reservation, and a new reader would be wrongly refused after release.
        var locker = new SharedExclusiveLock(new InMemorySharedExclusiveLockProvider());

        var reader = await locker.AcquireSharedAsync("k", Fast());

        var writerTask = locker.TryAcquireExclusiveAsync("k", TimeSpan.FromSeconds(5), Fast());
        await reader.DisposeAsync(); // drain the only reader so the retrying writer wins under its stable token

        var writer = await writerTask;
        Assert.NotNull(writer);
        Assert.True(writer!.IsHeld);

        await writer.DisposeAsync();

        // No stale reservation may linger: a single-shot shared acquire succeeds immediately. It would
        // return null if a reservation under an orphaned token were still denying new readers.
        await using var s = await locker.TryAcquireSharedAsync("k", Fast());
        Assert.NotNull(s);
        Assert.True(s!.IsHeld);
    }
}
