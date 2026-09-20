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
/// (<see cref="RedisFifoWaiterOptions.KeyPrefix"/>). Member = unique waiter id (Guid), score = the
/// arrival millisecond packed with a per-process arrival sequence. The packing is chosen so the whole
/// score stays inside a double's exact-integer range; see the score constants below.
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

    // Sub-millisecond tiebreaker: when two ZADDs land inside the same epoch millisecond, the pair
    // (ms, monotonic-sequence) still produces a unique score that preserves arrival order. The sequence
    // counter is process-local and wraps; equal-score collisions remain possible across two processes,
    // but the per-process burst case - the one that actually triggers the unfair-by-Guid-ordering hazard
    // - is ordered.
    //
    // THE SCORE MUST FIT IN 53 BITS. A Redis sorted-set score is a double, which represents integers
    // exactly only up to 2^53; above that the ULP grows and neighbouring packed values quantize onto the
    // SAME score. The previous packing, (unixMs << 16) | seq, needs 57 bits at a 2020s epoch, so its ULP
    // was 16: runs of ~16 same-millisecond waiters collapsed to one score and fell back to lexicographic
    // Guid ordering - exactly the unfairness the tiebreaker was added to remove. The budget is therefore
    // spent deliberately: milliseconds are counted from a fixed 2020 epoch rather than 1970 (38 bits
    // today instead of 41), and the sequence gets 12 bits (4096 waiters per millisecond per process,
    // orders of magnitude beyond what a round-tripping ZADD can produce). That totals ~50 bits now and
    // stays under 2^53 - every score exact, no quantization - until roughly the year 2089.
    private const long ScoreEpochMs = 1_577_836_800_000; // 2020-01-01T00:00:00Z
    private const int SequenceBits = 12;
    private const long SequenceMask = (1L << SequenceBits) - 1;

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

        // v0.3.29: record the live FIFO depth this candidate joined behind (0 = it landed at the
        // head). ZRANK is 0-based, so the rank IS the count of members ahead. Cancelled / crashed
        // waiters never linger here - every exit path ZREMs and PruneStaleAsync ran above - so the
        // rank is already live-only, matching the in-process coordinator's contract. A null rank
        // (a racing prune removed us between ZADD and ZRANK) degrades to 0 rather than throwing.
        var rank = await Db.SortedSetRankAsync(redisKey, waiterId).ConfigureAwait(false);
        Diagnostics.OrionLockDiagnostics.RecordFifoQueueDepth((int)(rank ?? 0));

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
        => PackScore(clock.GetUtcNow().ToUnixTimeMilliseconds(), Interlocked.Increment(ref sequenceCounter));

    /// <summary>
    /// Packs an arrival millisecond and a monotonic sequence into one sorted-set score. Internal so the
    /// precision budget can be asserted directly, without a Redis server: a score that has quantized is
    /// invisible end-to-end until two waiters happen to tie.
    /// </summary>
    internal static long PackScore(long unixMs, long sequence)
        => ((unixMs - ScoreEpochMs) << SequenceBits) | (sequence & SequenceMask);

    /// <summary>The score every waiter that arrived at or before <paramref name="unixMs"/> sorts at or below.</summary>
    internal static long PackCutoff(long unixMs) => (unixMs - ScoreEpochMs) << SequenceBits;

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
        if (cutoffMs <= ScoreEpochMs)
        {
            return;
        }
        // The cutoff has to be expressed in the SAME packing as the scores, or RemoveRangeByScore
        // compares a raw millisecond count against packed values and prunes nothing.
        var cutoffScore = PackCutoff(cutoffMs);
        await Db.SortedSetRemoveRangeByScoreAsync(redisKey, double.NegativeInfinity, cutoffScore).ConfigureAwait(false);
    }

    private sealed record RedisFifoWaiterTicket(string Key, string WaiterId) : IFifoWaiterTicket;
}
