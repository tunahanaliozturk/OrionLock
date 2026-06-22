# OrionLock.Redis

Redis backend for [OrionLock](https://www.nuget.org/packages/OrionLock). `SET NX PX` acquire with owner-checked Lua compare-and-extend / compare-and-delete.

```csharp
services.AddOrionLock().UseRedis("localhost:6379");
```

As of v0.4.2 this package also ships the distributed reader-writer (shared/exclusive) lock. `UseRedisSharedExclusive()` registers `ISharedExclusiveLock` over Redis, additive to `UseRedis()`:

```csharp
services.AddOrionLock()
    .UseRedis("localhost:6379")
    .UseRedisSharedExclusive();
```

It keeps a Lua-scripted writer marker, a per-reader sorted set scored by lease expiry (so one reader's expiry never frees another's), and a lease-bounded pending-writer marker that holds off new readers so a waiting writer is not starved. All lease math uses the Redis server clock, and renew/release are fencing-token checked.

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
