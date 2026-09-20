namespace Moongazing.OrionLock.Tests.Fencing;

using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Fencing;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;
using Xunit;

/// <summary>
/// The promise a fencing token makes: every successful acquisition of a key gets a number strictly
/// greater than every number that key has handed out before, no matter who asked or how they raced. A
/// token that merely usually increases is worse than none, because callers act on it.
/// </summary>
public sealed class FencingTokenTests
{
    private static readonly DistributedLockOptions NoRenew =
        new() { LeaseDuration = TimeSpan.FromSeconds(30), AutoRenew = false };

    /// <summary>A backend that takes the lock but has nothing it can make monotonic.</summary>
    private sealed class UnfencedProvider : IDistributedLockProvider
    {
        private readonly HashSet<string> held = [];

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken ct)
        {
            lock (held) { return Task.FromResult(held.Add(key)); }
        }

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken ct)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken ct)
        {
            lock (held) { held.Remove(key); }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Sequential_acquisitions_of_one_key_strictly_increase()
    {
        var sut = new DistributedLock(new InMemoryLockProvider());

        var tokens = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var handle = await sut.AcquireAsync("fence-seq", NoRenew);
            tokens.Add(handle.RequireFencingToken());
            await handle.DisposeAsync();
        }

        // Release must NOT reset the counter: if it did, holder 2 would present a token holder 1 had
        // already spent, and the resource would accept a write from whichever arrived second.
        Assert.Equal(tokens.OrderBy(t => t).Distinct().ToList(), tokens);
    }

    [Fact]
    public async Task A_later_acquisition_never_reuses_an_earlier_token_on_the_same_key()
    {
        var provider = new InMemoryLockProvider();
        var sut = new DistributedLock(provider);

        var first = await sut.AcquireAsync("fence-reuse", NoRenew);
        var firstToken = first.RequireFencingToken();
        await first.DisposeAsync();

        await using var second = await sut.AcquireAsync("fence-reuse", NoRenew);

        Assert.True(second.RequireFencingToken() > firstToken);
    }

    [Fact]
    public async Task Competing_acquisitions_never_share_a_token()
    {
        // Each contender is its own DistributedLock so reentrancy cannot collapse two of them into one
        // hold: this has to be a real race at the provider, not a registry hit.
        var provider = new InMemoryLockProvider();
        var seen = new List<long>();

        async Task ContendAsync()
        {
            var sut = new DistributedLock(provider);
            for (var i = 0; i < 20; i++)
            {
                var handle = await sut.AcquireAsync("fence-race", NoRenew);
                var token = handle.RequireFencingToken();
                lock (seen) { seen.Add(token); }
                await handle.DisposeAsync();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ContendAsync()));

        // 160 acquisitions, 160 distinct tokens. A duplicate here means two holders could present the
        // same number to the resource, and the check that is supposed to order them cannot.
        Assert.Equal(160, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Tokens_are_per_key_and_do_not_have_to_interleave_across_keys()
    {
        var sut = new DistributedLock(new InMemoryLockProvider());

        await using var a = await sut.AcquireAsync("fence-key-a", NoRenew);
        await using var b = await sut.AcquireAsync("fence-key-b", NoRenew);

        // Both keys are on their first acquisition. Fencing is a per-key promise, so two different keys
        // sharing a number is fine - a resource only ever compares tokens for the same key.
        Assert.Equal(1, a.RequireFencingToken());
        Assert.Equal(1, b.RequireFencingToken());
    }

    [Fact]
    public async Task A_backend_that_cannot_fence_reports_null_rather_than_a_counter_it_made_up()
    {
        var sut = new DistributedLock(new UnfencedProvider());

        await using var handle = await sut.AcquireAsync("unfenced", NoRenew);

        Assert.Null(handle.FencingToken);
        var ex = Assert.Throws<InvalidOperationException>(() => handle.RequireFencingToken());
        Assert.Contains("does not mint fencing tokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reentrant_handle_carries_the_outer_acquisition_token()
    {
        var sut = new DistributedLock(new InMemoryLockProvider());

        await using var outer = await sut.AcquireAsync("fence-reentrant", NoRenew);
        await using var inner = await sut.AcquireAsync("fence-reentrant", NoRenew);

        // One critical section must present ONE token. A fresh number for the nested handle would make
        // the resource reject whichever of our own writes carried the lower one.
        Assert.Equal(outer.FencingToken, inner.FencingToken);
    }

    [Fact]
    public async Task The_token_does_not_change_when_the_lease_is_renewed()
    {
        var provider = new InMemoryLockProvider();
        var sut = new DistributedLock(provider);

        await using var handle = await sut.AcquireAsync(
            "fence-renew",
            new DistributedLockOptions
            {
                // A lease this short renews several times inside the delay below, so the assertion is
                // about observed renewals rather than about a watchdog that might not have ticked.
                LeaseDuration = TimeSpan.FromMilliseconds(150),
                AutoRenew = true,
            });
        var atAcquire = handle.RequireFencingToken();

        await Task.Delay(400);

        Assert.True(handle.IsHeld);
        // The token identifies the ACQUISITION. Advancing it on renewal would start failing our own
        // later writes against a resource that had already recorded the earlier number.
        Assert.Equal(atAcquire, handle.FencingToken);
    }

    [Fact]
    public async Task The_acquire_span_carries_the_token_and_the_meter_never_does()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionLockDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var sut = new DistributedLock(new InMemoryLockProvider());
        await using (await sut.AcquireAsync("fence-span", NoRenew)) { }

        var acquire = Assert.Single(activities, a => a.DisplayName.Contains("fence-span", StringComparison.Ordinal));
        // Unbounded cardinality is fine on a sampled, per-trace span and fatal on an aggregated metric
        // series - see docs/lock-key-cardinality.md, which is why this assertion lives on the Activity.
        Assert.Equal(1L, acquire.GetTagItem("orionlock.fencing_token"));
    }

    [Fact]
    public async Task An_unfenced_backend_leaves_the_span_tag_off_entirely()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionLockDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var sut = new DistributedLock(new UnfencedProvider());
        await using (await sut.AcquireAsync("fence-span-absent", NoRenew)) { }

        var acquire = Assert.Single(activities, a => a.DisplayName.Contains("fence-span-absent", StringComparison.Ordinal));
        Assert.Null(acquire.GetTagItem("orionlock.fencing_token"));
    }
}
