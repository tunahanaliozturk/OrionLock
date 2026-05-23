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

---

## v0.2.0 — Multi-master Redis & native DB locks *(planned, Q3 2026)*

Address the two largest "explicitly deferred" items from the v0.1.0 spec.

- **Multi-master RedLock algorithm** as a new opt-in `RedLockDistributedLock` next to the
  existing single-instance `RedisLockProvider`. Same `IDistributedLockProvider` contract; the
  difference is correctness under Redis-cluster failover scenarios. Consumers pick per workload.
- **`OrionLock.SqlServer`** backend using `sp_getapplock` — the native SQL Server application
  lock primitive, with proper transaction and connection-lifetime semantics. Faster than the
  generic EF Core lock-table for SQL Server-only deployments.
- **`OrionLock.Postgres`** backend using PostgreSQL advisory locks (`pg_advisory_lock` /
  `pg_advisory_xact_lock`). Same rationale as SQL Server.
- **Concurrency stress harness** — a multi-process integration test that runs N OrionLock
  instances against a shared backend and asserts mutual exclusion under contention. Catches
  regressions in the lease/renewal paths.

---

## v0.3.0 — Fairness & observability *(planned, Q4 2026)*

The first release that goes beyond "minimum correct lock" into operational quality.

- **Optional FIFO waiter queueing** for blocking `AcquireAsync`. The default polling-retry loop
  is unchanged; the new queued mode lets a consumer pay a small per-acquire cost for fair ordering.
  Disabled by default (would change behaviour for existing callers).
- **`OrionLock.Consul`** backend — third-party Consul-managed sessions as an `IDistributedLockProvider`.
- **Health-check helper** — an `IHealthCheck` that surfaces backend reachability so consumers can
  fail-fast in container probes when the lock backend is unreachable.
- **Richer telemetry**: lease-renewal histogram, per-backend latency tag, lock-key cardinality
  guidance in the docs.

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
