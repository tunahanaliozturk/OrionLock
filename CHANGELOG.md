# Changelog

All notable changes to OrionLock are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-06-01

### Added

- New package `Moongazing.OrionLock.HealthChecks` 0.3.0 with `AddOrionLockHealthCheck(...)` on `IHealthChecksBuilder`. The check probes the registered `IDistributedLockProvider` by acquiring and releasing a configurable sentinel key (`OrionLockHealthCheckOptions.SentinelKey`, default `orionlock:_healthcheck`) with a short lease (default 2 s) and `WaitTimeout` (default 500 ms). Returns `Healthy` on success, `Degraded` on `LockAcquisitionTimeoutException` or sentinel contention, `Unhealthy` on `OrionLockBackendException` or any other exception with the message surfaced in `Data["error"]`. Intended for container readiness probes that should fail fast when the lock backend is unreachable.
- Three new instruments on the existing `Moongazing.OrionLock` Meter:
  - `orionlock.acquire.latency` histogram (milliseconds), tagged with `backend` (`redis`, `sqlserver`, `postgres`, `efcore`, `inmemory`). Measures a single backend `TryAcquireAsync` round-trip and isolates backend latency from the blocking-retry loop that `orionlock.acquire.duration` covers.
  - `orionlock.lease_renewal.duration` histogram (milliseconds), tagged with `backend`. Wired into the renewal watchdog so spikes that risk pushing renewals past `LeaseDuration / 3` are observable.
  - `orionlock.health_check.result` counter, tagged with `result` (`healthy`, `degraded`, `unhealthy`), incremented on every health-check probe.
- New `BackendNameAttribute` and `BackendNameResolver` in `Moongazing.OrionLock.Diagnostics` so providers declare their telemetry identifier as a stable class-level constant. All five bundled providers are annotated.
- Lock-key cardinality guidance documented at [docs/lock-key-cardinality.md](docs/lock-key-cardinality.md). Lock keys are never used as metric tags by OrionLock; if you wrap OrionLock, follow the same rule.

### Changed

- `AddOrionLock` now transparently wraps the registered `IDistributedLockProvider` in an internal measuring decorator at `IDistributedLock` construction time. The decorator records `orionlock.acquire.latency` and `orionlock.lease_renewal.duration` and passes provider exceptions and return values through unchanged. No public-API change for consumers; backends do not need to opt in.

### Deferred from v0.3.0

- **Optional FIFO waiter queueing for `AcquireAsync`** is deferred to v0.3.1. The blocking-acquire semantics change deserves its own design cycle and a behaviour-change opt-in switch.
- **`OrionLock.Consul` backend** is deferred to v0.3.2. Third-party SDK integration belongs in its own release alongside the matching design notes.

### Migration

No breaking changes. The HealthChecks package is opt-in - existing applications continue to work unchanged. The three new instruments are emitted automatically on the existing `Moongazing.OrionLock` Meter; consumers already listening on that Meter pick them up without configuration. The internal measuring decorator does not alter `IDistributedLockProvider` semantics or surface.

## [0.2.3] - 2026-05-26

### Added

- New backend package `OrionLock.Postgres` 0.2.3 using PostgreSQL `pg_try_advisory_lock` for session-scoped advisory locks. Same semantics as `OrionLock.SqlServer`: lock lifetime is the session lifetime; crashed process auto-releases. Configurable `KeyPrefix` and `CommandTimeout`. String keys hashed via SHA-256 to a 64-bit integer (Postgres advisory keys are int64). `Dispose` explicitly calls `pg_advisory_unlock` before returning each held connection to the Npgsql pool, so locks drop deterministically on provider disposal.

## [0.2.2] - 2026-05-26

### Fixed

- Packaged logo is now actually the cream-bg version. v0.2.1 shipped the per-csproj copy of the old transparent logo because csproj `<None Include="docs/logo.png">` resolves relative to the csproj, not the repo root. Per-csproj copies are now synced to the cream-bg root file. No functional change.

## [0.2.1] - 2026-05-26

### Changed

- Logo now ships with a cream (#F7F1E3) background instead of transparent. Improves contrast against dark-mode README rendering and NuGet package card backgrounds. No functional change.

## [0.2.0] - 2026-05-24

### Added

- `OrionLock.SqlServer` backend using native `sp_getapplock` with session-scope
  lifetime. The lock is held while the SQL session is alive — a crashed process
  releases its locks automatically, with no clock-based expiry. `KeyPrefix` and
  `CommandTimeout` options; combined key length limit of 240 characters
  (SQL Server `@Resource` is `nvarchar(255)` with a 15-char safety margin).
- `OrionLockBackendException` for non-contention backend failures (e.g. SQL
  Server `sp_getapplock` deadlock-victim and validation errors), distinct from
  `LockAcquisitionTimeoutException`.

### Notes

The original v0.2.0 scope (sp_getapplock + Postgres advisory locks + multi-master
RedLock + concurrency stress harness) was split into successive minor releases.
This 0.2.0 ships only the SQL Server backend; Postgres, RedLock, and the stress
harness will follow as 0.2.1+.

## [0.1.1] - 2026-05-23

### Changed

- New minimalist family-style logo (padlock with an Orion-star keyhole, indigo line-art, no badge ring) replaces the v0.1.0 circular emblem. Applied to the README and to every package's NuGet icon.

## [0.1.0] - 2026-05-21

### Added

- `IDistributedLock` with blocking `AcquireAsync` (wait + retry) and non-blocking `TryAcquireAsync`.
- `IDistributedLockHandle` with `IsHeld` and a `LostToken` that trips when the lease is lost.
- Background lease auto-renewal watchdog (renews at one third of the lease duration).
- Same-process reentrancy — re-acquiring a held key returns a counted nested handle.
- `OrionLock.Redis` backend (`SET NX PX` acquire, owner-checked Lua renew/release).
- `OrionLock.EntityFrameworkCore` backend (provider-agnostic `OrionLock_Locks` table).
- `OrionLock.Testing` in-memory backend.
- OpenTelemetry `ActivitySource` and `Meter` (`Moongazing.OrionLock`).
- `AddOrionLock()` DI with `UseRedis` / `UseEntityFrameworkCore` / `UseInMemory`.
