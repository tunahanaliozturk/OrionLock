# OrionLock v0.1.0 — Distributed Locking

**Date:** 2026-05-21
**Status:** Approved (design); pending implementation plan
**Solution:** `Moongazing.OrionLock`
**Primary package:** `OrionLock`
**Repository:** new standalone repo (`Desktop/OrionLock/`), sibling to `OrionGuard`, `OrionAudit`, `OrionKey`

## 1. Goal

Ship a standalone .NET distributed-locking library: acquire a named lock across processes and machines, run a critical section, release. OrionLock provides a backend-agnostic `IDistributedLock` abstraction with two production backends (Redis and an EF Core lock table), plus the value-adds that raw locks lack — blocking acquire with retry, reentrancy, and background lease auto-renewal that makes lease loss observable rather than silent.

```csharp
await using var handle = await locker.AcquireAsync($"order:{id}", TimeSpan.FromSeconds(30));
// critical section — handle.LostToken trips if the lease is lost mid-section
```

## 2. Position in the Orion family

OrionLock follows the Orion family quality bar:

- **Standalone** — independent repo, independent NuGet packages, no `PackageReference` on any other Orion library.
- **Focused** — distributed locking only.
- **Modern .NET** — multi-target `net8.0;net9.0;net10.0`, `TreatWarningsAsErrors=true`, AOT-aware.
- **Production-grade** — comprehensive tests, benchmarks, documented failure modes, OpenTelemetry-aware.

### 2.1 Relationship to OrionGuard

OrionGuard v6.4.0 ships its own minimal `IDistributedLock` inside `OrionGuard.EntityFrameworkCore` — a non-blocking, try-only lease used internally by the outbox dispatcher. OrionLock does **not** supersede or graduate that type:

- Unlike OrionKey's `[StronglyTypedId]` (an attribute the consumer applies, with no runtime coupling), `IDistributedLock` is an interface OrionGuard's dispatcher *consumes at runtime*. For OrionGuard to use OrionLock's interface it would need a `PackageReference` on OrionLock, violating the Orion family no-coupling rule.
- Therefore OrionLock is **fully independent**. OrionGuard keeps its own minimal `IDistributedLock`; OrionLock ships a richer, standalone one. They are not deprecated against each other.
- A consumer who wants OrionGuard's outbox to use an OrionLock backend writes a ~15-line adapter implementing OrionGuard's `IDistributedLock` over an OrionLock `IDistributedLock`. A future `Orion.Bridge.Guard-Lock` package may ship that glue. This is downstream and **out of scope for OrionLock v0.1.0** (see §13).

### Non-goals (out of scope for v0.1.0)

- No deadlock detection (no wait-for-graph). Lease expiry is the distributed-systems answer to a crashed/stuck holder; a separate detector is over-engineering and is not what comparable libraries (RedLock.net, medallion DistributedLock) do.
- No multi-master RedLock algorithm — v0.1.0 ships the single-Redis-instance lock (the 99% case, correctly implemented). Multi-master RedLock is a post-0.1 enhancement.
- No provider-native DB locks (`sp_getapplock`, Postgres advisory locks) — v0.1.0 ships one provider-agnostic EF Core lock-table backend.
- No OrionGuard modification, no bridge package.
- No fairness / FIFO queuing of waiters — blocking acquire is retry-with-interval, not a queue.

## 3. Solution & package layout

```text
Moongazing.OrionLock.sln
├── src/
│   ├── Moongazing.OrionLock                     -> NuGet: OrionLock
│   ├── Moongazing.OrionLock.Redis               -> NuGet: OrionLock.Redis
│   ├── Moongazing.OrionLock.EntityFrameworkCore -> NuGet: OrionLock.EntityFrameworkCore
│   └── Moongazing.OrionLock.Testing             -> NuGet: OrionLock.Testing
├── tests/
│   ├── Moongazing.OrionLock.Tests
│   ├── Moongazing.OrionLock.Redis.Tests
│   ├── Moongazing.OrionLock.EntityFrameworkCore.Tests
│   └── Moongazing.OrionLock.Testing.Tests
├── bench/   Moongazing.OrionLock.Benchmarks
├── sample/  Moongazing.OrionLock.Sample
├── Directory.Build.props
├── README.md
├── CHANGELOG.md
└── docs/
```

### 3.1 Why four packages

- `OrionLock` — the abstraction (`IDistributedLock`, `IDistributedLockHandle`, options, exceptions) **and** the backend-agnostic value-adds (reentrancy decorator, lease auto-renewal watchdog, blocking-acquire retry loop, DI). No backend dependency.
- `OrionLock.Redis` — `StackExchange.Redis`-backed primitive. A consumer who wants only Redis does not drag in EF Core.
- `OrionLock.EntityFrameworkCore` — `Microsoft.EntityFrameworkCore`-backed primitive. A consumer who wants only the DB lock does not drag in `StackExchange.Redis`.
- `OrionLock.Testing` — an in-memory `IDistributedLock` for unit tests, no Redis/DB required.

**Design split:** the core package owns the *value-add* (reentrancy, renewal, retry); backend packages own only the *raw primitive* (atomic acquire / release / renew of one lease). This keeps each backend small and uniform.

## 4. Core abstraction (`OrionLock`)

```csharp
namespace Moongazing.OrionLock;

public interface IDistributedLock
{
    /// <summary>
    /// Acquires the lock, waiting up to <see cref="DistributedLockOptions.WaitTimeout"/>.
    /// Throws <see cref="LockAcquisitionTimeoutException"/> if it cannot be acquired in time.
    /// </summary>
    Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to acquire the lock without waiting. Returns <see langword="null"/> immediately
    /// if another holder owns it.
    /// </summary>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);
}

public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>The lock key this handle holds.</summary>
    string Key { get; }

    /// <summary>
    /// True while the lease is held. Flips to false when the lease is released or lost
    /// (a renewal failed, or another holder took over after expiry).
    /// </summary>
    bool IsHeld { get; }

    /// <summary>
    /// A token that is cancelled if the lease is lost while the handle is alive. Critical
    /// sections observe it to abort safely instead of running without the lock.
    /// </summary>
    CancellationToken LostToken { get; }
}

public sealed class DistributedLockOptions
{
    /// <summary>How long the lease is valid before it expires. Default 30 seconds.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a blocking <see cref="IDistributedLock.AcquireAsync"/> waits. Default 10 seconds.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay between acquisition attempts inside a blocking acquire. Default 250 ms.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// When true, a background watchdog re-extends the lease while the handle is alive.
    /// Default true.
    /// </summary>
    public bool AutoRenew { get; set; } = true;
}
```

A convenience overload `AcquireAsync(string key, TimeSpan leaseDuration, CancellationToken)` constructs `DistributedLockOptions` with the given `LeaseDuration` and defaults for the rest.

### 4.1 Exceptions

- `LockAcquisitionTimeoutException` — a blocking `AcquireAsync` exceeded `WaitTimeout`. Carries the `Key` and the elapsed wait.
- `LeaseLostException` — thrown by `Renew`/`Release` paths that require a held lease when the lease is no longer owned. Application code normally observes `IsHeld` / `LostToken` rather than catching this.

## 5. Backend primitive (`IDistributedLockProvider`)

Backend packages do not implement `IDistributedLock` directly. They implement a smaller primitive; the core `OrionLock` package wraps it with reentrancy, renewal, and the blocking-retry loop.

```csharp
namespace Moongazing.OrionLock.Providers;

/// <summary>
/// The raw, single-attempt lock primitive a backend implements. The core OrionLock package
/// composes reentrancy, lease renewal, and blocking-acquire retry on top of this.
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>
    /// Tries once, without waiting, to acquire <paramref name="key"/> for <paramref name="ownerToken"/>.
    /// Returns true on success.
    /// </summary>
    Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Extends the lease if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Releases the lock if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken);
}
```

`ownerToken` is a per-acquisition GUID string. Renew and release are owner-checked (compare-and-act) so a holder whose lease already expired cannot renew or clobber a fresh holder.

## 6. Core composition (`OrionLock`)

The core package's `DistributedLock : IDistributedLock` wraps an `IDistributedLockProvider`:

### 6.1 Blocking acquire

`AcquireAsync` loops: call `provider.TryAcquireAsync`; on failure wait `RetryInterval` and retry; stop when acquired (return the handle) or when `WaitTimeout` elapses (throw `LockAcquisitionTimeoutException`) or `cancellationToken` is cancelled (`OperationCanceledException`). `TryAcquireAsync` is a single provider attempt with no loop.

### 6.2 Lease auto-renewal watchdog

On a successful acquire with `AutoRenew = true`, the handle starts a background task that calls `provider.TryRenewAsync` every `LeaseDuration / 3`. The `/3` interval gives two renewal attempts before expiry — resilient to a single transient failure.

If a renewal returns false or throws (lease lost — backend blip, or another holder took over after a missed window), the watchdog:

1. sets `IsHeld = false`,
2. cancels `LostToken`,
3. stops renewing.

The watchdog never silently keeps a "held" handle alive on a lost lease — lease loss is **observable**. A critical section is expected to pass `handle.LostToken` into its own cancellable work, or check `handle.IsHeld` at safe points, and abort rather than continue unprotected. This is the core value-add over a raw lock.

Disposing the handle stops the watchdog and calls `provider.ReleaseAsync` (best-effort; a no-op if the lease was already lost).

### 6.3 Reentrancy

The core keeps a process-local registry keyed by `(key, ownerScope)`. When the same owner scope acquires a key it already holds, the registry returns a counted nested handle without touching the backend. Disposing a nested handle decrements the count; only the outermost dispose releases the backend lock and stops the watchdog.

`ownerScope` identity: per `IDistributedLock` instance (a singleton in DI), reentrancy is process-wide for that instance — the common case where one service re-enters its own locked section. Reentrancy is **not** cross-process (that would defeat distributed locking); it only collapses redundant same-process re-acquisition.

### 6.4 DI

```csharp
services.AddOrionLock();   // registers the core DistributedLock over the configured provider
```

`AddOrionLock` returns an `OrionLockBuilder`. A backend package adds an extension on that builder:

```csharp
services.AddOrionLock().UseRedis("localhost:6379");          // OrionLock.Redis
services.AddOrionLock().UseEntityFrameworkCore<AppDbContext>(); // OrionLock.EntityFrameworkCore
```

Exactly one backend must be registered; registering none throws a clear error at first acquire.

## 7. `OrionLock.Redis`

`RedisLockProvider : IDistributedLockProvider`, backed by `StackExchange.Redis` `IConnectionMultiplexer`.

- **Acquire:** `SET key ownerToken NX PX <leaseMs>` — atomic create-if-absent with expiry.
- **Renew:** a Lua script — `if redis.call('GET', key) == ownerToken then return redis.call('PEXPIRE', key, leaseMs) else return 0 end` — compare-and-extend.
- **Release:** a Lua script — `if redis.call('GET', key) == ownerToken then return redis.call('DEL', key) else return 0 end` — compare-and-delete.

Single Redis endpoint (or a single logical endpoint behind Sentinel/Cluster as `StackExchange.Redis` presents it). The multi-master RedLock algorithm is explicitly deferred (§2 non-goals). Keys are namespaced with a configurable prefix (default `orionlock:`).

## 8. `OrionLock.EntityFrameworkCore`

`EfCoreLockProvider : IDistributedLockProvider`, backed by the consumer's `DbContext`.

- **Entity** `OrionLockRow` mapped to table `OrionLock_Locks`: `Key` (PK, max 200), `OwnerToken` (`string?`), `ExpiresOnUtc` (`DateTime`).
- **Acquire:** an atomic `UPDATE ... SET OwnerToken=@t, ExpiresOnUtc=@exp WHERE Key=@k AND (OwnerToken IS NULL OR ExpiresOnUtc <= @now)`, then an `INSERT ... WHERE NOT EXISTS` for the first-ever use of a key, then an owner-check `SELECT`. This is the proven pattern from OrionGuard's `SkipLockedDistributedLock`.
- **Renew / Release:** owner-checked `UPDATE` statements.
- All SQL via EF Core `ExecuteSqlInterpolatedAsync` — provider-agnostic (PostgreSQL, SQL Server, MySQL, SQLite).
- `OrionLockRowEntityTypeConfiguration` is applied by the consumer in `OnModelCreating`; the table is created by a consumer EF Core migration. A migration template ships in `docs/migrations/`.

## 9. `OrionLock.Testing`

`InMemoryDistributedLock : IDistributedLock` — a process-local implementation backed by a concurrent dictionary of leases with real expiry semantics. It lets a unit test exercise locking logic (including lease expiry and the `LostToken`) without a Redis server or a database. It is a complete `IDistributedLock`, not a provider — tests that need the real reentrancy/renewal composition use the core `DistributedLock` over the test provider `InMemoryLockProvider` (also shipped here).

## 10. OpenTelemetry / diagnostics

- An `ActivitySource` named `Moongazing.OrionLock`. Each `AcquireAsync` / `TryAcquireAsync` opens a span tagged with the key, the outcome (`acquired` / `timeout` / `not-acquired`), and the wait elapsed. Acquisition is genuinely span-worthy (it can block and contend), unlike a trivial primitive.
- A `Meter` named `Moongazing.OrionLock` with counters `orion.lock.acquisitions`, `orion.lock.contentions` (a try that found the lock held), `orion.lock.lease.lost`, and a histogram `orion.lock.acquire.duration`.
- Lease-loss is surfaced through `IsHeld` / `LostToken` (§6.2) and the `orion.lock.lease.lost` counter.

## 11. Versioning & repository

- Version starts at **`0.1.0`**.
- `Directory.Build.props` mirrors the family: `TargetFrameworks=net8.0;net9.0;net10.0`, `TreatWarningsAsErrors=true`, `Nullable=enable`, `LangVersion=latest`, `AnalysisLevel=latest-recommended`; non-packable test/bench/sample projects pin a single `net10.0` in their own bodies.
- New standalone git repository at `Desktop/OrionLock/`, default branch `main`.
- `RepositoryUrl` `https://github.com/tunahanaliozturk/OrionLock`.
- A GitHub Actions CI/CD pipeline mirroring OrionKey's: build-and-test matrix over .NET 8/9/10 on push and pull request; on release, packs all four packages and pushes to NuGet.org and GitHub Packages. The solution is a classic `.sln` (the .NET 8 SDK matrix leg cannot parse `.slnx`).

## 12. Testing strategy

- **`Moongazing.OrionLock.Tests`** — core composition: blocking-acquire retry honours `WaitTimeout` and `RetryInterval`; `TryAcquireAsync` does not loop; reentrancy counting (nested acquire/dispose); the auto-renewal watchdog renews on schedule, and on renewal failure flips `IsHeld` and trips `LostToken`; disposing stops the watchdog and releases. These run against `InMemoryLockProvider`.
- **`Moongazing.OrionLock.Redis.Tests`** — `RedisLockProvider` against a Redis instance: acquire/renew/release, owner-checked renew and release (a stale owner cannot clobber), lease expiry. Uses `Testcontainers` for Redis, or skips with a clear message when no Redis is available — the CI provides one.
- **`Moongazing.OrionLock.EntityFrameworkCore.Tests`** — `EfCoreLockProvider` against SQLite in-memory: acquire on a free/expired row, owner-checked renew/release, expired-lease takeover, a parallel-acquire test where exactly one of N callers wins.
- **`Moongazing.OrionLock.Testing.Tests`** — `InMemoryDistributedLock` honours leases, expiry, and `LostToken`.
- **`Moongazing.OrionLock.Benchmarks`** — uncontended acquire/release throughput per backend.

Coverage bar: every public type has happy-path and negative-path tests; every backend has an owner-check (stale-holder) test and a concurrency test.

## 13. Documentation deliverables

- `README.md` — quick start, the `AcquireAsync` / `TryAcquireAsync` examples, backend selection, the family "More from the Orion family" section.
- `CHANGELOG.md` — `[0.1.0]` initial release.
- `docs/lease-and-renewal.md` — how lease duration, auto-renewal, and `LostToken` interact; guidance on choosing `LeaseDuration` and on writing a critical section that observes `LostToken`.
- `docs/migrations/orionlock-locks-table.md` — EF Core migration template for `OrionLock_Locks` across the four providers.
- Per-package READMEs packed into each NuGet.

## 14. Downstream (not in this spec)

- An `Orion.Bridge.Guard-Lock` package — a thin adapter implementing OrionGuard's `IDistributedLock` over an OrionLock `IDistributedLock` — so OrionGuard's outbox dispatcher can run on a Redis-backed OrionLock. Tracked here for context; it is a separate package with its own spec, not part of OrionLock v0.1.0.
- Multi-master RedLock, provider-native DB locks (`sp_getapplock`, advisory locks), and FIFO fairness are post-0.1 enhancements.

## 15. Out-of-scope confirmations

- No deadlock detection.
- No multi-master RedLock in v0.1.0.
- No provider-native DB locking primitives in v0.1.0.
- No OrionGuard code change, no bridge package in this repository.
- No FIFO / fair queuing of waiters.
- No cross-process reentrancy (reentrancy collapses same-process re-acquisition only).
