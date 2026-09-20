# OrionLock

Distributed locking for .NET — a backend-agnostic `IDistributedLock` with blocking acquire, reentrancy, and background lease auto-renewal.

```csharp
services.AddOrionLock().UseRedis("localhost:6379");

await using var handle = await locker.AcquireAsync(
    "order:42",
    new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) });
// critical section; handle.LostToken trips if the lease is lost
```

A lock key is one opaque name on every backend. `LockKey.Validate` runs in the core at acquire time and
throws `ArgumentException` on your own thread for a key containing `/`, for `.` / `..`, for control
characters and the ranges ZooKeeper refuses in a znode name, and for anything over `LockKey.MaxLength`
(200). Express hierarchy through the backend's own prefix (`KeyPrefix` / `RootPath`), not through the key.

Backends ship separately: `OrionLock.Redis`, `OrionLock.EntityFrameworkCore`, `OrionLock.SqlServer`, `OrionLock.Postgres`, `OrionLock.Testing`. Container readiness probes can use `Moongazing.OrionLock.HealthChecks`. See https://github.com/tunahanaliozturk/OrionLock for the full README.

The core package is trimmable and Native AOT compatible (`IsTrimmable` and `IsAotCompatible` set); its only reflection reads assembly and attribute metadata for telemetry. The database and Redis backends carry their drivers' trimming posture, so they are not claimed AOT-safe.
