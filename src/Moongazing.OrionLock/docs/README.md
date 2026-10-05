# OrionLock

Distributed locking for .NET: a backend-agnostic `IDistributedLock` with blocking and single-attempt acquire, reentrancy, reader-writer locks, fencing tokens and background lease renewal.

![OrionLock acquire, renew and release: one acquire attempt, a backend wait for the rest of WaitTimeout or LockAcquisitionTimeoutException, a watchdog that renews every LeaseDuration / 3 and cancels LostToken when the lease is lost, and an owner-checked release on dispose](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/acquire-release.png)

## Install

    dotnet add package OrionLock
    dotnet add package OrionLock.Redis

The core holds no backend. Add exactly one: `OrionLock.Redis`, `OrionLock.Postgres`, `OrionLock.SqlServer`, `OrionLock.EntityFrameworkCore`, or `OrionLock.Testing` for tests.

## Quick start

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Redis;

services.AddOrionLock().UseRedis("localhost:6379");

// later, from DI
var locker = serviceProvider.GetRequiredService<IDistributedLock>();

await using var handle = await locker.AcquireAsync(
    "order:42",
    new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) });

// critical section; handle.LostToken is cancelled if the lease is lost
await ProcessAsync(handle.LostToken);
```

`TryAcquireAsync(key)` makes one attempt and returns `null` if the key is held. `TryAcquireAsync(key, deadline)` waits until the deadline and returns `null` instead of throwing.

## Options (`DistributedLockOptions`)

| Option | Default | Meaning |
| --- | --- | --- |
| `LeaseDuration` | 30 s | Lease length; the watchdog renews every `LeaseDuration / 3`. |
| `WaitTimeout` | 10 s | How long `AcquireAsync` waits before `LockAcquisitionTimeoutException`. |
| `RetryInterval` | 250 ms | Floor of the wait between attempts when the backend has to poll. |
| `RetryBackoffCeiling` | null | When set, polls sleep a random duration up to this ceiling. |
| `AutoRenew` | true | Run the renewal watchdog. |
| `RenewalFailureGracePeriod` | null (`LeaseDuration`) | How long renewals may keep throwing on a TTL backend before the lease is given up. |
| `MaxHoldDuration` | null (10 x `LeaseDuration`) | Leak backstop: an undisposed hold stops renewing and is released. |
| `UseFifoWaiterCoordinator` | false | Queue waiters in arrival order through the registered coordinator. |

Options are validated at every acquire, on your thread, with `ArgumentOutOfRangeException`.

## Lease loss and failures

- A lease lost after acquire is not an exception: `handle.IsHeld` goes false and `handle.LostToken` is cancelled. `handle.ThrowIfLost()` turns it into `LeaseLostException` where you choose.
- An acquire raises only `ArgumentException` (illegal key), `ArgumentOutOfRangeException` (bad option or a lease below the backend's floor), `LockAcquisitionTimeoutException`, `OrionLockBackendException` (driver errors, with the original as `InnerException`) or `OperationCanceledException`.
- A lock key is one opaque name: `/`, `.`, `..`, control characters and keys over `LockKey.MaxLength` (200) are rejected. Use the backend's `KeyPrefix` / `RootPath` for hierarchy.
- `handle.FencingToken` is a strictly increasing number on the backends that can mint one, and `null` on the rest.

## Trimming and Native AOT

The core is trimmable and Native AOT compatible (`IsTrimmable`, `IsAotCompatible`). Its telemetry uses `ActivitySource` and `Meter` named `Moongazing.OrionLock`, with `orion.lock.*` instruments.

## Related packages

- `OrionLock.Redis` - `SET NX PX` lease with Lua renew/release; reader-writer lock and fencing tokens opt-in.
- `OrionLock.Postgres` - session-scoped `pg_advisory_lock`; reader-writer lock over a lease table.
- `OrionLock.SqlServer` - session-scoped `sp_getapplock`.
- `OrionLock.EntityFrameworkCore` - lock table on any EF Core relational provider, with fencing tokens.
- `OrionLock.Testing` - in-memory backend for tests.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
