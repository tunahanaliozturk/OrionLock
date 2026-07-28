# OrionLock.HealthChecks

`IHealthCheck` integration for OrionLock distributed locking. Probes backend reachability by acquiring and releasing a sentinel lock, so container readiness probes fail fast when the lock backend is unreachable.

```csharp
services.AddOrionLock().UseRedis("localhost:6379");

services.AddHealthChecks()
        .AddOrionLockHealthCheck(
            name: "orionlock",
            failureStatus: HealthStatus.Degraded,
            tags: ["ready", "infra"]);
```

## Outcomes

- **Healthy** - sentinel was acquired and released; backend is reachable.
- **Degraded** - backend reachable but the sentinel could not be acquired before `WaitTimeout` (likely contention from another probe).
- **Unhealthy** - backend threw `OrionLockBackendException` or another exception.

Each probe increments the `orion.lock.health_check.result` counter on the OrionLock Meter, tagged with `result`.

Requires the `OrionLock` package and a registered backend. See https://github.com/tunahanaliozturk/OrionLock.
