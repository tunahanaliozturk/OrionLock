using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>Registers the in-memory OrionLock backend for tests.</summary>
public static class OrionLockTestingBuilderExtensions
{
    /// <summary>
    /// Uses an in-process <see cref="InMemoryLockProvider"/> (exclusive locks) and an in-process
    /// <see cref="InMemorySharedExclusiveLockProvider"/> (v0.4.0 reader-writer locks) — for tests
    /// only. Both <see cref="IDistributedLock"/> and <see cref="ISharedExclusiveLock"/> resolve
    /// against the in-memory backends.
    /// </summary>
    public static OrionLockBuilder UseInMemory(this OrionLockBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IDistributedLockProvider, InMemoryLockProvider>();
        builder.Services.TryAddSingleton<ISharedExclusiveLockProvider, InMemorySharedExclusiveLockProvider>();
        builder.Services.TryAddSingleton<ISharedExclusiveLock>(sp =>
            new SharedExclusiveLock(sp.GetRequiredService<ISharedExclusiveLockProvider>()));
        return builder;
    }
}
