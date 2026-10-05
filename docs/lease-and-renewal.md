# Lease and renewal

## The lease

Every successful `AcquireAsync` carries a **lease** — a time-bounded grant of ownership. After the lease's `ExpiresOnUtc` passes, the backend treats the key as free and the next caller can take it. This is the distributed-systems answer to a crashed holder: a process that died holding the lock cannot block the system forever.

`DistributedLockOptions` carries the values that govern the lease:

| Option | Default | Meaning |
|---|---|---|
| `LeaseDuration` | 30s | How long the backend grant is valid before another caller can take over. |
| `WaitTimeout` | 10s | How long a blocking `AcquireAsync` keeps waiting before throwing `LockAcquisitionTimeoutException`. |
| `RetryInterval` | 250 ms | The floor of the wait between attempts when the backend has to poll. |
| `RetryBackoffCeiling` | null (flat `RetryInterval`) | When set, each poll sleeps a random duration between `RetryInterval` and this ceiling. |
| `AutoRenew` | true | Whether OrionLock runs a background watchdog to extend the lease. |
| `RenewalFailureGracePeriod` | null (`LeaseDuration`) | How long renewals may keep throwing on a TTL backend before the lease is given up. |
| `MaxHoldDuration` | null (ten times `LeaseDuration`) | When the watchdog stops renewing a hold that was never disposed. Only applies with `AutoRenew = true`. |

## The auto-renewal watchdog

When `AutoRenew = true`, OrionLock starts a background task on every successful acquire. The watchdog tries to extend the lease every `LeaseDuration / 3` (a 30s lease renews every 10s) — three attempts per lease window, so a single transient failure does not lose the lease. The interval never goes below 10 ms: a lease shorter than 30 ms gets fewer than three attempts per window, and a lease shorter than 10 ms can expire on a TTL backend before the first renewal runs.

Renewal goes through the backend's owner-checked path: Redis runs a Lua compare-and-extend; EF Core runs an owner-token-conditioned `UPDATE`. A renewal only succeeds while this caller still holds the lease.

## Lease loss

![OrionLock lease renewal: the watchdog waits LeaseDuration / 3, stops at MaxHoldDuration, calls TryRenewAsync, loses the lease on false or once renewal failures outlast RenewalFailureGracePeriod on a TTL backend, and with AutoRenew = false a TTL backend's lease expires at LeaseDuration](diagrams/lease-renewal.png)

The watchdog gives the lease up when:

- a renewal returns `false`: the backend says this owner no longer holds the key;
- renewals keep throwing on a TTL backend (Redis, EF Core, in-memory, Consul, etcd) and none has succeeded for `RenewalFailureGracePeriod`. A single transient failure is retried on the next tick. On the session-scoped backends (PostgreSQL, SQL Server, ZooKeeper) a throwing renewal is retried for as long as the session lives;
- the hold has lasted `MaxHoldDuration`: the watchdog surrenders and releases the lock as a leak backstop. The check runs inside the renewal loop, so it needs `AutoRenew = true`; with `AutoRenew = false` on a session-scoped backend (PostgreSQL, SQL Server, ZooKeeper) there is neither a watchdog nor an expiry timer, and a hold that is never disposed stays held;
- `AutoRenew = false` on a TTL backend and `LeaseDuration` has elapsed.

In each case it:

1. flips `handle.IsHeld` to false,
2. cancels `handle.LostToken`,
3. stops renewing and calls `ILockEventObserver.OnLeaseLost`.

The critical section is now running **without** the lock. OrionLock cannot abort the section for you; it only makes the loss observable. Use `LostToken` to bail safely:

```csharp
await using var handle = await locker.AcquireAsync(
    "order:42",
    new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) });
await ProcessAsync(order, handle.LostToken);
// or check periodically:
foreach (var item in items)
{
    if (!handle.IsHeld) throw new OperationCanceledException("Lease lost.");
    Process(item);
}
```

`handle.ThrowIfLost()` throws `LeaseLostException` for code paths that want to throw rather than poll, but the canonical pattern is `LostToken` passed into cancellable inner work.

## A note on the SqlServer backend

`OrionLock.SqlServer` has the same `IDistributedLockHandle` contract as the
other backends — `IsHeld` flips and `LostToken` fires when the lease is lost —
but its underlying lease model is different. There is no clock-based expiry.
The lock is held while the SQL session that took it is alive, and `LeaseDuration`
only governs how often the watchdog runs its `SELECT 1` connection health check.

The practical effect: false positives from clock skew between application
nodes and SQL Server are impossible on this backend. The trade-off is that
each held lock costs one open SQL connection.

## Choosing `LeaseDuration`

- **Long enough** that the critical section's worst-case wall-clock fits inside it (otherwise the watchdog's three retries cannot cover transient backend hiccups before expiry).
- **Short enough** that a crashed holder frees the lock reasonably fast — a 60-minute lease means a crashed worker blocks the queue for an hour.
- 30 seconds is a reasonable default for an HTTP-request-scoped lock. Background workers with longer critical sections raise this; user-facing locks with short sections may lower it.

## Why not block forever?

Distributed locks never give the strong "I hold this for sure" guarantee an in-process `lock` gives. Network partitions, process pauses (GC, hypervisor freeze), and clock skew can all cause a caller to *think* it holds a lock it has actually lost. The lease bounds the damage: on a TTL backend the lock is automatically free after `LeaseDuration` (on a session-scoped backend, when the session ends), regardless of the original holder's state. `LostToken` lets the holder participate in detection. OrionLock dispatch is at-least-once from this angle — critical sections should be idempotent.
