# OrionLock.EntityFrameworkCore

EF Core lock-table backend for [OrionLock](https://www.nuget.org/packages/OrionLock). One row per lock key in `OrionLock_Locks`; provider-agnostic (PostgreSQL, SQL Server, MySQL, SQLite).

```csharp
modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());

services.AddOrionLock().UseEntityFrameworkCore<AppDbContext>();
```

Run `dotnet ef migrations add Add_OrionLock_Locks`. See https://github.com/tunahanaliozturk/OrionLock.
