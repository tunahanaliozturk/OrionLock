# OrionLock.EntityFrameworkCore

EF Core lock-table backend for [OrionLock](https://www.nuget.org/packages/OrionLock). One row per lock key in `OrionLock_Locks`; provider-agnostic (PostgreSQL, SQL Server, MySQL, SQLite).

```csharp
modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());

services.AddOrionLock().UseEntityFrameworkCore<AppDbContext>();
```

Run `dotnet ef migrations add Add_OrionLock_Locks`.

### Which clock decides expiry

A lease deadline is written by one host and read by another, so it is only meaningful if both compare it
against the same clock. This provider reads the instant from the **database server** on every acquire,
renew and release and does all lease arithmetic from that value — never from `DateTime.UtcNow` on the
application host. Clock drift between your application hosts therefore cannot cause two of them to hold
the same key; only the database's own clock matters. Verified on PostgreSQL (`clock_timestamp()`),
SQL Server (`SYSUTCDATETIME()`) and SQLite (`CURRENT_TIMESTAMP`).

Note that SQLite's `CURRENT_TIMESTAMP` has whole-second resolution, so leases shorter than about two
seconds are not meaningful there. Use a real server for sub-second leases.

### Identifiers

Table and column names are taken from your EF model and quoted with the active provider's own rules, so
renaming the table, adding a schema or remapping a column all keep working, and reserved words are safe
(`Key` is reserved in T-SQL and MySQL). Earlier versions spliced the names in unquoted, which only parsed
on SQLite.

## Reader-writer (shared/exclusive) lock

A provider-portable distributed reader-writer lock: many `Shared` (read) holders coexist, OR one `Exclusive` (write) holder. Works on any relational EF Core provider (SQL Server, PostgreSQL, ...) through provider-agnostic EF Core. Holds are clock-leased rows in `OrionLock_RwHolds`; per-resource serialization uses a `Serializable` transaction over an anchor row in `OrionLock_RwResources`, and the live DB clock (`CURRENT_TIMESTAMP`) is read per transition.

```csharp
modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());

services.AddOrionLock().UseEntityFrameworkCoreSharedExclusive<AppDbContext>();
```

Create both tables via EF Core migrations / `Database.EnsureCreated()`, then resolve `ISharedExclusiveLock`.

See https://github.com/tunahanaliozturk/OrionLock.
