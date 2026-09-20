# OrionLock.SqlServer

SQL Server backend for [OrionLock](https://www.nuget.org/packages/OrionLock) using the
native `sp_getapplock` application lock primitive. Session-scope lifetime: the lock is
held only while the dedicated SQL session is alive, so a crashed process releases its
locks automatically (no clock-based expiry needed).

```csharp
services.AddOrionLock()
        .UseSqlServer("Server=...;Database=app;Trusted_Connection=true;");
```

### Notes

- **Case-insensitive keys.** `sp_getapplock @Resource` uses the server's default
  collation; on stock installs `"Invoice:42"` and `"invoice:42"` collide. This
  differs from Redis (case-sensitive). Use `KeyPrefix` to namespace, not casing.
- **240-character key limit.** Combined `KeyPrefix + key` must be ≤ 240
  characters; longer keys throw `ArgumentException`. Hash on the caller side.
- **Connection pooling.** Leave `Microsoft.Data.SqlClient` pooling at its
  default (enabled). The provider holds each session open for the lifetime of
  the lock and only returns it to the pool *after* calling
  `sp_releaseapplock`, so pool reset is harmless.

## Lease durations

`sp_getapplock` with `@LockOwner = 'Session'` is session-scoped: the hold lives until release or session
end, and no wall clock bounds it. `LeaseDuration` therefore sets the renewal cadence and the watchdog's
grace period but does not expire anything, so `handle.EffectiveLeaseDuration` reports
`Timeout.InfiniteTimeSpan` rather than the value you asked for. That is also why a crashed process
releases its locks immediately, without waiting out a TTL.

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
