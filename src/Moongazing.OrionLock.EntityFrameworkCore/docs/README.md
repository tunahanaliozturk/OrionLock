# OrionLock.EntityFrameworkCore

EF Core lock-table backend for [OrionLock](https://www.nuget.org/packages/OrionLock). One row per lock key in `OrionLock_Locks`; provider-agnostic (PostgreSQL, SQL Server, MySQL, SQLite). Also ships a provider-portable reader-writer lock and fencing tokens.

![OrionLock packages: the app calls the OrionLock core and one backend package implements IDistributedLockProvider underneath it](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/overview.png)

## Install

    dotnet add package OrionLock.EntityFrameworkCore

It plugs into the core `OrionLock` package, which it references, and uses your own `DbContext`.

## Quick start

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.EntityFrameworkCore;

// in AppDbContext.OnModelCreating
modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());

// registration (AppDbContext is registered with AddDbContext as usual)
services.AddOrionLock().UseEntityFrameworkCore<AppDbContext>();

var locker = serviceProvider.GetRequiredService<IDistributedLock>();
await using var handle = await locker.AcquireAsync("order:42");
```

Then add the table with a migration: `dotnet ef migrations add Add_OrionLock_Locks`.

### Which clock decides expiry

A lease deadline is written by one host and read by another, so it is only meaningful if both compare it
against the same clock. This provider reads the instant from the **database server** on every acquire,
renew and release and does all lease arithmetic from that value — never from `DateTime.UtcNow` on the
application host. Clock drift between your application hosts therefore cannot cause two of them to hold
the same key; only the database's own clock matters. Verified on PostgreSQL (`clock_timestamp()`),
SQL Server (`SYSUTCDATETIME()`) and SQLite (`CURRENT_TIMESTAMP`).

Note that SQLite's `CURRENT_TIMESTAMP` has whole-second resolution, so leases shorter than about two
seconds are not meaningful there. Use a real server for sub-second leases.

### Fencing tokens

`OrionLock_Locks` carries a `FencingToken` column, bumped by the same `UPDATE` that takes the row, and
`handle.FencingToken` returns it. Because the row is never deleted — release nulls the owner and leaves the
row — the counter only ever moves forward, which is what makes it usable as a fencing token.

**The column is new: run a migration.** If you cannot yet, call `.Ignore(x => x.FencingToken)` on the
entity in your own `OnModelCreating`; the provider reads column names from the EF model, finds nothing
mapped, and emits exactly the SQL it emitted before fencing existed while reporting no token. Locking
behaviour is unchanged either way.

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

## Related packages

- `OrionLock` - the core: `IDistributedLock`, options, lease watchdog.
- `OrionLock.SqlServer`, `OrionLock.Postgres` - native session-scoped locks when you target one database.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
