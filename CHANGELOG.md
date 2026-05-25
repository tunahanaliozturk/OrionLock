# Changelog

All notable changes to OrionLock are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
