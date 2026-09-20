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

## Waiting is SQL Server's job now

A contended `AcquireAsync` no longer asks SQL Server again every `RetryInterval`. It passes the caller's
remaining wait budget as `sp_getapplock @LockTimeout`, so the request sits in SQL Server's own
application-lock queue and returns the instant the lock frees - one round trip for the whole wait,
however long it is. The single-shot `TryAcquireAsync` still passes `@LockTimeout = 0` and is unchanged.

Two consequences worth knowing:

- **Waiters are FIFO.** SQL Server's lock manager serves its application-lock queue in arrival order, so
  a waiter can no longer be overtaken indefinitely by a luckier poller. This is the lock manager's
  behaviour, not something OrionLock imposes on top of it.
- **The command timeout is raised for the wait.** `SqlServerLockOptions.CommandTimeout` bounds the
  network round trip; the wait budget is added on top of it for the blocking call only. Left as it was,
  `Microsoft.Data.SqlClient` would abort a legitimate queued wait as though the link had hung.

Nothing has to be configured on the server. A cancelled caller's command is cancelled and its connection
disposed, so no session is left holding a place in the queue.

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
