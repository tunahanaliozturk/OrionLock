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

It keeps a Lua-scripted writer marker, a per-reader sorted set scored by lease expiry (so one reader's expiry never frees another's), and a lease-bounded pending-writer marker that holds off new readers so a waiting writer is not starved. All lease math uses the Redis server clock, and renew/release are fencing-token checked.

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
