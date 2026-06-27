using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// v0.6.0: EF Core mapping for the per-resource serialization anchor rows
/// (<see cref="OrionLockRwResourceRow"/>) backing <see cref="EfCoreSharedExclusiveLockProvider"/>. Apply
/// alongside <see cref="OrionLockRwHoldRowEntityTypeConfiguration"/> in <c>OnModelCreating</c>.
/// </summary>
public sealed class OrionLockRwResourceRowEntityTypeConfiguration : IEntityTypeConfiguration<OrionLockRwResourceRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrionLockRwResourceRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OrionLock_RwResources");
        builder.HasKey(x => x.Resource);
        builder.Property(x => x.Resource).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Token).HasMaxLength(64).IsRequired();
    }
}
