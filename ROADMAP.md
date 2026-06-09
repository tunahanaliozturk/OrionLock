# OrionLock Roadmap

This document lists what is shipped, what is actively planned, and what we are deliberately
*not* building. It is a planning artifact, not a contract — dates slip, priorities reshuffle.
If an item here matters to you, open a GitHub issue so we can weigh it against everything else.

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

## v0.3.x — Remaining v0.3.0-era work *(planned)*

The two items originally bundled into v0.3.0 ship as follow-up minor versions. Each lands as
its own release after its own design cycle, mirroring the v0.2.x split.

### v0.3.1 — Lease-renewal failure telemetry *(shipped 2026-06-04)*

Smaller piece of the original v0.3.0 deferral. Splits the `lease.lost` counter into a distinct `lease_renewal.failures` counter; the watchdog continues on exception instead of dropping the lease.

### v0.3.2 — FIFO waiter coordination contract *(shipped 2026-06-09)*

Ships `IFifoWaiterCoordinator` + `NullFifoWaiterCoordinator` default + `InProcessFifoWaiterCoordinator` single-process implementation. Integration into `DistributedLockOptions` and the `AcquireAsync` retry loop stages to v0.3.3 so distributed (cross-process) backends can land without source-breaking the public interface.

### v0.3.3 — FIFO waiter coordination wiring *(shipped 2026-06-09)*

- Wires `IFifoWaiterCoordinator` into the `AcquireAsync` retry loop behind the new `DistributedLockOptions.UseFifoWaiterCoordinator` opt-in flag (default `false`).
- `DistributedLock` constructor gains optional `IFifoWaiterCoordinator` parameter; positional callers of the v0.3.2 single-arg constructor still compile.
- `AddOrionLock()` registers `NullFifoWaiterCoordinator` via `TryAddSingleton`; consumers replace it with `InProcessFifoWaiterCoordinator` (or a future distributed implementation) before the call.
- `TryAcquireAsync` deliberately bypasses the coordinator; opt-in fairness applies only to blocking `AcquireAsync`.

### v0.3.4 — Distributed FIFO backend *(planned, retargeted from v0.3.3 distributed-backend slot)*

- First distributed (cross-process) `IFifoWaiterCoordinator` implementation backed by Redis
  sorted-set queueing. Plugs into the v0.3.3 wiring without source-breaking the public
  interface.

### v0.3.5 — `OrionLock.Consul` backend *(planned, retargeted from v0.3.4)*

- **`OrionLock.Consul`** backend - third-party Consul-managed sessions as an
  `IDistributedLockProvider`. Brings the HashiCorp Consul .NET SDK as a new top-level
  dependency and warrants its own release alongside the matching session-TTL design notes.

---

## v0.4.0 — Cross-process reentrancy & owner-token persistence *(planned, Q1 2027)*

A carefully-scoped extension to the v0.1.0 reentrancy model.

- **Opt-in cross-process reentrancy** via owner-token persistence in the backend. A consumer
  with a stable owner identity (e.g., a workflow instance id) can reacquire its own held lock
  across process restarts without dropping the lease. Default behaviour stays process-local;
  the new mode is explicit `opts.OwnerIdentity = "..."`.
- **Pluggable owner-token format** — for advanced consumers who need to encode tenant id,
  correlation id, or other context into the owner token without rewriting backend providers.
- **Throughput pass** — micro-optimisation of the hot `TryAcquireAsync` path on each backend.

---

## v0.5.0 — Backends & coordination primitives *(planned, Q1-Q2 2027)*

Round out the backend matrix and add the closest neighbours to "lock" that real consumers ask for.

- **`OrionLock.ZooKeeper`** backend — for shops that already run ZooKeeper for other coordination.
- **Distributed counter / sequence primitive** as a sibling abstraction in `OrionLock`, sharing
  the backend infrastructure. Not a lock per se; the same providers can implement it cheaply.
- **Connection-pooled provider patterns** — guidance + a sample integration with the
  `Microsoft.Extensions.ObjectPool` infrastructure for high-throughput services.

---

## v1.0.0 — Stable API *(planned, Q2 2027)*

The 1.0 release is a commitment: we stop changing public types and method signatures inside
the 1.x line. Anything obsolete by then is removed; everything that remains is stable.

- **API stability** — `IDistributedLock`, `IDistributedLockHandle`, `DistributedLockOptions`,
  the provider primitive interface, and the four bundled backends freeze. Additions only.
- **Documentation pass** — every public type has a runnable example. Lease/renewal pitfalls
  documented exhaustively. Migration guide from any breaking change introduced in 0.x.
- **AOT readiness audit** — every reflection path annotated; trimmer-safe by default.
- **`OrionLock.Testing` polish** — deterministic-lease test helpers, scenario builders for
  contention and lease loss.

---

## Considered (no commitment yet)

- **`OrionLock.Etcd`** backend.
- **Read/write lock** primitive (multiple readers, single writer).
- **Hierarchical lock keys** with prefix-based release.
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
