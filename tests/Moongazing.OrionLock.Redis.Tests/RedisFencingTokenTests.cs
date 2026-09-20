using Moongazing.OrionLock.Redis;
using Moongazing.OrionLock.Tests.Containers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// Redis mints the token with an INCR inside the same Lua call as the SET NX PX, so the number and the
/// lock are taken together. These run against a real Redis because the atomicity is the claim: a fake
/// would only prove that the C# around the script is wired up.
/// </summary>
public sealed class RedisFencingTokenTests : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder(ContainerImages.Redis).Build();
    private IConnectionMultiplexer mux = default!;

    public async Task InitializeAsync()
        => mux = await RedisContainerStartup.StartAndConnectAsync(container).ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        await mux.DisposeAsync();
        await container.DisposeAsync();
    }

    private RedisLockProvider Fenced() => new(mux, new RedisLockOptions { FencingTokens = true });

    private static string NewKey() => "fence-" + Guid.NewGuid().ToString("N");

    [DockerFact]
    public async Task Tokens_strictly_increase_across_sequential_acquisitions_of_one_key()
    {
        var p = Fenced();
        var k = NewKey();

        var tokens = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var taken = await p.TryAcquireFencedAsync(k, $"owner-{i}", TimeSpan.FromSeconds(30), default);
            Assert.True(taken.Acquired);
            tokens.Add(Assert.NotNull(taken.FencingToken));
            await p.ReleaseAsync(k, $"owner-{i}", default);
        }

        // Release DELs the lock key but must leave the counter alone. If the counter went with it, these
        // would all read 1 and four different holders would present the same token.
        Assert.Equal([1L, 2L, 3L, 4L], tokens);
    }

    [DockerFact]
    public async Task A_lost_race_mints_nothing()
    {
        var p = Fenced();
        var k = NewKey();

        var first = await p.TryAcquireFencedAsync(k, "owner-1", TimeSpan.FromSeconds(30), default);
        var second = await p.TryAcquireFencedAsync(k, "owner-2", TimeSpan.FromSeconds(30), default);

        Assert.Equal(1L, first.FencingToken);
        Assert.False(second.Acquired);
        Assert.Null(second.FencingToken);

        // The loser must not have advanced the counter either: the next real acquirer gets 2, not 3.
        await p.ReleaseAsync(k, "owner-1", default);
        var third = await p.TryAcquireFencedAsync(k, "owner-3", TimeSpan.FromSeconds(30), default);
        Assert.Equal(2L, third.FencingToken);
    }

    [DockerFact]
    public async Task Competing_acquirers_never_receive_the_same_token()
    {
        var p = Fenced();
        var k = NewKey();
        var tokens = new List<long>();

        for (var round = 0; round < 10; round++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 10)
                .Select(i => p.TryAcquireFencedAsync(k, $"r{round}-o{i}", TimeSpan.FromSeconds(30), default)));

            var winners = results.Where(r => r.Acquired).ToArray();
            var winner = Assert.Single(winners);
            tokens.Add(Assert.NotNull(winner.FencingToken));
            await p.ReleaseAsync(k, $"r{round}-o{Array.IndexOf(results, winner)}", default);
        }

        Assert.Equal(tokens.OrderBy(t => t).Distinct().ToList(), tokens);
    }

    [DockerFact]
    public async Task A_key_that_expired_rather_than_being_released_still_advances_the_counter()
    {
        // The case fencing exists for: the holder never released, its lease simply ran out. The next
        // holder MUST outrank it, or the resource cannot tell them apart.
        var p = Fenced();
        var k = NewKey();

        var first = await p.TryAcquireFencedAsync(k, "paused-owner", TimeSpan.FromMilliseconds(200), default);
        await Task.Delay(1000);
        var second = await p.TryAcquireFencedAsync(k, "successor", TimeSpan.FromSeconds(30), default);

        Assert.True(second.Acquired);
        Assert.True(second.FencingToken > first.FencingToken);
    }

    [DockerFact]
    public async Task A_lock_key_shaped_like_a_counter_key_does_not_collide_with_one()
    {
        // The reported collision: with an empty prefix, locking `a` used to generate the counter key
        // `{a}:fence`, which is also the physical key for the logical lock `{a}:fence`. The two locks
        // then contended through one key - and when the counter key held an owner token, the script's
        // INCR failed AFTER its SET had already taken the lease, which Redis does not roll back, so the
        // lock was held by nobody until it expired.
        var p = new RedisLockProvider(mux, new RedisLockOptions { FencingTokens = true, KeyPrefix = string.Empty });
        var k = NewKey();
        var counterShaped = $"{{{k}}}:fence";

        var both = await Task.WhenAll(
            p.TryAcquireFencedAsync(k, "owner-plain", TimeSpan.FromSeconds(30), default),
            p.TryAcquireFencedAsync(counterShaped, "owner-shaped", TimeSpan.FromSeconds(30), default));

        // Two unrelated keys: both callers get their lock, and both get a token.
        Assert.True(both[0].Acquired);
        Assert.True(both[1].Acquired);
        Assert.NotNull(both[0].FencingToken);
        Assert.NotNull(both[1].FencingToken);

        // And each is really held - neither acquire half-ran and stranded a lease.
        Assert.False((await p.TryAcquireFencedAsync(k, "intruder", TimeSpan.FromSeconds(30), default)).Acquired);
        Assert.False((await p.TryAcquireFencedAsync(counterShaped, "intruder", TimeSpan.FromSeconds(30), default)).Acquired);

        await p.ReleaseAsync(k, "owner-plain", default);
        await p.ReleaseAsync(counterShaped, "owner-shaped", default);
        Assert.True((await p.TryAcquireFencedAsync(k, "next", TimeSpan.FromSeconds(30), default)).Acquired);
    }

    [DockerFact]
    public async Task The_counters_of_two_counter_shaped_keys_stay_independent()
    {
        var p = new RedisLockProvider(mux, new RedisLockOptions { FencingTokens = true, KeyPrefix = string.Empty });
        var k = NewKey();
        var counterShaped = $"{{{k}}}:fence";

        // Drive the plain key's counter up on its own. If the two shared a counter, the other key's
        // first-ever acquisition would come back as 4 rather than 1.
        for (var i = 0; i < 3; i++)
        {
            await p.TryAcquireFencedAsync(k, $"o{i}", TimeSpan.FromSeconds(30), default);
            await p.ReleaseAsync(k, $"o{i}", default);
        }

        var shaped = await p.TryAcquireFencedAsync(counterShaped, "owner-shaped", TimeSpan.FromSeconds(30), default);

        Assert.Equal(1L, shaped.FencingToken);
    }

    [DockerFact]
    public async Task Fencing_is_off_by_default_and_leaves_no_counter_key_behind()
    {
        var p = new RedisLockProvider(mux, new RedisLockOptions());
        var k = NewKey();

        var taken = await p.TryAcquireFencedAsync(k, "owner-1", TimeSpan.FromSeconds(30), default);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
        // The opt-in exists because the counter key is permanent. A default-on feature would have
        // started growing every existing consumer's keyspace on upgrade.
        Assert.False(await mux.GetDatabase().KeyExistsAsync(RedisFencingKeys.CounterKey("orionlock:" + k)));
    }
}
