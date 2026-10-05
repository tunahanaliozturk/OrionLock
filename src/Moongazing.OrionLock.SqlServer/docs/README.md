# OrionLock.SqlServer

SQL Server backend for [OrionLock](https://www.nuget.org/packages/OrionLock) using the
native `sp_getapplock` application lock primitive. Session-scope lifetime: the lock is
held only while the dedicated SQL session is alive, so a crashed process releases its
locks automatically (no clock-based expiry needed).

![OrionLock packages: the app calls the OrionLock core and one backend package implements IDistributedLockProvider underneath it](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/overview.png)

## Install

    dotnet add package OrionLock.SqlServer

It plugs into the core `OrionLock` package, which it references.

## Quick start

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.SqlServer;

services.AddOrionLock()
        .UseSqlServer("Server=...;Database=app;Trusted_Connection=true;");

var locker = serviceProvider.GetRequiredService<IDistributedLock>();
await using var handle = await locker.AcquireAsync("order:42");
```

`SqlServerLockOptions` defaults: `KeyPrefix` empty, `CommandTimeout` 30 s. This backend has no reader-writer lock and no fencing token (`handle.FencingToken` is `null`).

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
application-lock queue and returns the instant the lock frees - normally one `sp_getapplock` command for
the whole wait, however long it is. The single-shot `TryAcquireAsync` still passes `@LockTimeout = 0` and is unchanged.

Three consequences worth knowing:

- **Waiters are FIFO.** SQL Server's lock manager serves its application-lock queue in arrival order, so
  a waiter can no longer be overtaken indefinitely by a luckier poller. This is the lock manager's
  behaviour, not something OrionLock imposes on top of it.
- **The command timeout is raised for the wait.** `SqlServerLockOptions.CommandTimeout` bounds the
  network round trip; the wait budget is added on top of it for the blocking call only. Left as it was,
  `Microsoft.Data.SqlClient` would abort a legitimate queued wait as though the link had hung.
- **The budget is timed here, not by the server** - which is why "normally" one command. `@LockTimeout`
  is enforced against SQL Server's own lock-wait accounting rather than wall clock, and on a contended
  host that accounting runs ahead of it: a 700 ms budget has been measured returning "not acquired"
  after 195 ms of real time. The provider therefore keeps the budget on its own monotonic clock and
  re-issues the command with what is left when a round gives up early, so `WaitTimeout` means the same
  thing under load as it does on an idle box. When the server's timer is honest it is still one command,
  and an infinite wait is always one, since it has no budget to keep and re-issuing could only cost it
  the place in the queue it already holds.

Nothing has to be configured on the server. A cancelled caller's command is cancelled and its connection
disposed, so no session is left holding a place in the queue.

One thing SqlClient does not do for you: cancelling a command that is blocked inside `sp_getapplock`
tears the command down, and the driver reports the teardown - *"A severe error occurred on the current
command"* - as a `SqlException`, not a cancellation. The provider translates that back, so a cancelled
`AcquireAsync` raises `OperationCanceledException` as the contract says. Without the translation the
`BackendFaultGuard` would wrap it as `OrionLockBackendException` and tell the caller the backend failed
for something they asked for.

## Related packages

- `OrionLock` - the core: `IDistributedLock`, options, lease watchdog.
- `OrionLock.Postgres` - the same session-scoped model on PostgreSQL.
- `OrionLock.EntityFrameworkCore` - a lock table on any EF Core relational provider, with fencing tokens.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
