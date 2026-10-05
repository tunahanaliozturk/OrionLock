# OrionLock.ZooKeeper

Apache ZooKeeper backend for [OrionLock](https://www.nuget.org/packages/OrionLock), following the lock recipe from the ZooKeeper documentation: each acquire creates an ephemeral sequential child under a parent znode for the key, and the holder is the child with the lowest sequence number. When a session expires ZooKeeper deletes its znode, so a crashed holder loses the lock with no outside help.

![OrionLock packages: the app calls the OrionLock core and one backend package implements IDistributedLockProvider underneath it; Consul, Etcd, ZooKeeper and HealthChecks are not published yet](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/overview.png)

## Status: not published

This package is at **0.7.0** and is **not on nuget.org**; the release job does not pack it, and the
`dotnet add package` line below works only once it is published. The reason is test coverage, not
readiness: its test suite has no Testcontainers reference, no fixture and no CI service, so the provider has never run against a real ZooKeeper ensemble - every test uses a fake client adapter.
Until that coverage exists, use it by project reference from the
[repository](https://github.com/tunahanaliozturk/OrionLock), with that caveat in mind.

## Install

    dotnet add package OrionLock.ZooKeeper

It plugs into the core `OrionLock` package, which it references, and uses the `ZooKeeperNetEx` client.

## Quick start

`UseZooKeeper` resolves a `ZooKeeper` client you register yourself, because the connection needs a
`Watcher` your application owns for session-state and reconnect handling.

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.ZooKeeper;
using org.apache.zookeeper;

services.AddSingleton(_ => new ZooKeeper("localhost:2181", 30_000, myWatcher));

services.AddOrionLock().UseZooKeeper(o => o.RootPath = "/orionlock"); // default root

var locker = serviceProvider.GetRequiredService<IDistributedLock>();
await using var handle = await locker.AcquireAsync("order:42");
```

## Session-scoped, no fencing token

A hold lasts as long as the ZooKeeper session, not a wall-clock lease, so `handle.EffectiveLeaseDuration`
is `Timeout.InfiniteTimeSpan`. `handle.FencingToken` is `null`: the sequence number and the parent's
`cversion` both reset when an empty parent znode is pruned, so neither is strictly monotonic per key.

## Waiting watches the predecessor

The ephemeral sequential child is created once and kept for the whole wait. When it is not the lowest,
the waiter watches its immediate predecessor, so waiters are served in arrival order and each position
gained costs two round trips. Anything the waiter registered is deleted on every path that does not win.

## ACLs

By default znodes are created with ZooKeeper's open ACL. On a shared ensemble call
`UseDigestAcl(username, password)` after `UseZooKeeper(...)` to issue `digest` ACLs instead, and add the
matching auth info to your session yourself:
`client.addAuthInfo("digest", Encoding.UTF8.GetBytes($"{username}:{password}"))`. Without it, acquires
fail with `NoAuthException`.

## Lock keys

The core rejects `/`, `.`, `..`, control characters, the ranges ZooKeeper refuses in a znode name and keys
over 200 characters. Use `ZooKeeperLockOptions.RootPath` for hierarchy; it is validated per segment when
the host is built.

## Related packages

- `OrionLock` - the core: `IDistributedLock`, options, lease watchdog.
- `OrionLock.Postgres`, `OrionLock.SqlServer` - published session-scoped backends.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
