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

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
