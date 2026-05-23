using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>Registers the EF Core lock-table OrionLock backend.</summary>
public static class OrionLockEfCoreBuilderExtensions
{
    /// <summary>
    /// Uses an EF Core lock table as the OrionLock backend, resolving <typeparamref name="TDbContext"/>
    /// per acquisition. <typeparamref name="TDbContext"/> must apply
    /// <see cref="OrionLockRowEntityTypeConfiguration"/> in <c>OnModelCreating</c>.
    /// </summary>
    public static OrionLockBuilder UseEntityFrameworkCore<TDbContext>(this OrionLockBuilder builder)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TDbContext>());
        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            sp => new EfCoreLockProvider(sp.GetRequiredService<IServiceScopeFactory>()));

        return builder;
    }
}
