# Fencing tokens

## The hole every lease-based lock has

A distributed lock cannot stop a holder from being wrong about still holding it.

Your process acquires `order:42` on a 30-second lease and enters the critical section. Then it stops — a stop-the-world GC, a VM migration, a container throttled to nothing, a laptop lid. None of your code runs, so nothing observes anything. Forty seconds later the backend expires the key. Another process acquires it, entirely legitimately, and starts working. Your process resumes. From the inside, no time has passed: it is still in the middle of its critical section, and it writes.

Two writers. No bug in the lock. No bug in your code. `handle.IsHeld` was never false, because nothing was running to check it, and `LostToken` never tripped, because renewal never failed — it never ran.

Shortening the lease does not fix it; it only changes how long the pause has to be. Checking `IsHeld` immediately before the write does not fix it; the pause can land between the check and the write. The only known fix moves the decision to the thing being protected: the lock hands out a number that strictly increases with every acquisition, the holder presents that number with its write, and the resource refuses anyone whose number it has already moved past. The resumed process presents an old number and is turned away — by the resource, which is the only participant in a position to know.

This is Martin Kleppmann's ["How to do distributed locking"](https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html), and it is the argument OrionLock had no answer to before `FencingToken`.

## What OrionLock gives you

```csharp
await using var handle = await locker.AcquireAsync("order:42");
long? token = handle.FencingToken;
```

The contract, in full:

- **Strictly increasing per key.** Every successful acquisition of a key gets a number greater than every number that key has handed out before — across processes, across releases, across leases that expired without anyone noticing.
- **Per key, not global.** Two different keys may hand out the same number. A resource only ever compares tokens for the same key, so this costs nothing.
- **Stable for the life of the acquisition.** Renewing the lease does not advance it. A reentrant nested handle reports the outer acquisition's token, not a new one — one critical section presents one number.
- **Not dense.** Gaps are fine and expected. A lost race can burn a number. Nothing depends on the sequence being gapless, only on it going up.
- **`null` when the backend cannot honour that.** Not a fallback counter, not a timestamp, not "close enough". A token that is nearly monotonic is worse than no token, because callers act on it and the failure appears only under exactly the conditions fencing exists for.

`RequireFencingToken()` turns that `null` into an exception at acquire time, which is where you want it — a `null` flowing into a comparison silently disables the protection while every test still passes.

## Where the token comes from, per backend

| Backend | Derived from | Genuinely monotonic? | Default |
| --- | --- | --- | --- |
| **etcd** | the mvcc revision in the acquiring transaction's own response header | **Yes.** Cluster-wide, advances on every committed write, never reset — not by deleting the key, not by lease expiry, not by leader election | On. Free: the revision already comes back with the acquire |
| **Redis** | `INCR` on `{lockKey}:fence`, in the same Lua call as the `SET NX PX` | **Yes.** One atomic script, so the token and the lock cannot come apart and two acquirers cannot read the same value | Off (`RedisLockOptions.FencingTokens`) |
| **EF Core** | the `FencingToken` column, `+ 1` in the same `UPDATE` that takes the row | **Yes.** The row is never deleted — release nulls the owner and leaves the row — so the counter only moves forward | On, but needs a [migration](migrations/orionlock-locks-table.md) |
| **Consul** | the KV `ModifyIndex` of the acquired key | **Yes.** It is the Raft log index: cluster-global, advances on every committed write, never rewound | Off (`ConsulLockOptions.FencingTokens`) |
| **In-memory (Testing)** | a per-key counter bumped under the same compare-and-swap that grants the lease | **Yes, within one process** — the only scope this backend ever claims | On |
| **ZooKeeper** | — | **No** | `null` |
| **PostgreSQL** (`pg_try_advisory_lock`) | — | **No** | `null` |
| **SQL Server** (`sp_getapplock`) | — | **No** | `null` |

### Why Redis fencing is opt-in

The counter key can never expire and is never deleted. It cannot: a counter that restarted would hand a later holder a token an earlier one already spent, which is precisely the failure being prevented. So turning fencing on leaves one small permanent key for every lock key you ever take. That is a storage change, and a library should not make one on your behalf during an upgrade.

The counter key is written as `{prefix+key}:fence` — braces included — so Redis Cluster hashes it into the lock key's slot and the two-key script is not rejected. A lock key that contains a brace of its own can break that co-location, and the cluster then answers `CROSSSLOT`: loudly, at acquire time.

### Why Consul fencing is opt-in, and why not `LockIndex`

Consul's acquire returns a bare `true`/`false` with no index attached, so the provider has to read the entry back — one extra GET per acquire. Only callers who want a token should pay it. The read-back is safe: we hold the key at that point, so nobody else can acquire it, and the next holder must write again, which necessarily lands above whatever we read. If the read fails, the acquire fails — the provider releases the key and destroys the session rather than returning a hold whose token is quietly missing.

`LockIndex` is the trap. It counts how many times a key has been acquired, so it reads exactly like a purpose-built fencing token. It lives on the KV entry, and it dies with it: a session whose behaviour is `delete` removes the key on expiry, and the next acquisition starts counting from `1` again — reissuing a token an earlier holder already used, under exactly the crash-and-expire conditions fencing is for. `ModifyIndex` has no such failure mode because it is not a property of the entry at all; it is the Raft log position at which the entry was last written.

### Why ZooKeeper reports null

Both candidates come from the parent znode, and this provider deletes the parent.

The sequential child's 10-digit suffix is allocated from the parent's `cversion`. OrionLock's ZooKeeper provider prunes a lock's parent znode once its last child is gone — it has to, because the parent is PERSISTENT and ZooKeeper holds its whole tree in memory, so without pruning every key ever locked leaks a znode for the life of the ensemble. When the key is next locked the parent is recreated at `cversion` 0 and the child is called `lock-0000000000` again. Two acquisitions, one number. The parent's `cversion` is the same counter and dies with it.

What would work is the created znode's `czxid` — the ZooKeeper transaction id, which is ensemble-wide, strictly increasing and never reset, exactly like etcd's revision. ZooKeeper's `create` does not return a `Stat`, so reading it means an extra `exists` round trip per acquire and a new method on `IZooKeeperClientAdapter`. That is the upgrade path. Until it exists, `null` is the honest answer.

### Why the two advisory-lock backends report null

`pg_try_advisory_lock` and `sp_getapplock` write nothing that outlives the session. There is no row, no key, no entry — which is the whole reason to choose them over the EF Core lock table, and the reason they are crash-safe without a clock. There is correspondingly nothing to count acquisitions in, and minting a token would mean a sequence or a counter table the caller has to create and grant on: the schema requirement these providers exist to avoid.

The schema-free candidates do not survive inspection either:

- `pg_current_xact_id()` is strictly increasing, but calling it consumes a real transaction id on every acquire. That is transaction-ID wraparound pressure and autovacuum work incurred by taking a lock.
- `pg_snapshot_xmax(pg_current_snapshot())` and `pg_current_wal_lsn()` are free but only *non-decreasing*: two acquisitions with no intervening transaction or WAL write read the same value. Two holders sharing a token is the exact failure being prevented.

If you need fencing on PostgreSQL or SQL Server, use the EF Core backend, whose lock row carries a counter bumped by the same `UPDATE` that takes it.

## What the reader-writer lock does

`ISharedExclusiveLock` handles report `null`.

A `Shared` (read) hold must never carry a token, and this is not a gap to fill later. Readers are concurrent by definition, so there is no order among them for a token to express: whatever number they were handed would either collide — two readers, one token, indistinguishable to the resource — or imply a sequence the lock never enforced. A fencing token exists to let a resource reject a stale *writer*, and a reader is not writing.

An `Exclusive` hold legitimately could carry one. None is minted yet: `ISharedExclusiveLockProvider` has no fenced acquire, and the only backend implementing it is the in-memory testing one. Building the plumbing before a production backend fills it would produce a property that is `null` for a second, less interesting reason. When a reader-writer backend can mint a token for its writer path, that is where it belongs.

## Using the token

### The resource check, in SQL

This is the part that goes wrong. The comparison and the write must be **one statement**, so they commit together:

```sql
UPDATE orders
   SET status     = @status,
       last_fence = @fence
 WHERE id         = @orderId
   AND last_fence < @fence;
```

```csharp
var rows = await connection.ExecuteAsync(Sql, new { orderId, status, fence });
if (rows == 0)
{
    // A higher token has already written. We are the paused holder. Abandon; do not retry.
    throw new StaleFenceException(orderId, fence);
}
```

Details that matter:

- **One statement, not two.** A `SELECT last_fence` followed by an `UPDATE` can be interleaved by exactly the stale writer you are trying to stop — it slips in between your read and your write, and your write then overwrites it.
- **`<`, not `<=`.** Two acquisitions never share a token, so a repeat is a replay of an old one.
- **`last_fence NOT NULL DEFAULT 0`,** so the first write to a brand-new row passes.
- **Zero rows is a real outcome, not an error to swallow.** It means your lease is gone and something else is already doing the work. Retrying is the worst possible response.
- **One `last_fence` per lock key,** because the token is only monotonic per key. If one lock protects several rows, the column belongs on whatever the key identifies.

Add the column to the table the lock protects, not to the lock table:

```sql
ALTER TABLE orders ADD COLUMN last_fence BIGINT NOT NULL DEFAULT 0;
```

### An in-process resource

`FencingGuard` keeps the same high-water mark in memory, for a resource that really does live in this process — an in-memory cache, a file this process owns, a client whose ordering you control:

```csharp
var guard = new FencingGuard();                      // one per resource, long-lived

guard.Accept("order:42", handle.RequireFencingToken());   // throws FencingTokenRegressedException
// or, to branch instead of throw:
if (!guard.TryAccept("order:42", token)) { return; }
```

It is not a substitute for the SQL. Two application instances would each keep their own idea of the highest token, which is no protection at all.

## Telemetry

The token is attached to the acquire span as `orionlock.fencing_token`, and to `ILockEventObserver.OnAcquired(key, durationMs, fencingToken)`.

It is deliberately **not** a metric tag. The token is unique per acquisition, so as a metric dimension it would mint a fresh time series on every single acquire — the same unbounded-cardinality failure described in [lock-key-cardinality.md](lock-key-cardinality.md) for raw lock keys, only worse, because a key at least repeats. Spans are sampled and stored per trace, which is what makes a unique value affordable there.

Recording the token in an audit trail is worth doing: it is what lets you reconstruct, after the fact, the order of two holders who both believed they held the same key.
