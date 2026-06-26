# OrionLock.Postgres

PostgreSQL `pg_try_advisory_lock` backend for OrionLock distributed locking.

Lock lifetime is the PostgreSQL session lifetime: a crashed process releases its locks automatically with no clock-based expiry.

```csharp
services.AddOrionLock()
        .UsePostgres("Host=localhost;Database=app;Username=...;Password=...", o =>
        {
            o.KeyPrefix = "app:";
            o.CommandTimeout = TimeSpan.FromSeconds(30);
        });
```

### Notes

- **64-bit integer keys.** Postgres advisory locks are keyed by `bigint`. The provider hashes `KeyPrefix + key` with SHA-256 and takes the first 8 bytes as a little-endian `int64`. Collision risk is negligible for realistic key counts; use `KeyPrefix` to namespace if you also share the database with `pg_advisory_lock` from other code paths.
- **Session-scoped, no clock expiry.** A crashed process releases its locks the moment the database session terminates. There is no lease timer in Postgres itself; OrionLock's renewal watchdog only probes the connection liveness.
- **Connection pooling.** The provider holds each dedicated `NpgsqlConnection` open for the lifetime of the lock and disposes it on release, returning it to the Npgsql pool only after `pg_advisory_unlock` has run.

## Reader-writer (shared/exclusive) lock

```csharp
services.AddOrionLock()
        .UsePostgresSharedExclusive("Host=localhost;Database=app;Username=...;Password=...", o =>
        {
            o.TableName = "orionlock_rw_holds";   // default; idempotently created on first use
            o.AutoCreateTable = true;             // set false to manage the schema out of band
        });

// resolve ISharedExclusiveLock and acquire shared/exclusive holds
```

For a given key, any number of `Shared` (read) holders coexist, OR exactly one `Exclusive` (write) holder owns it.

- **Clock-leased table, not advisory locks.** Unlike the exclusive-only backend, the reader-writer provider models each hold as a row (reader, writer, or pending-writer) in a table with an explicit `expires_at`, so it can track readers individually with their own fencing token and reclaim a dead reader at its own expiry. Lease durations are therefore honoured as a wall-clock TTL against the PostgreSQL server clock (`now()`).
- **Atomic transitions.** Every acquire / renew / release runs in a transaction that first serializes all transitions for the key with `pg_advisory_xact_lock` (auto-released at commit), then prunes expired rows, evaluates, and writes. There is no read-then-write race.
- **Per-reader fencing.** The caller's owner token is the fencing token. Renew and release affect only the caller's own share; releasing an already-expired share is a no-op.
- **Writer fairness.** A blocked writer plants a lease-bounded pending-writer marker that holds off NEW readers (an existing reader may still refresh) so in-flight readers drain and the writer proceeds. The marker carries the writer's own lease, so a crashed writer cannot block readers past that TTL. This is writer-preference, not strict FIFO among writers.

## Acquire-or-give-up-by-deadline

`ISharedExclusiveLock` also exposes deadline overloads that poll until a deadline and return `null` on expiry instead of throwing `LockAcquisitionTimeoutException`:

```csharp
var handle = await rwLock.TryAcquireExclusiveAsync("k", deadline: TimeSpan.FromSeconds(2));
if (handle is null) { /* could not acquire in time - ordinary control flow */ }
```

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
