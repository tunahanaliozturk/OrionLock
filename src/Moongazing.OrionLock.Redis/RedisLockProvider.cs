using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Redis-backed <see cref="IDistributedLockProvider"/>. Acquire is <c>SET key token NX PX</c>;
/// renew and release are owner-checked Lua scripts (compare-and-extend, compare-and-delete).
/// </summary>
/// <remarks>
/// Lease durations are normalized through <see cref="RedisLease"/>, so a positive sub-millisecond lease
/// can never reach Redis as <c>PX 0</c> - which is not a very short lease but a DELETE.
/// </remarks>
[BackendName("redis")]
public sealed class RedisLockProvider : IDistributedLockProvider
{
    private const string RenewScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) else return 0 end";

    private const string ReleaseScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    // Acquire and mint the fencing token as ONE atomic step. Splitting them into two round trips would
    // let the process die between them and hand the next holder a token no higher than ours, or let two
    // acquirers interleave their INCRs and read each other's number. KEYS[2] is the counter; it is never
    // given a TTL and never deleted, because a counter that restarted would repeat a token that some
    // resource has already accepted. INCR on a missing key starts at 1, so 0 can only ever mean "lost
    // the race" and needs no separate nil reply to distinguish it.
    //
    // The INCR runs BEFORE the SET on purpose. Redis does not roll back the writes a script has already
    // made when a later command raises a runtime error, so with the SET first, an INCR that failed -
    // against a counter key holding a non-integer, say - would leave the lease taken and return an error
    // to the caller, who never gets a handle and so never releases it: a lock held by nobody until it
    // expires. Ordered this way the error costs a counter value and nothing else. The leading EXISTS
    // keeps the common contended case to one command and stops a long polling wait from burning a token
    // per attempt; gaps are harmless either way, since a fencing token must increase, not be dense.
    private const string FencedAcquireScript =
        "if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end "
        + "local token = redis.call('INCR', KEYS[2]) "
        + "if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then return token end "
        + "return 0";

    private readonly IConnectionMultiplexer multiplexer;
    private readonly RedisLockOptions options;

    /// <summary>
    /// The shortest wait a parked waiter can actually take. <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    /// truncates its timeout to whole milliseconds, so anything below this sleeps for nothing.
    /// </summary>
    internal static readonly TimeSpan ShortestSleep = TimeSpan.FromMilliseconds(1);

    /// <summary>Creates the provider over an existing Redis connection.</summary>
    public RedisLockProvider(IConnectionMultiplexer multiplexer, RedisLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);
        this.multiplexer = multiplexer;
        this.options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Redis <c>PX</c> / <c>PEXPIRE</c> take whole milliseconds, so <see cref="RedisLease"/> rounds a
    /// lease UP to the next one - a request of a single tick really is a 1 ms TTL. Without this override
    /// the interface default reported the requested duration back verbatim, so the property that
    /// promises the lease the backend actually honours was reporting the one it does not.
    /// </remarks>
    public TimeSpan EffectiveLeaseDuration(TimeSpan requested)
        // A non-positive lease is refused by the core before any hold exists; return it unchanged rather
        // than throwing, because this is a query about a lease and not an attempt to take one.
        => requested <= TimeSpan.Zero
            ? requested
            : TimeSpan.FromMilliseconds(RedisLease.ToMilliseconds(requested));

    private IDatabase Db => multiplexer.GetDatabase(options.Database);

    private RedisKey Key(string key) => options.KeyPrefix + key;

    // Counter keys live under their own reserved prefix, and a lock key that would land in it is refused
    // - see RedisFencingKeys for why decorating the lock key was not enough.
    private RedisKey FenceKey(string key) => RedisFencingKeys.CounterKey(options.KeyPrefix + key);

    /// <summary>The per-key channel a release publishes to and a waiter subscribes to.</summary>
    internal RedisChannel ReleaseChannel(string key)
        => RedisChannel.Literal(options.KeyPrefix + key + options.ReleaseChannelSuffix);

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => (await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false)).Acquired;

    /// <inheritdoc />
    /// <remarks>
    /// Reports a token only when <see cref="RedisLockOptions.FencingTokens"/> is on; see that property
    /// for why minting one is not free and therefore not the default.
    /// </remarks>
    public async Task<LockAcquisition> TryAcquireFencedAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        // StackExchange.Redis takes no CancellationToken on these calls, so the token is observed here
        // rather than silently ignored: a caller that already gave up does not go on to take the lock.
        cancellationToken.ThrowIfCancellationRequested();

        var expiryMs = RedisLease.ToMilliseconds(leaseDuration);

        if (!options.FencingTokens)
        {
            // No counter keys exist on this path, so there is no reserved namespace to police and a key
            // that looks like one is just a key.
            // keepTtl is spelled out so this binds to the current StringSetAsync overload rather than the
            // legacy one the compiler would otherwise pick.
            var taken = await Db.StringSetAsync(
                Key(key), ownerToken, TimeSpan.FromMilliseconds(expiryMs), keepTtl: false, when: When.NotExists)
                .ConfigureAwait(false);
            return taken ? LockAcquisition.Unfenced : LockAcquisition.NotAcquired;
        }

        RedisFencingKeys.ThrowIfReserved(options.KeyPrefix + key, nameof(key));

        var result = await Db.ScriptEvaluateAsync(
            FencedAcquireScript,
            new[] { Key(key), FenceKey(key) },
            new RedisValue[] { ownerToken, expiryMs }).ConfigureAwait(false);

        var token = (long)result;
        return token > 0 ? LockAcquisition.Fenced(token) : LockAcquisition.NotAcquired;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await Db.ScriptEvaluateAsync(
            RenewScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken, RedisLease.ToMilliseconds(leaseDuration) }).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        // Deliberately NOT cancellation-checked: dropping a release because the caller's token was
        // cancelled would strand the lock until its lease expires.

        var result = await Db.ScriptEvaluateAsync(
            ReleaseScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken }).ConfigureAwait(false);

        if (!options.UseReleaseNotifications || (long)result != 1)
        {
            // Nothing was deleted - the lock had already lapsed or belonged to someone else - so
            // there is no hand-off to announce.
            return;
        }

        // Fire-and-forget: the PUBLISH rides along in the same pipeline, so waking every waiter on
        // this key costs the releasing caller no extra round trip. A release must never fail
        // because a notification could not be delivered, which is also why nothing is awaited here.
        await multiplexer.GetSubscriber()
            .PublishAsync(ReleaseChannel(key), ownerToken, CommandFlags.FireAndForget)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes to the key's release channel and parks until the holder publishes, the holder's
    /// TTL lapses, or the budget runs out - instead of issuing a <c>SET NX</c> every retry interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subscription is taken BEFORE the attempt, so a release landing between the refusal and
    /// the subscribe cannot be slept through. The signal is a counting semaphore rather than a
    /// completion source, so a release that arrives while the waiter is between iterations is still
    /// there when it looks.
    /// </para>
    /// <para>
    /// The wait is additionally bounded by the holder's remaining TTL, because a lease that expires
    /// publishes nothing: the holder crashed, or released through a client that does not know about
    /// this channel. That costs one <c>PTTL</c> per wait - not one per retry interval.
    /// </para>
    /// <para>
    /// A cancelled or timed-out waiter unsubscribes in a <c>finally</c>, so no subscription is left
    /// parked on the multiplexer.
    /// </para>
    /// <para>
    /// The budget is treated as spent once less than <see cref="ShortestSleep"/> of it is left,
    /// because that is the shortest wait the park below can actually take.
    /// </para>
    /// </remarks>
    public async Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        if (!options.UseReleaseNotifications)
        {
            // Pub/sub turned off: behave exactly as every release before v2.1 did. The shared poll
            // loop attempts the FENCED overload, so a waiter that wins here still gets its token.
            return await DistributedLockProviderExtensions.PollUntilAcquiredAsync(
                this, key, ownerToken, leaseDuration, maxWait, waitPolicy.ToPollOptions(), cancellationToken)
                .ConfigureAwait(false);
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var infinite = maxWait == Timeout.InfiniteTimeSpan;
        var pollFloor = waitPolicy.ToPollOptions().InitialDelay;
        var missingTtlAlreadyRetried = false;

        using var released = new SemaphoreSlim(0, 1);
        var channel = ReleaseChannel(key);
        var subscriber = multiplexer.GetSubscriber();

        // The handler overload rather than a ChannelMessageQueue: the handler is all this needs,
        // and it is a surface a test can substitute, so the parks-rather-than-spins fact below is
        // provable without a Redis server anywhere near it.
        void OnReleased(RedisChannel _, RedisValue __)
        {
            try
            {
                released.Release();
            }
            catch (SemaphoreFullException)
            {
                // A signal is already pending; one wake is all a waiter needs.
            }
            catch (ObjectDisposedException)
            {
                // The waiter has already given up and torn down its semaphore.
            }
        }

        // Subscribe BEFORE the first attempt. The other order loses a release that lands between
        // the refusal and the subscribe, and the waiter then sleeps through its chance.
        await subscriber.SubscribeAsync(channel, OnReleased).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The FENCED attempt: a lock taken by waiting carries the same token a lock taken
                // on the first attempt would have, so fencing does not go dark under contention.
                var acquisition = await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken)
                    .ConfigureAwait(false);
                if (acquisition.Acquired)
                {
                    return acquisition;
                }

                var remaining = maxWait - elapsed.Elapsed;
                // The floor is one millisecond, not zero. SemaphoreSlim.WaitAsync truncates its
                // timeout to whole milliseconds, so a sub-millisecond sliver of budget is a wait
                // for nothing: the call returns at once and sends the loop straight back to the
                // store, over and over, until the clock finally crosses the deadline. A budget
                // that cannot be slept on any more is a budget that is over.
                if (!infinite && remaining < ShortestSleep)
                {
                    return LockAcquisition.NotAcquired;
                }

                var wait = infinite ? Timeout.InfiniteTimeSpan : remaining;
                var ttl = await Db.KeyTimeToLiveAsync(Key(key)).ConfigureAwait(false);
                if (ttl is { } holderTtl)
                {
                    missingTtlAlreadyRetried = false;
                    // One millisecond past the expiry, so the retry sees a lapsed lease rather than
                    // an expiring one.
                    var untilExpiry = holderTtl + TimeSpan.FromMilliseconds(1);
                    if (infinite || untilExpiry < wait)
                    {
                        wait = untilExpiry;
                    }
                }
                else if (!missingTtlAlreadyRetried)
                {
                    // No TTL came back. Overwhelmingly this means the key VANISHED between the
                    // failed attempt and this read - the holder's lease lapsed - so the lock is free
                    // right now and nothing will ever be published for it, because a TTL expiry
                    // publishes nothing. Parking here is how a waiter sleeps through an already-free
                    // lock and reports a timeout, so retry at once. This was previously done only
                    // for an infinite wait, which is exactly backwards: the finite waiter is the one
                    // with a budget to burn.
                    missingTtlAlreadyRetried = true;
                    continue;
                }
                else
                {
                    // The retry also found no TTL, so this is the other thing a null means:
                    // StackExchange.Redis reports "key has no expiry" and "key does not exist"
                    // identically, and a key under our prefix with no expiry is not ours to wait
                    // for. Fall back to the caller's poll floor rather than spinning on it.
                    wait = infinite || pollFloor < wait ? pollFloor : wait;
                }

                await released.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel, OnReleased).ConfigureAwait(false);
        }
    }
}
