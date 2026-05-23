<h1 align="center">OrionLock</h1>

<p align="center">
  Distributed locking for .NET. A backend-agnostic IDistributedLock with reentrancy and background lease auto-renewal.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/OrionLock"><img src="https://img.shields.io/nuget/v/OrionLock?style=flat-square&color=blue" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/OrionLock"><img src="https://img.shields.io/nuget/dt/OrionLock?style=flat-square&color=green" alt="Downloads" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-MIT-yellow?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple?style=flat-square" alt="Target" />
</p>

---

## Quick start

```bash
dotnet add package OrionLock
dotnet add package OrionLock.Redis           # or OrionLock.EntityFrameworkCore
```

```csharp
services.AddOrionLock()
        .UseRedis("localhost:6379");
```

```csharp
await using var handle = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30));
// critical section - handle.LostToken trips if the lease is lost mid-section
```

## Acquire vs TryAcquire

- `AcquireAsync` blocks up to `WaitTimeout` (default 10s), retrying every `RetryInterval` (default 250 ms). Throws `LockAcquisitionTimeoutException` if it cannot acquire.
- `TryAcquireAsync` is a single attempt. Returns `null` immediately if the lock is held.

## Lease and renewal

Each acquired lock carries a lease (default 30s). A background watchdog renews the lease at `LeaseDuration / 3` while the handle is alive. If renewal fails, `handle.IsHeld` flips to false and `handle.LostToken` is cancelled — so the critical section can observe and abort safely instead of running without the lock. See [docs/lease-and-renewal.md](docs/lease-and-renewal.md).

## Reentrancy

A single `DistributedLock` instance (a DI singleton) re-acquiring a key it already holds returns a counted nested handle without touching the backend. The outermost dispose releases. Reentrancy collapses same-process re-acquisition only; it does not cross process boundaries.

## Backends

- **`OrionLock.Redis`** — `SET NX PX` acquire, owner-checked Lua renew/release. Single Redis endpoint (single-instance lock; multi-master RedLock is post-0.1).
- **`OrionLock.EntityFrameworkCore`** — provider-agnostic `OrionLock_Locks` table; PostgreSQL, SQL Server, MySQL, SQLite. See [docs/migrations/orionlock-locks-table.md](docs/migrations/orionlock-locks-table.md).
- **`OrionLock.Testing`** — in-memory provider for tests, no Redis or DB required.

## OpenTelemetry

`ActivitySource` and `Meter` named `Moongazing.OrionLock`. Each acquire opens a span tagged with the key and outcome; counters `orionlock.acquisitions`, `orionlock.contentions`, `orionlock.lease.lost`; histogram `orionlock.acquire.duration`.

## More from the Orion family

- [OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) — validation, guard clauses, DDD primitives, domain events
- [OrionKey](https://github.com/tunahanaliozturk/OrionKey) — source-generated strongly-typed IDs
- [OrionAudit](https://github.com/tunahanaliozturk/OrionAudit) — automatic EF Core change-audit trail

## License

MIT. See [LICENSE.txt](LICENSE.txt).
