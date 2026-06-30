# OrionLock Roadmap

This document lists what is shipped, what is actively planned, and what we are deliberately
*not* building. It is a planning artifact, not a contract — dates slip, priorities reshuffle.
If an item here matters to you, open a GitHub issue so we can weigh it against everything else.

**Current release: 1.0.0.** Reader-writer (shared/exclusive) locking shipped for the in-memory
backend in 0.4.0; 0.4.1 trimmed an allocation on the acquire hot path and made the diagnostics
meter version self-deriving; 0.4.2 shipped the first *distributed* reader-writer provider, backed by
Redis; 0.5.0 added the PostgreSQL distributed reader-writer provider plus a
`TryAcquireAsync`-with-deadline ergonomics surface on the reader-writer lock; 0.6.0 added the
*provider-portable* EF Core distributed reader-writer provider (works on SQL Server, PostgreSQL, and
any other relational EF Core provider) and the matching `TryAcquireAsync`-with-deadline overload on
the exclusive lock. 1.0.0 is the stabilization milestone: it freezes the public API surface with the
`PublicApiAnalyzers` baselines, audits the family for trimming / Native AOT, and ships runnable docs,
all without changing runtime behavior. The next milestone is folding the FIFO coordinator into the
reader-writer path for fair ordering beyond the best-effort starvation marker.

## Status legend

- **Shipped** — in the named release on NuGet.
- **Planned** — committed to the named milestone; design is firm.
- **Considered** — interesting but unscheduled. Needs a concrete use case before we commit.
- **Out of scope** — explicitly declined for the 1.x line. The library stays small; some
  features belong in adjacent packages or in user code.

---

## Released

### v0.1.0 — Foundation *(shipped 2026-05-21)*

The first release. Enough to acquire a distributed lock across processes, with a real lease,
auto-renewal, and reentrancy.

- `IDistributedLock` with blocking `AcquireAsync` (wait + retry) and non-blocking `TryAcquireAsync`.
- `IDistributedLockHandle` with `IsHeld` and a `LostToken` that trips when the lease is lost mid-section.
- Background lease auto-renewal watchdog (renews at one third of the lease duration).
- Same-process reentrancy — re-acquiring a held key returns a counted nested handle.
- `OrionLock.Redis` backend (`SET NX PX` acquire, owner-checked Lua renew/release).
- `OrionLock.EntityFrameworkCore` backend (provider-agnostic `OrionLock_Locks` table).
- `OrionLock.Testing` in-memory backend.
- OpenTelemetry `ActivitySource` and `Meter` (`Moongazing.OrionLock`).
- `AddOrionLock()` DI with `UseRedis` / `UseEntityFrameworkCore` / `UseInMemory`.

### v0.1.1 — Logo refresh *(shipped 2026-05-23)*

New minimalist family-style padlock + Orion-star keyhole logo in indigo line-art. No code changes.

### v0.2.0 — SqlServer backend *(shipped 2026-05-24)*

First piece of the original v0.2.0 scope. The remaining three items (Postgres
advisory locks, multi-master RedLock, concurrency stress harness) ship as
follow-up minor releases (0.2.x) rather than landing together.

- **`OrionLock.SqlServer`** backend using `sp_getapplock` — native SQL Server
  application lock with session-scope lifetime. Crash-safe: lock release is
  tied to SQL session lifetime, so a crashed process drops its locks
  automatically (no clock-based expiry). Faster than the generic EF Core
  lock-table for SQL-Server-only deployments.
- **`OrionLockBackendException`** for non-contention backend failures
  (e.g. `sp_getapplock` deadlock-victim, parameter validation), distinct from
  `LockAcquisitionTimeoutException`.

### v0.2.3 — Postgres backend *(shipped 2026-05-26)*

- **`OrionLock.Postgres`** backend using PostgreSQL `pg_try_advisory_lock`.
  Session-scope lifetime, same crash-safe rationale as SqlServer. Closes one
  of the three follow-up items originally bundled into v0.2.0.

### v0.3.0 — HealthChecks + telemetry pass *(shipped 2026-06-01)*

The first release that goes beyond "minimum correct lock" into operational quality. Only the
HealthChecks package and the telemetry pass ship here; FIFO waiter queueing and the Consul
backend follow as v0.3.1 and v0.3.2 (see below) because each deserves its own design cycle.

- **`Moongazing.OrionLock.HealthChecks`** package with `AddOrionLockHealthCheck(...)` on
  `IHealthChecksBuilder`. Probes the registered `IDistributedLockProvider` by acquiring a
  sentinel lock; returns `Healthy`, `Degraded` (contention or `LockAcquisitionTimeoutException`),
  or `Unhealthy` (`OrionLockBackendException` or other exception). Sized for container readiness
  probes.
- **Richer telemetry on the existing `Moongazing.OrionLock` Meter**:
  `orionlock.acquire.latency` histogram tagged by backend (single backend round-trip),
  `orionlock.lease_renewal.duration` histogram tagged by backend (per-renewal),
  `orionlock.health_check.result` counter tagged by result. Plus a [lock-key cardinality
  guidance doc](docs/lock-key-cardinality.md) explaining why OrionLock never tags metrics with
  raw lock keys.

---

## v0.2.x — Remaining v0.2.0-era work *(planned)*

Two of the three items originally bundled into v0.2.0 still ship as follow-up minor versions.
Postgres advisory locks landed in v0.2.3; multi-master RedLock and the concurrency stress
harness remain.

- **Multi-master RedLock algorithm** as a new opt-in `RedLockDistributedLock`
  next to the existing single-instance `RedisLockProvider`. Same
  `IDistributedLockProvider` contract; the difference is correctness under
  Redis-cluster failover scenarios. Consumers pick per workload.
- **Concurrency stress harness** — a multi-process integration test that runs
  N OrionLock instances against a shared backend and asserts mutual exclusion
  under contention. Catches regressions in the lease/renewal paths.

---

## v0.3.x — v0.3.0-era follow-ups *(all shipped)*

The two items originally bundled into v0.3.0 shipped as follow-up minor versions, mirroring the
v0.2.x split, and the v0.3.x line then kept going well past them. Everything below is released.

### v0.3.1 — Lease-renewal failure telemetry *(shipped 2026-06-04)*

Smaller piece of the original v0.3.0 deferral. Splits the `lease.lost` counter into a distinct `lease_renewal.failures` counter; the watchdog continues on exception instead of dropping the lease.

### v0.3.2 — FIFO waiter coordination contract *(shipped 2026-06-09)*

Ships `IFifoWaiterCoordinator` + `NullFifoWaiterCoordinator` default + `InProcessFifoWaiterCoordinator` single-process implementation. Integration into `DistributedLockOptions` and the `AcquireAsync` retry loop stages to v0.3.3 so distributed (cross-process) backends can land without source-breaking the public interface.

### v0.3.3 — FIFO waiter coordination wiring *(shipped 2026-06-09)*

- Wires `IFifoWaiterCoordinator` into the `AcquireAsync` retry loop behind the new `DistributedLockOptions.UseFifoWaiterCoordinator` opt-in flag (default `false`).
- `DistributedLock` constructor gains optional `IFifoWaiterCoordinator` parameter; positional callers of the v0.3.2 single-arg constructor still compile.
- `AddOrionLock()` registers `NullFifoWaiterCoordinator` via `TryAddSingleton`; consumers replace it with `InProcessFifoWaiterCoordinator` (or a future distributed implementation) before the call.
- `TryAcquireAsync` deliberately bypasses the coordinator; opt-in fairness applies only to blocking `AcquireAsync`.

### v0.3.4 — Distributed FIFO backend *(shipped 2026-06-09)*

- **`RedisFifoWaiterCoordinator`** ships in the existing `Moongazing.OrionLock.Redis` package. Sorted-set per lock key, score = arrival epoch ms; `EnterAsync` polls `ZRANGE 0 0`, `LeaveAsync` issues `ZREM`. Cancellation removes the caller from the queue so it does not block waiters behind it.
- `RedisFifoWaiterOptions` (KeyPrefix, PollInterval, WaiterTtl, Database) + `OrionLockBuilder.UseRedisFifoWaiterCoordinator()` DI helper.
- Stale-waiter pruning by score on every Enter / Leave call so crashed processes do not block the queue indefinitely.

### v0.3.5 — `OrionLock.Consul` backend *(shipped 2026-06-10)*

- `ConsulLockProvider` implements `IDistributedLockProvider` over Consul session-bound KV semantics.
- `IConsulClientAdapter` abstraction (`DefaultConsulClientAdapter` over the official Consul.NET client; mocked in unit tests).
- `ConsulLockOptions` (`KeyPrefix`, `SessionBehavior`, `MinSessionTtl`).
- `OrionLockBuilder.UseConsul(address, configure?)` + `UseConsul(configure?)` DI helpers.

### v0.3.6 / v0.3.7 / v0.3.8 — Etcd and ZooKeeper backends *(shipped 2026-06-10)*

The last two backends in the original v0.5.0 plan landed early, ahead of schedule.

- **`OrionLock.Etcd`** backend over etcd v3 lease-bound keys (lease grant + transactional
  put-if-absent, keep-alive renewal, compare-and-swap release).
- **`OrionLock.ZooKeeper`** backend over the canonical ephemeral-sequential znode recipe
  (lowest sequence number holds the lock; session expiry releases on crash). v0.3.8 adds a
  SASL/digest ACL factory (`DigestZooKeeperAclFactory`) alongside the default open-ACL one.

### v0.3.9 through v0.3.29 — Telemetry and fairness depth *(shipped 2026-06-11 to 2026-06-16)*

A run of small additive releases on the existing `Moongazing.OrionLock` Meter and the fairness
path. No public-API breaks; each is source-compatible with the one before. Highlights:

- `WaitForAcquireAsync` polling helper with exponential backoff and jitter.
- Fairness watchdog: `RenewalFailureGracePeriod`-bounded auto-release on prolonged renewal
  failure, with a distinct `orionlock.lease.grace_period_exhausted` counter.
- `WithMetricsLabel` static metric tags for multi-tenant dashboard splitting.
- A family of acquire/lease/handle/reentrancy/fairness instruments
  (`acquire.timeout`, `acquire.cancelled`, `acquire.attempt_count`, `contention.duration`,
  `handle.holding_duration`, `handle.renewals_per_hold`, `leases.held_concurrent`,
  `reentrancy.depth`, `reentrancy.max_depth`, `lease.expired_before_release`,
  `lease.renewal_failures_consecutive`, `fairness.coordinator_enter_duration`,
  `fairness.queue_depth`, plus a `key_hash` cardinality-bucketed tag).
- `ILockEventObserver` consumer-supplied lifecycle observer (acquired, timed-out, lease-lost,
  released), fully wired at the emission sites.

See [CHANGELOG.md](CHANGELOG.md) for the per-release detail.

### v0.4.0 — Shared / exclusive (reader-writer) locks *(shipped 2026-06-19)*

The reader-writer primitive, originally a *Considered* item, shipped for the in-memory backend.
For a given key, either any number of `Shared` (read) holders coexist, OR exactly one
`Exclusive` (write) holder owns it. Acquire, lease/TTL, renewal, release, options, and
diagnostics semantics mirror the exclusive `IDistributedLock`.

- `LockMode` enum (`Shared`, `Exclusive`).
- `ISharedExclusiveLock` with blocking `AcquireSharedAsync` / `AcquireExclusiveAsync` and
  non-blocking `TryAcquireSharedAsync` / `TryAcquireExclusiveAsync`, all returning the existing
  `IDistributedLockHandle`.
- `ISharedExclusiveLockProvider` — the raw single-attempt reader-writer primitive a backend
  implements. Kept separate from `IDistributedLockProvider` so the exclusive-only fast path and
  its wire format are unchanged.
- `SharedExclusiveLock` composer + `SharedExclusiveLockHandle` (background renewal watchdog,
  `IsHeld` / `LostToken`, mode-aware release).
- `OrionLock.Testing` `InMemorySharedExclusiveLockProvider` with real lease-expiry semantics and
  best-effort, in-process writer-starvation mitigation (a lease-bounded pending-writer
  reservation holds off new shared arrivals so existing readers can drain). `UseInMemory()` now
  also registers `ISharedExclusiveLockProvider` and `ISharedExclusiveLock`.

The distributed backends (Redis, EntityFrameworkCore, Postgres, SqlServer, Consul, Etcd,
ZooKeeper) keep the exclusive lock only in this release; a distributed reader-writer
implementation is the next milestone (see v0.5.0 below). Cross-process fair ordering for the
reader-writer lock is part of that work.

### v0.4.1 — Acquire-path allocation trim + self-deriving meter version *(shipped 2026-06-20)*

- The blocking acquire hot path no longer builds the OpenTelemetry activity display name when no
  `ActivitySource` listener is subscribed. `DistributedLock.AcquireAsync` and
  `SharedExclusiveLock` gate the interpolated activity-name string behind
  `ActivitySource.HasListeners()`. With no listener (the production-typical case) `StartActivity`
  returned null and that string was never observed, so the change is behavior-identical; a
  subscribed listener still sees the exact same activity name. Measured on the uncontested
  in-memory acquire/release path, steady-state allocation dropped from about 815 to about 743
  bytes per acquire (roughly 9 percent). No public API, locking, timeout, or fairness semantics
  changed.
- The diagnostics `Meter` / `ActivitySource` version now derives from the assembly version
  (`MeterVersion`) instead of a hand-edited per-release constant, so the long-standing
  per-release version bump no longer needs a manual edit.

### v0.4.2 — Redis distributed reader-writer provider *(shipped 2026-06-22)*

The first distributed `ISharedExclusiveLockProvider`, bringing the v0.4.0 reader-writer seam to a
real cross-process backend. The public surface was already in place, so this is a new backend
implementation behind the existing contract, not an API change. The exclusive-only
`RedisLockProvider` and its wire format are unchanged.

- **`RedisSharedExclusiveLockProvider`** in the existing `Moongazing.OrionLock.Redis` package.
  Per logical key it keeps three Lua-managed Redis keys: a writer string (`:w`) holding the writer
  fencing token, a readers sorted set (`:r`) whose members are reader fencing tokens scored by
  absolute lease-expiry, and a pending-writer string (`:pw`). Every reader/writer transition is a
  single atomic Lua script, so shared holders coexist while an exclusive acquire fails whenever any
  reader or another writer is live.
- Readers are tracked individually in the sorted set (never a bare counter), pruned by score on
  every acquire / renew / release, so one reader's expiry never frees another's. All lease math uses
  the Redis server clock via `redis.call('TIME')` so there is no client-clock-skew hazard.
- Per-mode lease with owner-checked renew/release (a reader by sorted-set membership, a writer by
  fencing-token equality), matching the exclusive Redis provider's ownership discipline so two
  processes cannot release each other's hold. Release of an already-expired share is a no-op.
- Best-effort cross-process writer fairness: a lease-bounded pending-writer marker holds off new
  reader arrivals while a writer waits, so in-flight readers drain and the writer proceeds; the
  marker carries the writer's own lease TTL so a crashed writer cannot block readers forever. This
  is writer-preference, not strict FIFO among writers, and is the distributed analogue of the
  in-memory reservation. It pays off the v0.4.0 "cross-process fair ordering is a follow-up" note
  for the reader-writer path.
- `RedisSharedExclusiveLockOptions` (`KeyPrefix`, `Database`) + the
  `OrionLockBuilder.UseRedisSharedExclusive()` DI helper, additive to `UseRedis`.

### v0.5.0 — PostgreSQL reader-writer provider + acquire-by-deadline *(shipped 2026-06-27)*

The relational half of the distributed reader-writer work, plus the first of the v0.5.0-era
ergonomics items. The public surface (`ISharedExclusiveLock`, `ISharedExclusiveLockProvider`,
`LockMode`) was already in place, so the provider is a new backend behind the existing contract; the
deadline overloads are additive default-interface methods. The family version moves to a uniform
0.5.0 (it supersedes the Redis-only v0.4.2 roadmap point).

- **`PostgresSharedExclusiveLockProvider`** in the existing `OrionLock.Postgres` package, with the
  same correctness guarantees as the Redis provider. Unlike the exclusive-only advisory-lock backend,
  holds are clock-leased rows in a table (default `orionlock_rw_holds`): a reader row per reader keyed
  by its fencing token, one writer row, one pending-writer row, each with an explicit `expires_at`.
  Readers are tracked individually so one reader's expiry never frees another's. Every transition runs
  in a transaction that serializes the key with `pg_advisory_xact_lock`, prunes expired rows, then
  evaluates and writes, so there is no read-then-write race. All lease math uses the server clock via
  `now()`. Owner-checked renew/release; release of an expired share is a no-op. Best-effort writer
  fairness via a lease-bounded pending-writer marker that holds off new readers while a writer waits
  (writer-preference, not strict FIFO), the analogue of the in-memory and Redis reservation.
- `PostgresSharedExclusiveLockOptions` (`KeyPrefix`, `TableName`, `AutoCreateTable`, `CommandTimeout`)
  + `OrionLockBuilder.UsePostgresSharedExclusive(connectionString, configure?)` DI helper, additive to
  `UsePostgres`.
- **`TryAcquireAsync` with a deadline on the reader-writer lock.** `ISharedExclusiveLock` gains
  `TryAcquireSharedAsync(key, deadline, ...)` / `TryAcquireExclusiveAsync(key, deadline, ...)` that
  poll until the deadline and return `null` on expiry instead of throwing
  `LockAcquisitionTimeoutException`, closing the gap between "one attempt" and "block-or-throw". The
  poll delay is clamped to the time left so it cannot overshoot by a full retry interval. The
  exclusive-`IDistributedLock` deadline overload remains a follow-up.

### v0.6.0 — Provider-portable EF Core reader-writer provider + exclusive acquire-by-deadline *(shipped 2026-06-27)*

The reader-writer matrix's portable relational half, plus the exclusive-lock half of the
acquire-by-deadline ergonomics. The public surface was already in place, so the provider is a new
backend behind the existing contract; the exclusive deadline overload is an additive default-interface
method.

- **`EfCoreSharedExclusiveLockProvider`** in the existing `OrionLock.EntityFrameworkCore` package, the
  first *provider-portable* reader-writer backend: it works on SQL Server, PostgreSQL, and any other
  relational EF Core provider through provider-agnostic EF Core, not raw provider SQL. Holds are
  clock-leased rows in `OrionLock_RwHolds` (a `Kind='r'` row per reader keyed by its fencing token, one
  `Kind='w'` writer row, one `Kind='pw'` pending-writer row, each with an explicit `ExpiresOnUtc`),
  mirroring the PostgreSQL schema but not PostgreSQL-specific. Readers are tracked individually so one
  reader's expiry never frees another's. Per-resource serialization uses a `Serializable` transaction
  that first writes the resource's anchor row in `OrionLock_RwResources`, so concurrent transitions for
  one resource conflict and one retries (the portable substitute for `pg_advisory_xact_lock`); the live
  DB clock is read per transition via `CURRENT_TIMESTAMP` (the portable `clock_timestamp()` analogue) so
  there is no client-clock-skew hazard and a hold that lapsed during the serialization wait is reclaimed.
  Owner-checked renew/release; release of an expired share is a no-op. Best-effort writer fairness via
  the lease-bounded pending-writer marker, the analogue of the in-memory, Redis, and PostgreSQL
  reservation.
- `EfCoreSharedExclusiveLockOptions` (`KeyPrefix`, `MaxSerializationRetries`, `SerializationRetryBaseDelay`),
  the two `IEntityTypeConfiguration`s for the holds and resource tables, and the
  `OrionLockBuilder.UseEntityFrameworkCoreSharedExclusive<TDbContext>(configure?)` DI helper, additive to
  `UseEntityFrameworkCore`. The schema is created via EF Core migrations / `EnsureCreated()`, as for any
  other application table (no auto-create, because EF Core owns the schema).
- **`TryAcquireAsync` with a deadline on the exclusive lock.** `IDistributedLock` gains
  `TryAcquireAsync(key, deadline, ...)`, the exclusive counterpart of the 0.5.0 reader-writer deadline
  overloads: it polls until the deadline and returns `null` on expiry instead of throwing. Added as a
  default interface method with a concrete `DistributedLock` implementation that reuses one owner token
  across retries; the poll delay is clamped to the time left so it cannot overshoot by a full interval.

---

## v0.6.x — Fairness and coordination primitives *(planned)*

The remaining depth items, now that the distributed reader-writer matrix covers in-memory, Redis,
PostgreSQL, and (portably) every EF Core relational provider, and both locks have the acquire-by-deadline
surface.

- **Fair queueing beyond opt-in FIFO.** The FIFO coordinator is opt-in and per-acquire. The next
  step is reusing it (and the Redis sorted-set queue) to give the distributed reader-writer lock a
  fair writer/reader ordering rather than the best-effort starvation marker, and folding the
  in-process and Redis coordinators behind one selection point.
- **Distributed counter / sequence primitive** as a sibling abstraction sharing the backend
  infrastructure. Not a lock per se; the same providers can implement it cheaply (etcd and Redis
  in particular).
- **Connection-pooled provider patterns** — guidance plus a sample integration with
  `Microsoft.Extensions.ObjectPool` for high-throughput services.

---

## Observability and conformance *(planned, ongoing)*

Cross-cutting work that lands incrementally rather than in one milestone.

- **Reader-writer telemetry parity.** The exclusive lock has a deep instrument set
  (`acquire.duration`, `contention.duration`, `leases.held_concurrent`, and the rest). The
  shared/exclusive path needs the same coverage, tagged by `mode` (shared vs exclusive) so
  operators can read reader/writer contention separately. A `held_concurrent`-style gauge split by
  mode is the most useful first cut.
- **Conformance suite coverage for the reader-writer semantics.** The shared backend contract
  (many readers coexist; a writer excludes all; lease expiry and renewal behave like the exclusive
  lock) should be expressed as a reusable provider conformance suite, so every new
  `ISharedExclusiveLockProvider` — the Redis and relational ones above, and any third-party one —
  is verified against the same behavioural facts rather than per-backend ad hoc tests.

---

## v1.0.0 — Stable API *(shipped 2026-06-30)*

The 1.0 release is a commitment: we stop changing public types and method signatures inside
the 1.x line. It is a stabilization milestone, not a feature release: no runtime behavior changed
and no existing public API broke. The public surface is captured as-is and frozen.

- **API freeze** — `Microsoft.CodeAnalysis.PublicApiAnalyzers` added to the six packable projects
  (`OrionLock`, `OrionLock.Redis`, `OrionLock.EntityFrameworkCore`, `OrionLock.SqlServer`,
  `OrionLock.Postgres`, `OrionLock.Testing`), each with a `PublicAPI.Shipped.txt` baseline and an
  empty `PublicAPI.Unshipped.txt` wired as `AdditionalFiles`. With `TreatWarningsAsErrors` on, any
  public-surface change now fails the build (RS0016 / RS0017) until the baselines are edited
  deliberately. `IDistributedLock`, `IDistributedLockHandle`, `DistributedLockOptions`, the provider
  primitive interfaces (`IDistributedLockProvider`, `ISharedExclusiveLockProvider`),
  `ISharedExclusiveLock` / `LockMode`, and the bundled backends are frozen. Additions only.
- **Documentation pass** — runnable, copy-pasteable examples for the main scenarios (exclusive
  acquire/release, reader-writer with readers and a writer, `TryAcquireAsync` with a deadline,
  choosing a backend), plus the per-package trimming/AOT posture.
- **AOT readiness audit** — `OrionLock` (core) and `OrionLock.Testing` marked `IsTrimmable` and
  `IsAotCompatible`, built clean with the trim and AOT analyzers; the only core reflection
  (assembly/attribute metadata for telemetry) is AOT-safe. The database and Redis backends are
  documented as not claimed AOT-safe because of their drivers' trimming posture.

---

## Considered (no commitment yet)

- **Hierarchical lock keys** with prefix-based release.
- **Opt-in cross-process reentrancy** via owner-token persistence. A consumer with a stable owner
  identity (e.g. a workflow instance id) could reacquire its own held lock across process restarts
  without dropping the lease. Default behaviour stays process-local; the mode would be an explicit
  opt-in. Needs a concrete workflow-restart use case before it is scheduled.
- **`OrionLock.Distributed.Bridge`** — a thin adapter so OrionGuard's outbox dispatcher can run
  on an OrionLock backend without a consumer-written shim. Tracked in the OrionGuard-side
  v0.1.0 spec under "downstream"; will ship when the OrionGuard team is ready to consume it.

If any of the above maps to a real workload you are on right now, open an issue with the
`roadmap` label and a short description — that is how items move from *considered* to *planned*.

---

## Out of scope for the 1.x line

- **Built-in deadlock detection (wait-for graph).** Lease expiry is the distributed-systems
  answer to a crashed or stuck holder; a separate detector adds complexity without correctness
  gains in the leases-and-renewal model. None of the comparable libraries (RedLock.net,
  medallion DistributedLock) ship one for the same reason.
- **Strong serialisability across backends.** OrionLock is at-least-once under lease
  expiry — consumers' critical sections must be idempotent. We will not invent a stronger
  guarantee that the underlying backend cannot provide.
- **Distributed transactions** built on top of locks. That is OrionFlow's territory; OrionLock
  exposes the primitive and stays out of the orchestration layer.

---

## How to influence priority

- **Open an issue** with the `roadmap` label and describe your use case. Real workload
  demand bumps items up.
- **Reference OrionLock in a public project**, and let us know. Adoption signal matters.
- **Send a focused PR** for a *Considered* item with a concrete design. We will prioritise
  reviewing it.

Dates are targets, not commitments. If a milestone date slips by more than four weeks, the
delay shows up here.
