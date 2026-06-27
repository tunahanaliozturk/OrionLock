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

    /// <summary>
    /// v0.6.0: uses an EF Core holds table as a PROVIDER-PORTABLE reader-writer (shared/exclusive)
    /// OrionLock backend, resolving <typeparamref name="TDbContext"/> per transition. Registers
    /// <see cref="EfCoreSharedExclusiveLockProvider"/> as <see cref="ISharedExclusiveLockProvider"/> and
    /// the composed <see cref="ISharedExclusiveLock"/>. Additive to the exclusive-only
    /// <see cref="UseEntityFrameworkCore{TDbContext}(OrionLockBuilder)"/> registration; both
    /// <see cref="IDistributedLockProvider"/> and <see cref="ISharedExclusiveLockProvider"/> can then
    /// resolve through EF Core. Uses <c>TryAdd*</c>, matching the exclusive backend, so a consumer-supplied
    /// implementation registered earlier wins.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TDbContext"/> must apply BOTH
    /// <see cref="OrionLockRwHoldRowEntityTypeConfiguration"/> and
    /// <see cref="OrionLockRwResourceRowEntityTypeConfiguration"/> in <c>OnModelCreating</c>, and the
    /// underlying schema must exist (create it with EF Core migrations, or
    /// <c>Database.EnsureCreated()</c>, as for any other application table). Unlike the PostgreSQL
    /// reader-writer provider, this backend uses only provider-agnostic EF Core, so the same lock works on
    /// SQL Server, PostgreSQL, and any other relational EF Core provider.
    /// </remarks>
    public static OrionLockBuilder UseEntityFrameworkCoreSharedExclusive<TDbContext>(
        this OrionLockBuilder builder,
        Action<EfCoreSharedExclusiveLockOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new EfCoreSharedExclusiveLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TDbContext>());
        builder.Services.TryAddSingleton<ISharedExclusiveLockProvider>(
            sp => new EfCoreSharedExclusiveLockProvider(sp.GetRequiredService<IServiceScopeFactory>(), options));
        builder.Services.TryAddSingleton<ISharedExclusiveLock>(
            sp => new SharedExclusiveLock(sp.GetRequiredService<ISharedExclusiveLockProvider>()));

        return builder;
    }
}
