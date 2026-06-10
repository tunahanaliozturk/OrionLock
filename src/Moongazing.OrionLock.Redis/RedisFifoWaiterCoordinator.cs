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

    // Sub-millisecond tiebreaker: when two processes ZADD inside the same epoch ms, the
    // pair (ms, monotonic-sequence) produces a unique score that preserves arrival order.
    // The sequence counter is process-local and wraps; equal-score collisions are still
    // possible across two processes but the per-process burst case (which is what triggers
    // the unfair-by-Guid-ordering hazard the most) is fixed.
    private static long sequenceCounter;

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
        cancellationToken.ThrowIfCancellationRequested();

        var waiterId = Guid.NewGuid().ToString("N");
        var redisKey = Key(key);

        await PruneStaleAsync(redisKey).ConfigureAwait(false);

        // Score = epoch-ms shifted left + per-process monotonic sequence. Within a single
        // process this preserves arrival order even when many ZADD calls land in the same
        // millisecond. Across processes the millisecond component dominates so two waiters
        // arriving 1+ ms apart still order by arrival; same-ms cross-process ties fall back
        // to member (random Guid) but are exceedingly rare.
        var scoreNow = ComputeScore();
        await Db.SortedSetAddAsync(redisKey, waiterId, scoreNow).ConfigureAwait(false);

        // From here on the waiter id is in Redis - ANY exit path must ZREM it, otherwise
        // we strand the slot until the WaiterTtl prune sweep catches it.
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var head = await Db.SortedSetRangeByRankAsync(redisKey, 0, 0).ConfigureAwait(false);
                if (head.Length > 0 && head[0] == waiterId)
                {
                    return new RedisFifoWaiterTicket(key, waiterId);
                }

                await Task.Delay(options.PollInterval, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Covers cancellation (pre-loop, mid-loop, during delay) AND any transient Redis
            // failure that escapes the loop. ZREM is idempotent so a duplicate Leave is
            // harmless. Use the unbounded operation token so cleanup still happens even when
            // the caller's token is cancelled.
            await Db.SortedSetRemoveAsync(redisKey, waiterId).ConfigureAwait(false);
            throw;
        }
    }

    private long ComputeScore()
    {
        var ms = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var seq = Interlocked.Increment(ref sequenceCounter) & 0xFFFF; // 16-bit wrap
        return (ms << 16) | seq;
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
        var cutoffMs = clock.GetUtcNow().ToUnixTimeMilliseconds() - (long)options.WaiterTtl.TotalMilliseconds;
        if (cutoffMs <= 0)
        {
            return;
        }
        // Scores are packed (ms << 16) | sequence. Shift the millisecond cutoff into the
        // same encoding so RemoveRangeByScore compares like-for-like; without this the raw
        // ms cutoff (e.g. 850) is smaller than every packed score (>= 65,536,000) and
        // pruning would never remove anything.
        var cutoffScore = cutoffMs << 16;
        await Db.SortedSetRemoveRangeByScoreAsync(redisKey, double.NegativeInfinity, cutoffScore).ConfigureAwait(false);
    }

    private sealed record RedisFifoWaiterTicket(string Key, string WaiterId) : IFifoWaiterTicket;
}
