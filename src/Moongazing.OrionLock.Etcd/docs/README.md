# OrionLock.Etcd

etcd v3 backend for [OrionLock](https://www.nuget.org/packages/OrionLock): a lock is a key bound to an etcd lease. `TryAcquire` grants a lease and runs a transactional put-if-absent, renewal keeps the lease alive, and release revokes it.

![OrionLock packages: the app calls the OrionLock core and one backend package implements IDistributedLockProvider underneath it; Consul, Etcd, ZooKeeper and HealthChecks are not published yet](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/overview.png)

## Status: not published

This package is at **0.7.0** and is **not on nuget.org**; the release job does not pack it, and the
`dotnet add package` line below works only once it is published. The reason is test coverage, not
readiness: its test suite has no Testcontainers reference, no fixture and no CI service, so the provider has never run against a real etcd cluster - every test uses a fake client adapter.
Until that coverage exists, use it by project reference from the
[repository](https://github.com/tunahanaliozturk/OrionLock), with that caveat in mind.

## Install

    dotnet add package OrionLock.Etcd

It plugs into the core `OrionLock` package, which it references, and uses the `dotnet-etcd` client.

## Quick start

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Etcd;

services.AddOrionLock().UseEtcd("http://localhost:2379", o => o.KeyPrefix = "orionlock/"); // default prefix

var locker = serviceProvider.GetRequiredService<IDistributedLock>();
await using var handle = await locker.AcquireAsync(
    "order:42",
    new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(10) });
```

`UseEtcd()` without a connection string uses an `IEtcdClient` you registered yourself.

## Lease durations

etcd leases have a whole-second TTL with a floor. `EtcdLockOptions.MinLeaseTtlSeconds` (default 5) is
the shortest lease this backend honours: a shorter `LeaseDuration` is refused with
`ArgumentOutOfRangeException` at acquire time, and a longer one is rounded up to a whole second, which
`handle.EffectiveLeaseDuration` reports.

## Fencing tokens

Always on, at no extra cost: `handle.FencingToken` is the mvcc revision of the transaction that took the
key, which strictly increases across the cluster.

## Waiting watches the key

A failed acquire costs three round trips, so a contended waiter does not poll. It watches the lock key
and retries when the cluster reports it deleted, which covers both a release and a lease that lapsed
under a crashed holder. Nothing has to be configured on the server.

## Lock keys

The core rejects `/`, `.`, `..`, control characters and keys over 200 characters. Use
`EtcdLockOptions.KeyPrefix` for namespacing; the full etcd key is `{KeyPrefix}{key}`.

## Related packages

- `OrionLock` - the core: `IDistributedLock`, options, lease watchdog.
- `OrionLock.Redis` - a published TTL backend with opt-in fencing tokens.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
