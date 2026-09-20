using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// EF Core mapping for <see cref="OrionLockRow"/>. Apply inside <c>OnModelCreating</c>:
/// <c>modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());</c>.
/// </summary>
public sealed class OrionLockRowEntityTypeConfiguration : IEntityTypeConfiguration<OrionLockRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrionLockRow> builder)
    {
        builder.ToTable("OrionLock_Locks");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OwnerToken).HasMaxLength(64);
        builder.Property(x => x.ExpiresOnUtc);
        // Defaulted to 0 so an existing row that predates the column still increments to a token no
        // earlier acquisition of that key can have used - there were none, because without the column
        // there were no tokens.
        builder.Property(x => x.FencingToken).HasDefaultValue(0L);
    }
}
