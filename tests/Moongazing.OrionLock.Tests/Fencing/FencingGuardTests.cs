namespace Moongazing.OrionLock.Tests.Fencing;

using Moongazing.OrionLock;
using Moongazing.OrionLock.Fencing;
using Moongazing.OrionLock.Testing;
using Xunit;

/// <summary>
/// The resource side of the pattern: whatever the lock says, the resource is the thing that has to
/// refuse the stale writer.
/// </summary>
public sealed class FencingGuardTests
{
    [Fact]
    public void A_higher_token_is_accepted_and_becomes_the_new_high_water_mark()
    {
        var guard = new FencingGuard();

        Assert.True(guard.TryAccept("orders", 1));
        Assert.True(guard.TryAccept("orders", 2));
        Assert.Equal(2, guard.HighestAccepted("orders"));
    }

    [Fact]
    public void A_repeated_token_is_rejected_because_two_acquisitions_never_share_one()
    {
        var guard = new FencingGuard();
        guard.TryAccept("orders", 7);

        Assert.False(guard.TryAccept("orders", 7));
        Assert.Equal(7, guard.HighestAccepted("orders"));
    }

    [Fact]
    public void A_lower_token_is_rejected_and_does_not_move_the_mark_backwards()
    {
        var guard = new FencingGuard();
        guard.TryAccept("orders", 9);

        Assert.False(guard.TryAccept("orders", 4));
        Assert.Equal(9, guard.HighestAccepted("orders"));
    }

    [Fact]
    public void Keys_are_tracked_independently()
    {
        var guard = new FencingGuard();
        guard.TryAccept("orders", 100);

        // Fencing is a per-key promise, so a high token on one key must not lock out a low one on
        // another - that would reject the first legitimate write to every other resource.
        Assert.True(guard.TryAccept("invoices", 1));
        Assert.Null(guard.HighestAccepted("shipments"));
    }

    [Fact]
    public void Accept_throws_so_a_stale_writer_cannot_fall_through_an_unwritten_branch()
    {
        var guard = new FencingGuard();
        guard.Accept("orders", 5);

        var ex = Assert.Throws<FencingTokenRegressedException>(() => guard.Accept("orders", 3));
        Assert.Equal("orders", ex.Key);
        Assert.Equal(3, ex.Presented);
        Assert.Equal(5, ex.HighestSeen);
    }

    [Fact]
    public async Task The_paused_holder_scenario_end_to_end()
    {
        // The exact sequence Kleppmann describes. Holder A takes the lock and gets a token. A pauses
        // long enough for its lease to lapse; B acquires and writes. A wakes up, still believing it
        // holds the lock, and writes - and the resource is what stops it.
        var provider = new InMemoryLockProvider();
        var resource = new FencingGuard();

        var a = new DistributedLock(provider);
        var handleA = await a.AcquireAsync(
            "shared-resource", new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(100), AutoRenew = false });
        var tokenA = handleA.RequireFencingToken();

        // A is stopped by a GC pause / VM migration here: no code of ours runs, the lease simply lapses.
        await Task.Delay(300);

        var b = new DistributedLock(provider);
        await using var handleB = await b.AcquireAsync(
            "shared-resource", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30), AutoRenew = false });
        resource.Accept("shared-resource", handleB.RequireFencingToken());

        // A resumes and writes. It has no way to know it was superseded - that is the whole problem.
        Assert.Throws<FencingTokenRegressedException>(() => resource.Accept("shared-resource", tokenA));

        await handleA.DisposeAsync();
    }
}
