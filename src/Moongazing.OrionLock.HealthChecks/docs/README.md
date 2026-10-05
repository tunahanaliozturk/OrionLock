# OrionLock.HealthChecks

`IHealthCheck` integration for [OrionLock](https://www.nuget.org/packages/OrionLock). Probes backend reachability by acquiring and releasing a sentinel lock, so container readiness probes fail fast when the lock backend is unreachable.

![OrionLock packages: the app calls the OrionLock core and one backend package implements IDistributedLockProvider underneath it; Consul, Etcd, ZooKeeper and HealthChecks are not published yet](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/overview.png)

## Status: not published

This package is at **0.7.0** and is **not on nuget.org**; the release job does not pack it, and the
`dotnet add package` line below works only once it is published. The reason is test coverage, not
readiness: its test suite has no Testcontainers reference, no fixture and no CI service, so it has only ever probed the in-memory provider and throwing fakes, never a real backend. It holds no lock semantics of its own; it probes whichever backend is registered.
Until that coverage exists, use it by project reference from the
[repository](https://github.com/tunahanaliozturk/OrionLock), with that caveat in mind.

## Install

    dotnet add package OrionLock.HealthChecks

It needs the core `OrionLock` package and one registered backend.

## Quick start

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.HealthChecks;
using Moongazing.OrionLock.Redis;

services.AddOrionLock().UseRedis("localhost:6379");

services.AddHealthChecks()
        .AddOrionLockHealthCheck(
            name: "orionlock",
            failureStatus: HealthStatus.Degraded,
            tags: ["ready", "infra"]);
```

## Outcomes

- **Healthy** - the sentinel was acquired and released; the backend is reachable.
- **Degraded** - the backend is reachable but the sentinel stayed held for the whole probe `WaitTimeout` (likely another probe).
- **The registration's `failureStatus`** (`Unhealthy` unless you pass another) - the backend threw.

Each probe increments the `orion.lock.health_check.result` counter on the OrionLock meter, tagged with `result`.

## Options (`OrionLockHealthCheckOptions`)

| Option | Default |
| --- | --- |
| `SentinelKey` | `"orionlock:_healthcheck"` |
| `LeaseDuration` | 2 s |
| `WaitTimeout` | 500 ms |

Pass them through the `configure` argument of `AddOrionLockHealthCheck`, and a probe deadline through `timeout`.

## Related packages

- `OrionLock` - the core: `IDistributedLock`, options, lease watchdog.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
