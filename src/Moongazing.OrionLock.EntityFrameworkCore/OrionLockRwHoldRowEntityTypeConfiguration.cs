using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// v0.6.0: EF Core mapping for the reader-writer hold rows (<see cref="OrionLockRwHoldRow"/>) and the
/// per-resource serialization rows (<see cref="OrionLockRwResourceRow"/>). Apply BOTH inside
/// <c>OnModelCreating</c> when a <see cref="DbContext"/> backs
/// <see cref="EfCoreSharedExclusiveLockProvider"/>:
/// <code>
/// modelBuilder.ApplyConfiguration(new OrionLockRwHoldRowEntityTypeConfiguration());
/// modelBuilder.ApplyConfiguration(new OrionLockRwResourceRowEntityTypeConfiguration());
/// </code>
/// They are kept separate from <see cref="OrionLockRowEntityTypeConfiguration"/> (the exclusive-only
/// <c>OrionLock_Locks</c> table) so the exclusive backend's schema is completely unchanged.
/// </summary>
public sealed class OrionLockRwHoldRowEntityTypeConfiguration : IEntityTypeConfiguration<OrionLockRwHoldRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrionLockRwHoldRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OrionLock_RwHolds");
        // Composite key: many reader rows per resource, at most one writer / pending-writer row per
        // resource (the acquire predicates enforce singularity). Kind is short ('r' / 'w' / 'pw').
        builder.HasKey(x => new { x.Resource, x.Kind, x.OwnerToken });
        builder.Property(x => x.Resource).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(2).IsRequired();
        builder.Property(x => x.OwnerToken).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ExpiresOnUtc).IsRequired();
        // Keeps the per-resource prune (DELETE WHERE Resource = @r AND ExpiresOnUtc <= @now) cheap.
        builder.HasIndex(x => new { x.Resource, x.ExpiresOnUtc });
    }
}
