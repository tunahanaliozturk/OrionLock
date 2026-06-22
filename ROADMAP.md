# OrionLock Roadmap

This document lists what is shipped, what is actively planned, and what we are deliberately
*not* building. It is a planning artifact, not a contract — dates slip, priorities reshuffle.
If an item here matters to you, open a GitHub issue so we can weigh it against everything else.

**Current release: 0.4.1.** Reader-writer (shared/exclusive) locking shipped for the in-memory
backend in 0.4.0; 0.4.1 trimmed an allocation on the acquire hot path and made the diagnostics
meter version self-deriving. The next milestone is a *distributed* reader-writer provider.

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

---

## v0.4.x — Distributed reader-writer locks *(planned, next)*

The headline follow-up to v0.4.0. The reader-writer abstraction and the in-memory provider
shipped; the distributed half is the next concrete piece of work. The public surface
(`ISharedExclusiveLock`, `ISharedExclusiveLockProvider`, `LockMode`) is already in place, so this
is new backend implementations behind the existing contract, not an API change.

### v0.4.2 — Redis distributed reader-writer provider *(planned, July 2026)*

- A `RedisSharedExclusiveLockProvider` implementing `ISharedExclusiveLockProvider` in the existing
  `Moongazing.OrionLock.Redis` package. The natural encoding is a small Lua-scripted state per key
  (a writer marker plus a shared-holder set or counter) so the reader/writer transitions stay
  atomic under contention: shared holders coexist, an exclusive acquire fails while any shared
  holder or another writer is live.
- Per-mode lease with owner-checked renew/release, matching the exclusive Redis provider's
  ownership discipline so two processes cannot release each other's hold.
- Best-effort cross-process writer-starvation mitigation (a lease-bounded pending-writer marker
  that holds off new shared arrivals), the distributed analogue of the in-memory reservation. This
  pays off the v0.4.0 "cross-process fair ordering is a follow-up" note for the reader-writer path.

### v0.4.3 — EF Core / Postgres distributed reader-writer provider *(planned, August 2026)*

- A relational `ISharedExclusiveLockProvider` for the SQL backends. The EF Core lock-table model
  extends cleanly to a mode column plus a shared-holder count, so acquire/renew/release become
  guarded `UPDATE`s. Postgres can alternatively use shared-vs-exclusive advisory locks
  (`pg_try_advisory_lock_shared`) where a relational table is not wanted.
- Documented semantics for which relational backend gives which fairness and crash-safety
  guarantee, since session-scoped advisory locks and the clock-leased table differ here.

---

## v0.5.0 — Fairness, ergonomics, and coordination primitives *(planned, Q4 2026)*

With the backend matrix essentially complete (Redis, EF Core, SqlServer, Postgres, Consul, Etcd,
ZooKeeper all ship), the focus shifts from breadth to depth: fairness, acquire ergonomics, and the
nearest neighbours to "lock" that real consumers ask for.

- **`TryAcquireAsync` with a deadline.** Today blocking fairness lives only on `AcquireAsync`, and
  `TryAcquireAsync` is a single shot. A `TryAcquireAsync(key, deadline, ...)` overload that returns
  `null` on expiry instead of throwing `LockAcquisitionTimeoutException` closes the gap between
  "one attempt" and "block-or-throw" without forcing callers to catch a timeout exception for
  ordinary control flow. The shared/exclusive variants get the same treatment.
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

## v1.0.0 — Stable API *(planned, Q2 2027)*

The 1.0 release is a commitment: we stop changing public types and method signatures inside
the 1.x line. Anything obsolete by then is removed; everything that remains is stable.

- **API stability** — `IDistributedLock`, `IDistributedLockHandle`, `DistributedLockOptions`, the
  provider primitive interfaces (`IDistributedLockProvider`, `ISharedExclusiveLockProvider`),
  `ISharedExclusiveLock` / `LockMode`, and the bundled backends (Redis, EF Core, SqlServer,
  Postgres, Consul, Etcd, ZooKeeper, Testing) freeze. Additions only.
- **Documentation pass** — every public type has a runnable example. Lease/renewal pitfalls
  documented exhaustively. Migration guide from any breaking change introduced in 0.x.
- **AOT readiness audit** — every reflection path annotated; trimmer-safe by default.
- **`OrionLock.Testing` polish** — deterministic-lease test helpers, scenario builders for
  contention and lease loss, including the reader-writer modes.

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
