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

## Reference DDL per provider

Use these only if you bypass EF Core migrations (for example, when a DBA owns the schema).

### PostgreSQL

```sql
CREATE TABLE "OrionLock_Locks" (
    "Key"          VARCHAR(200) PRIMARY KEY,
    "OwnerToken"   VARCHAR(64) NULL,
    "ExpiresOnUtc" TIMESTAMP NOT NULL
);
```

### SQL Server

```sql
CREATE TABLE [OrionLock_Locks] (
    [Key]          NVARCHAR(200) NOT NULL PRIMARY KEY,
    [OwnerToken]   NVARCHAR(64) NULL,
    [ExpiresOnUtc] DATETIME2 NOT NULL
);
```

`Key` is a reserved word in SQL Server — the bracketed form `[Key]` is required.

### MySQL / MariaDB

```sql
CREATE TABLE OrionLock_Locks (
    `Key`        VARCHAR(200) NOT NULL,
    OwnerToken   VARCHAR(64) NULL,
    ExpiresOnUtc DATETIME NOT NULL,
    PRIMARY KEY (`Key`)
);
```

### SQLite

```sql
CREATE TABLE OrionLock_Locks (
    Key          TEXT NOT NULL PRIMARY KEY,
    OwnerToken   TEXT NULL,
    ExpiresOnUtc TEXT NOT NULL
);
```
