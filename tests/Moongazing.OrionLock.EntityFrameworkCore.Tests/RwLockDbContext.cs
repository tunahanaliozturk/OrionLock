using Microsoft.EntityFrameworkCore;
using Moongazing.OrionLock.Tests.Containers;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

/// <summary>
/// A <see cref="DbContext"/> backing the EF Core provider conformance suites. Applies both reader-writer
/// configurations (the holds table and the per-resource serialization anchor table) AND the exclusive
/// lock-table configuration, so one container fixture serves both providers. Used against a real
/// PostgreSQL and a real SQL Server via Testcontainers so the providers are verified on more than one
/// relational EF Core provider, proving the portability claim.
/// </summary>
public sealed class RwLockDbContext(DbContextOptions<RwLockDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());
    }
}
