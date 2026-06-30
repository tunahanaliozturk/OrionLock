<p align="center">
  <img src="docs/logo.png" alt="OrionLock Logo" width="150" />
</p>

<h1 align="center">OrionLock</h1>

<p align="center">
  Distributed locking for .NET. A backend-agnostic IDistributedLock with reentrancy, shared/exclusive (reader-writer) locks, and background lease auto-renewal.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/OrionLock"><img src="https://img.shields.io/nuget/v/OrionLock?style=flat-square&color=blue" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/OrionLock"><img src="https://img.shields.io/nuget/dt/OrionLock?style=flat-square&color=green" alt="Downloads" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-MIT-yellow?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple?style=flat-square" alt="Target" />
</p>

---

## How it works

The acquire path is a single backend call with a generated lease id; the release path validates ownership before deleting the row so two processes cannot accidentally release each other's locks. Between the two, a watchdog renews the lease at one-third of `LeaseDuration` and trips `handle.LostToken` if renewal fails.

```mermaid
sequenceDiagram
    autonumber
    participant App as Application code
    participant Lock as DistributedLock
    participant WD as Renewal watchdog<br/>(per handle)
    participant BE as Backend<br/>(Redis / Postgres / SqlServer)

    App->>Lock: AcquireAsync("order:42", 30s)
    Lock->>BE: SET NX PX 30000<br/>(or pg_try_advisory_lock / sp_getapplock)
    BE-->>Lock: acquired (lease id = G)
    Lock->>WD: start (renew every 10s)
    Lock-->>App: handle (IsHeld=true, LostToken open)

    loop while held
        WD->>BE: renew if owner == G
        BE-->>WD: ok or lost
        alt renewal failed
            WD->>App: cancel LostToken
        end
    end

    App->>Lock: handle.DisposeAsync()
    Lock->>BE: delete if owner == G
    BE-->>Lock: released
```

The same pattern fits the "single-instance hosted job" recipe: a background service tries to claim a well-known key on its schedule, runs the work if it wins, and goes back to sleep if another replica got there first. Postgres advisory locks make this especially clean because the lock is auto-released on session end if the holding process crashes.

```mermaid
flowchart TD
    Start([Replica wakes on schedule]) --> Try{TryAcquireAsync<br/>'settlement:daily'}
    Try -- "null (another replica holds it)" --> Skip[Log skipped]
    Skip --> Sleep[Sleep until next tick]
    Try -- "handle acquired" --> Run[Run the job body]
    Run --> Release[Dispose handle<br/>backend releases]
    Release --> Sleep
    Sleep --> Start

    classDef skip fill:#fee2e2,stroke:#991b1b,color:#7f1d1d
    classDef run fill:#dcfce7,stroke:#166534,color:#14532d
    class Skip,Sleep skip
    class Run,Release run
```

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
- `TryAcquireAsync(key)` is a single attempt. Returns `null` immediately if the lock is held.
- `TryAcquireAsync(key, deadline)` polls until the deadline and returns `null` on expiry instead of throwing. The poll delay is clamped to the time left so it cannot overshoot by a full retry interval.

```csharp
// Single attempt: null if already held.
await using var first = await locker.TryAcquireAsync("order:42");
if (first is null)
{
    return; // someone else holds it
}

// Bounded wait: poll for up to two seconds, then give up without throwing.
await using var bounded = await locker.TryAcquireAsync("order:42", TimeSpan.FromSeconds(2));
if (bounded is null)
{
    return; // could not acquire within the deadline
}
// critical section
```

## Lease and renewal

Each acquired lock carries a lease (default 30s). A background watchdog renews the lease at `LeaseDuration / 3` while the handle is alive. If renewal fails, `handle.IsHeld` flips to false and `handle.LostToken` is cancelled — so the critical section can observe and abort safely instead of running without the lock. See [docs/lease-and-renewal.md](docs/lease-and-renewal.md).

## Reentrancy

A single `DistributedLock` instance (a DI singleton) re-acquiring a key it already holds returns a counted nested handle without touching the backend. The outermost dispose releases. Reentrancy collapses same-process re-acquisition only; it does not cross process boundaries.

## Shared / exclusive (reader-writer) locks

Added in v0.4.0. `ISharedExclusiveLock` is a reader-writer lock for a resource key: any number of `Shared` (read) holders coexist, OR exactly one `Exclusive` (write) holder owns it. Acquire, `WaitTimeout`/`RetryInterval`, lease and renewal, release, and diagnostics semantics mirror the exclusive `IDistributedLock`, and every acquire returns the same `IDistributedLockHandle`.

`UseInMemory()` from `OrionLock.Testing` registers `ISharedExclusiveLock`, so it resolves from DI like the exclusive lock:

```csharp
var rwLock = serviceProvider.GetRequiredService<ISharedExclusiveLock>();

// Many readers can hold the key at once.
await using (var read = await rwLock.AcquireSharedAsync("catalog:42"))
{
    // shared critical section - read.LostToken trips if the lease is lost
}

// A single writer excludes all readers and other writers.
await using (var write = await rwLock.AcquireExclusiveAsync("catalog:42"))
{
    // exclusive critical section
}
```

Blocking `AcquireSharedAsync` / `AcquireExclusiveAsync` wait up to `WaitTimeout` and throw `LockAcquisitionTimeoutException` on timeout. The non-blocking `TryAcquireSharedAsync` / `TryAcquireExclusiveAsync` make a single attempt and return `null` when the key is held in a conflicting mode.

`TryAcquireSharedAsync` / `TryAcquireExclusiveAsync` also take an optional deadline, the reader-writer counterpart of `TryAcquireAsync(key, deadline)`: they poll until the deadline and return `null` on expiry instead of throwing.

```csharp
await using var write = await rwLock.TryAcquireExclusiveAsync("catalog:42", TimeSpan.FromSeconds(2));
if (write is null)
{
    return; // readers (or another writer) did not drain within the deadline
}
```

The reader-writer lock runs distributed on Redis, PostgreSQL, and any EF Core relational provider. Each registration is additive to the exclusive-only registration of the same backend:

```csharp
services.AddOrionLock()
    .UseRedis("localhost:6379")        // exclusive IDistributedLock
    .UseRedisSharedExclusive();        // reader-writer ISharedExclusiveLock over Redis

// or PostgreSQL:
services.AddOrionLock()
    .UsePostgres(connectionString)
    .UsePostgresSharedExclusive(connectionString);

// or any EF Core relational provider (SQL Server, PostgreSQL, and so on):
services.AddOrionLock()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseEntityFrameworkCoreSharedExclusive<AppDbContext>();
```

Every distributed reader-writer provider keeps a writer marker, a per-reader record scored or stamped by lease expiry (so one reader's expiry never frees another's), and a lease-bounded pending-writer marker that holds off new readers so a waiting writer is not starved. The Redis provider does this with Lua scripts and a sorted set; the PostgreSQL provider with clock-leased rows serialized by `pg_advisory_xact_lock`; the EF Core provider with clock-leased rows in a serializable transaction. The remaining backends (SqlServer's `sp_getapplock`, Consul, Etcd, ZooKeeper) keep the exclusive lock only. See the runnable section in `demo/Moongazing.OrionLock.Demo`.

## Choosing a backend

All backends implement the same `IDistributedLock`, so application code never changes when you switch; only the registration does. Pick the in-memory backend for tests, then a distributed backend for production.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;

// In-memory (tests, single process) - no Redis or DB required.
services.AddOrionLock().UseInMemory();                          // OrionLock.Testing

// Redis - SET NX PX acquire, clock-leased, owner-checked Lua release.
services.AddOrionLock().UseRedis("localhost:6379");             // OrionLock.Redis

// PostgreSQL - native pg_try_advisory_lock, session-scoped, crash-safe.
services.AddOrionLock().UsePostgres(connectionString);          // OrionLock.Postgres

// Resolve and use the same way regardless of backend:
var locker = serviceProvider.GetRequiredService<IDistributedLock>();
await using var handle = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30));
// critical section
```

## Backends

- **`OrionLock.Redis`** — `SET NX PX` acquire, owner-checked Lua renew/release. Single Redis endpoint (single-instance lock; multi-master RedLock is a separate opt-in). Also ships the distributed reader-writer lock (`UseRedisSharedExclusive()`): a Lua-scripted writer marker plus a per-reader sorted set scored by lease expiry, with a lease-bounded pending-writer marker for writer fairness.
- **`OrionLock.EntityFrameworkCore`** — provider-agnostic `OrionLock_Locks` table; PostgreSQL, SQL Server, MySQL, SQLite. Also ships the provider-portable reader-writer lock (`UseEntityFrameworkCoreSharedExclusive<TDbContext>()`) over clock-leased rows in a serializable transaction. See [docs/migrations/orionlock-locks-table.md](docs/migrations/orionlock-locks-table.md).
- **`OrionLock.SqlServer`** — native `sp_getapplock` with session-scope lifetime. Crash-safe (no clock-based expiry; SQL Server releases the lock when the session ends) and faster than the EF Core lock table on SQL Server.
- **`OrionLock.Postgres`** — native `pg_try_advisory_lock` with session-scope lifetime, crash-safe with the same rationale as SqlServer. Also ships the distributed reader-writer lock (`UsePostgresSharedExclusive()`) over clock-leased rows serialized by `pg_advisory_xact_lock`.
- **`OrionLock.Testing`** — in-memory provider for tests, no Redis or DB required.

## Trimming and Native AOT

OrionLock's own surface uses no dynamic code generation; the only reflection in the core reads assembly and attribute metadata for telemetry, which is trimmer- and AOT-safe. The posture below reflects what each package's dependencies allow.

| Package | Trimmable / AOT-compatible | Notes |
| --- | --- | --- |
| `OrionLock` (core) | Yes | `IsTrimmable` and `IsAotCompatible` set; built clean with the trim and AOT analyzers. |
| `OrionLock.Testing` | Yes | In-memory provider only; `IsTrimmable` and `IsAotCompatible` set. |
| `OrionLock.Redis` | Not claimed | Depends on `StackExchange.Redis`, which is not annotated AOT-safe. |
| `OrionLock.EntityFrameworkCore` | Not claimed | EF Core model building uses dynamic code; not Native AOT compatible. |
| `OrionLock.SqlServer` | Not claimed | Depends on `Microsoft.Data.SqlClient`, which is not annotated AOT-safe. |
| `OrionLock.Postgres` | Not claimed | Depends on the `Npgsql` driver; not claimed AOT-safe at this time. |

For a Native AOT or aggressively trimmed application, reference the core and (in tests) the Testing package directly; the database and Redis backends carry their drivers' trimming posture, so build them with the trim warnings on and validate against your own configuration.

## Health checks

`Moongazing.OrionLock.HealthChecks` ships an `IHealthCheck` that probes backend reachability by acquiring and releasing a sentinel lock. Register it via `services.AddHealthChecks().AddOrionLockHealthCheck(name: "orionlock", failureStatus: HealthStatus.Degraded, tags: ["ready", "infra"])`. The probe returns `Healthy` on success, `Degraded` when the sentinel is contended within `WaitTimeout`, and `Unhealthy` when the backend throws. Useful for failing fast in container readiness probes when Redis or the database is unreachable.

## OpenTelemetry

`ActivitySource` and `Meter` named `Moongazing.OrionLock`. Each acquire opens a span tagged with the key and outcome. Counters: `orionlock.acquisitions`, `orionlock.contentions`, `orionlock.lease.lost`, `orionlock.health_check.result` (tagged by `result`). Histograms: `orionlock.acquire.duration` (end-to-end blocking-acquire time), `orionlock.acquire.latency` (single backend round-trip, tagged by `backend`), `orionlock.lease_renewal.duration` (per-renewal time, tagged by `backend`). See [docs/lock-key-cardinality.md](docs/lock-key-cardinality.md) before sending high-cardinality lock keys through the meter.

## Benchmarks

See [benchmarks.md](benchmarks.md) for the BenchmarkDotNet harness in `bench/Moongazing.OrionLock.Benchmarks`, the scenarios it covers (uncontended in-memory acquire/release as the abstraction-cost floor, with Redis and Postgres backends queued for v0.2), and the comparison baselines we report against.

## Roadmap

The current release is **1.0.0**: the public API is frozen for the 1.x line. `IDistributedLock`, `IDistributedLockHandle`, `DistributedLockOptions`, the provider primitive interfaces (`IDistributedLockProvider`, `ISharedExclusiveLockProvider`), `ISharedExclusiveLock` / `LockMode`, and the bundled backends (Redis, EF Core, SqlServer, Postgres, Testing) are stable; future changes are additions only, guarded by `Microsoft.CodeAnalysis.PublicApiAnalyzers` and per-project `PublicAPI.Shipped.txt` baselines. Forward plan in [ROADMAP.md](ROADMAP.md): fair queueing beyond opt-in FIFO and a distributed counter/sequence primitive. If something on the list matters to you, open an issue with the `roadmap` label.

## More from the Orion family

- [OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) — validation, guard clauses, DDD primitives, domain events
- [OrionKey](https://github.com/tunahanaliozturk/OrionKey) — source-generated strongly-typed IDs
- [OrionAudit](https://github.com/tunahanaliozturk/OrionAudit) — automatic EF Core change-audit trail
- [OrionPatch](https://github.com/tunahanaliozturk/OrionPatch) — transactional outbox for EF Core (enqueue inside SaveChanges, dispatch at-least-once through a pluggable sink)

### See it in a real app

[Moongazing.OrionShowcase](https://github.com/tunahanaliozturk/OrionShowcase) is a production-shaped banking sample integrating all six Orion packages end-to-end. OrionLock.Postgres backs two patterns in the showcase: sorted-key deadlock-free distributed locks in TransferMoneyHandler and single-instance gating for the DailySettlementService background job. Concrete usage:

- [src/Moongazing.OrionShowcase.Application/Accounts/Commands/TransferMoney/TransferMoneyHandler.cs](https://github.com/tunahanaliozturk/OrionShowcase/blob/main/src/Moongazing.OrionShowcase.Application/Accounts/Commands/TransferMoney/TransferMoneyHandler.cs)
- [src/Moongazing.OrionShowcase.Infrastructure/HostedServices/DailySettlementService.cs](https://github.com/tunahanaliozturk/OrionShowcase/blob/main/src/Moongazing.OrionShowcase.Infrastructure/HostedServices/DailySettlementService.cs)

## Contributing

Issues and pull requests welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md) before opening one.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
