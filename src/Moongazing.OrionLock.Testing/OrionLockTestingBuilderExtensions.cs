using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>Registers the in-memory OrionLock backend for tests.</summary>
public static class OrionLockTestingBuilderExtensions
{
    /// <summary>Uses an in-process <see cref="InMemoryLockProvider"/> — for tests only.</summary>
    public static OrionLockBuilder UseInMemory(this OrionLockBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IDistributedLockProvider, InMemoryLockProvider>();
        return builder;
    }
}
