# OrionLock.EntityFrameworkCore

EF Core lock-table backend for [OrionLock](https://www.nuget.org/packages/OrionLock). One row per lock key in `OrionLock_Locks`; provider-agnostic (PostgreSQL, SQL Server, MySQL, SQLite).

```csharp
modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());

services.AddOrionLock().UseEntityFrameworkCore<AppDbContext>();
```

Run `dotnet ef migrations add Add_OrionLock_Locks`.

## Reader-writer (shared/exclusive) lock

A provider-portable distributed reader-writer lock: many `Shared` (read) holders coexist, OR one `Exclusive` (write) holder. Works on any relational EF Core provider (SQL Server, PostgreSQL, ...) through provider-agnostic EF Core. Holds are clock-leased rows in `OrionLock_RwHolds`; per-resource serialization uses a `Serializable` transaction over an anchor row in `OrionLock_RwResources`, and the live DB clock (`CURRENT_TIMESTAMP`) is read per transition.

```csharp
modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());

services.AddOrionLock().UseEntityFrameworkCoreSharedExclusive<AppDbContext>();
```

Create both tables via EF Core migrations / `Database.EnsureCreated()`, then resolve `ISharedExclusiveLock`.

See https://github.com/tunahanaliozturk/OrionLock.
