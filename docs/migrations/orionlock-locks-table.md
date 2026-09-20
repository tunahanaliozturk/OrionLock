# OrionLock_Locks migration

The `OrionLock.EntityFrameworkCore` backend stores one row per lock key in a table named `OrionLock_Locks`. The consumer applies the EF Core configuration and adds a migration.

## Apply the configuration in your DbContext

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyConfiguration(new Moongazing.OrionLock.EntityFrameworkCore.OrionLockRowEntityTypeConfiguration());
}
```

## Generate the migration

```bash
dotnet ef migrations add Add_OrionLock_Locks --context YourDbContext
dotnet ef database update --context YourDbContext
```

## Upgrading: the `FencingToken` column

`FencingToken` is new. It counts acquisitions of the key and doubles as the [fencing token](../fencing-tokens.md) handed to the acquirer, bumped by the same `UPDATE` that takes the row. Add it the same way:

```bash
dotnet ef migrations add Add_OrionLock_FencingToken --context YourDbContext
dotnet ef database update --context YourDbContext
```

```sql
-- Or, if a DBA owns the schema. Existing rows start at 0, so their next acquisition
-- returns 1 - a number no earlier acquisition of that key can have used, because
-- without the column there were no tokens.
ALTER TABLE "OrionLock_Locks" ADD COLUMN "FencingToken" BIGINT NOT NULL DEFAULT 0;  -- PostgreSQL
ALTER TABLE [OrionLock_Locks] ADD [FencingToken] BIGINT NOT NULL DEFAULT 0;          -- SQL Server
ALTER TABLE OrionLock_Locks ADD FencingToken BIGINT NOT NULL DEFAULT 0;              -- MySQL / MariaDB
ALTER TABLE OrionLock_Locks ADD FencingToken INTEGER NOT NULL DEFAULT 0;             -- SQLite
```

### If you cannot add it yet

Drop the property from your model and the provider emits exactly the SQL it emitted before fencing existed, reporting no token:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyConfiguration(new Moongazing.OrionLock.EntityFrameworkCore.OrionLockRowEntityTypeConfiguration());
    modelBuilder.Entity<Moongazing.OrionLock.EntityFrameworkCore.OrionLockRow>()
        .Ignore(x => x.FencingToken);
}
```

The provider reads column names from the EF model rather than from string literals, so an unmapped `FencingToken` is detected at the model, not by a failing query. Locking behaviour is unaffected; `handle.FencingToken` is `null`.

## Reference DDL per provider

Use these only if you bypass EF Core migrations (for example, when a DBA owns the schema).

### PostgreSQL

```sql
CREATE TABLE "OrionLock_Locks" (
    "Key"          VARCHAR(200) PRIMARY KEY,
    "OwnerToken"   VARCHAR(64) NULL,
    "ExpiresOnUtc" TIMESTAMP NOT NULL,
    "FencingToken" BIGINT NOT NULL DEFAULT 0
);
```

### SQL Server

```sql
CREATE TABLE [OrionLock_Locks] (
    [Key]          NVARCHAR(200) NOT NULL PRIMARY KEY,
    [OwnerToken]   NVARCHAR(64) NULL,
    [ExpiresOnUtc] DATETIME2 NOT NULL,
    [FencingToken] BIGINT NOT NULL DEFAULT 0
);
```

`Key` is a reserved word in SQL Server — the bracketed form `[Key]` is required.

### MySQL / MariaDB

```sql
CREATE TABLE OrionLock_Locks (
    `Key`        VARCHAR(200) NOT NULL,
    OwnerToken   VARCHAR(64) NULL,
    ExpiresOnUtc DATETIME NOT NULL,
    FencingToken BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (`Key`)
);
```

### SQLite

```sql
CREATE TABLE OrionLock_Locks (
    Key          TEXT NOT NULL PRIMARY KEY,
    OwnerToken   TEXT NULL,
    ExpiresOnUtc TEXT NOT NULL,
    FencingToken INTEGER NOT NULL DEFAULT 0
);
```
