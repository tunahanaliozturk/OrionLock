# OrionLock.Redis

Redis backend for [OrionLock](https://www.nuget.org/packages/OrionLock). `SET NX PX` acquire with owner-checked Lua compare-and-extend / compare-and-delete.

```csharp
services.AddOrionLock().UseRedis("localhost:6379");
```

This package also ships the distributed reader-writer (shared/exclusive) lock. `UseRedisSharedExclusive()` registers `ISharedExclusiveLock` over Redis, additive to `UseRedis()`:

```csharp
services.AddOrionLock()
    .UseRedis("localhost:6379")
    .UseRedisSharedExclusive();
```

It keeps a Lua-scripted writer marker, a per-reader sorted set scored by lease expiry (so one reader's expiry never frees another's), and a lease-bounded pending-writer marker that holds off new readers so a waiting writer is not starved. All lease math uses the Redis server clock, and renew/release are owner-token checked.

## Fencing tokens

Off by default. Turn it on and every acquire also returns a `handle.FencingToken`: an `INCR` on a per-key
counter issued in the same Lua call as the `SET NX PX`, so the token and the lock are taken atomically and
the number strictly increases per key across processes.

```csharp
services.AddOrionLock().UseRedis("localhost:6379", o => o.FencingTokens = true);
```

It is opt-in because the counter key can never expire or be deleted — one that restarted would hand a
later holder a token an earlier one already spent — so enabling it leaves one small permanent
`orionlock-fence:{key}` key per lock key you take.

Counters live under that reserved `orionlock-fence:` prefix so no lock key can also be somebody's
counter; with fencing on, a lock key that would resolve into that namespace is rejected with an
`ArgumentException`. With the default `orionlock:` prefix the rejection can never fire. See the OrionLock
docs on fencing tokens for what to do with the number.

## Lease durations

Both providers round a lease up to whole milliseconds, so the shortest lock you can take is 1 ms. A lease
of zero or less is rejected with `ArgumentOutOfRangeException` rather than accepted: `PX 0` / `PEXPIRE 0`
does not mean "expire immediately" in Redis, it DELETES the key, which would drop the lock at its first
renewal.

## FIFO waiter fairness

`RedisFifoWaiterCoordinator` orders waiters by arrival millisecond, with a per-process sequence breaking
ties inside one millisecond. Two waiters in the same millisecond **in different processes** still fall
back to member ordering — cross-process sub-millisecond ordering is not a guarantee this coordinator
makes.

Requires the `OrionLock` package. See <https://github.com/tunahanaliozturk/OrionLock>.
