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
    public async Task Fencing_is_off_by_default_and_leaves_no_counter_key_behind()
    {
        var p = new RedisLockProvider(mux, new RedisLockOptions());
        var k = NewKey();

        var taken = await p.TryAcquireFencedAsync(k, "owner-1", TimeSpan.FromSeconds(30), default);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
        // The opt-in exists because the counter key is permanent. A default-on feature would have
        // started growing every existing consumer's keyspace on upgrade.
        Assert.False(await mux.GetDatabase().KeyExistsAsync($"{{orionlock:{k}}}:fence"));
    }
}
