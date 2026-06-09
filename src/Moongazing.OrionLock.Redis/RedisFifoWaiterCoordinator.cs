namespace Moongazing.OrionLock.Redis;

using Moongazing.OrionLock.Fairness;
using StackExchange.Redis;

/// <summary>
/// Redis sorted-set backed distributed <see cref="IFifoWaiterCoordinator"/>. Cross-process
/// fairness for blocking <c>AcquireAsync</c>: every waiter joins a per-key sorted set scored
/// by arrival timestamp; the coordinator polls until the caller is at the head.
/// </summary>
/// <remarks>
/// <para>
/// Storage shape: one sorted set per key under the configured prefix
/// (<see cref="RedisFifoWaiterOptions.KeyPrefix"/>). Member = unique waiter id (Guid),
/// score = arrival epoch millisecond.
/// </para>
/// <para>
/// Leave semantics: <see cref="LeaveAsync"/> issues a <c>ZREM</c>. If the caller never
/// completed (process crash), the entry stays until <see cref="RedisFifoWaiterOptions.WaiterTtl"/>
/// elapses - a periodic scan-and-prune pass removes stale entries by score (current epoch
/// minus TTL). Stale entries do NOT block forever because the prune pass runs on every
/// <see cref="EnterAsync"/> and <see cref="LeaveAsync"/> call.
/// </para>
/// <para>
/// Polling: head-position check uses <c>ZRANGE 0 0</c> with the configured
/// <see cref="RedisFifoWaiterOptions.PollInterval"/>. The polling cost is bounded
/// per-key; consumers acquiring many distinct keys should keep PollInterval at the default
/// 50 ms or higher.
/// </para>
/// </remarks>
public sealed class RedisFifoWaiterCoordinator : IFifoWaiterCoordinator
{
    private readonly IConnectionMultiplexer connection;
    private readonly RedisFifoWaiterOptions options;
    private readonly TimeProvider clock;

    /// <summary>Construct against an already-connected <see cref="IConnectionMultiplexer"/>.</summary>
    public RedisFifoWaiterCoordinator(
        IConnectionMultiplexer connection,
        RedisFifoWaiterOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        this.connection = connection;
        this.options = options ?? new RedisFifoWaiterOptions();
        this.clock = clock ?? TimeProvider.System;
    }

    private IDatabase Db => connection.GetDatabase(options.Database);
    private RedisKey Key(string key) => new($"{options.KeyPrefix}:{key}");

    /// <inheritdoc />
    public async Task<IFifoWaiterTicket> EnterAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var waiterId = Guid.NewGuid().ToString("N");
        var redisKey = Key(key);

        await PruneStaleAsync(redisKey).ConfigureAwait(false);

        var scoreNow = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await Db.SortedSetAddAsync(redisKey, waiterId, scoreNow).ConfigureAwait(false);

        // Poll for head position. Bounded by the caller's cancellation token; AcquireAsync's
        // WaitTimeout wraps this via the linked token plumbed from the lock options.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var head = await Db.SortedSetRangeByRankAsync(redisKey, 0, 0).ConfigureAwait(false);
            if (head.Length > 0 && head[0] == waiterId)
            {
                return new RedisFifoWaiterTicket(key, waiterId);
            }

            try
            {
                await Task.Delay(options.PollInterval, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // On cancellation, remove ourselves from the queue so we do not block waiters
                // behind us. ZREM is idempotent so a duplicate from LeaveAsync is harmless.
                await Db.SortedSetRemoveAsync(redisKey, waiterId).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task LeaveAsync(IFifoWaiterTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket is not RedisFifoWaiterTicket rt)
        {
            throw new InvalidOperationException(
                $"Ticket of type '{ticket.GetType()}' was not produced by this coordinator.");
        }

        var redisKey = Key(rt.Key);
        await Db.SortedSetRemoveAsync(redisKey, rt.WaiterId).ConfigureAwait(false);
        await PruneStaleAsync(redisKey).ConfigureAwait(false);
    }

    private async Task PruneStaleAsync(RedisKey redisKey)
    {
        if (options.WaiterTtl <= TimeSpan.Zero)
        {
            return;
        }
        var cutoff = clock.GetUtcNow().ToUnixTimeMilliseconds() - (long)options.WaiterTtl.TotalMilliseconds;
        if (cutoff <= 0)
        {
            return;
        }
        await Db.SortedSetRemoveRangeByScoreAsync(redisKey, double.NegativeInfinity, cutoff).ConfigureAwait(false);
    }

    private sealed record RedisFifoWaiterTicket(string Key, string WaiterId) : IFifoWaiterTicket;
}
