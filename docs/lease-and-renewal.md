# Lease and renewal

## The lease

Every successful `AcquireAsync` carries a **lease** — a time-bounded grant of ownership. After the lease's `ExpiresOnUtc` passes, the backend treats the key as free and the next caller can take it. This is the distributed-systems answer to a crashed holder: a process that died holding the lock cannot block the system forever.

`DistributedLockOptions` carries three time values that govern the lease:

| Option | Default | Meaning |
|---|---|---|
| `LeaseDuration` | 30s | How long the backend grant is valid before another caller can take over. |
| `WaitTimeout` | 10s | How long a blocking `AcquireAsync` keeps retrying before throwing `LockAcquisitionTimeoutException`. |
| `RetryInterval` | 250 ms | How long blocking acquire waits between attempts. |
| `AutoRenew` | true | Whether OrionLock runs a background watchdog to extend the lease. |

## The auto-renewal watchdog

When `AutoRenew = true`, OrionLock starts a background task on every successful acquire. The watchdog tries to extend the lease every `LeaseDuration / 3` (a 30s lease renews every 10s) — three attempts per lease window, so a single transient failure does not lose the lease.

Renewal goes through the backend's owner-checked path: Redis runs a Lua compare-and-extend; EF Core runs an owner-token-conditioned `UPDATE`. A renewal only succeeds while this caller still holds the lease.

## Lease loss

If a renewal returns false or throws — backend blip, lease already expired, another holder took over — the watchdog:

1. flips `handle.IsHeld` to false,
2. cancels `handle.LostToken`,
3. stops renewing.

The critical section is now running **without** the lock. OrionLock cannot abort the section for you; it only makes the loss observable. Use `LostToken` to bail safely:

```csharp
await using var handle = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30));
await ProcessAsync(order, handle.LostToken);
// or check periodically:
foreach (var item in items)
{
    if (!handle.IsHeld) throw new OperationCanceledException("Lease lost.");
    Process(item);
}
```

`LeaseLostException` exists for code paths that want to throw rather than poll, but the canonical pattern is `LostToken` passed into cancellable inner work.

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

Distributed locks never give the strong "I hold this for sure" guarantee an in-process `lock` gives. Network partitions, process pauses (GC, hypervisor freeze), and clock skew can all cause a caller to *think* it holds a lock it has actually lost. The lease bounds the damage: the lock is automatically free after `LeaseDuration`, regardless of the original holder's state. `LostToken` lets the holder participate in detection. OrionLock dispatch is at-least-once from this angle — critical sections should be idempotent.
