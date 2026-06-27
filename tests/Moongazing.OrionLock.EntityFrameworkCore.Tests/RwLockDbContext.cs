using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

/// <summary>
/// A <see cref="DbContext"/> backing the v0.6.0 EF Core reader-writer provider conformance suite. Applies
/// both reader-writer configurations (the holds table and the per-resource serialization anchor table).
/// Used against a real PostgreSQL and a real SQL Server via Testcontainers so the provider is verified on
/// more than one relational EF Core provider, proving the portability claim.
/// </summary>
public sealed class RwLockDbContext(DbContextOptions<RwLockDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());
    }
}
