namespace Moongazing.OrionLock.Etcd;

using global::dotnet_etcd;
using global::dotnet_etcd.interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

/// <summary>DI helpers for the etcd OrionLock backend.</summary>
public static class OrionLockEtcdBuilderExtensions
{
    /// <summary>
    /// Uses etcd as the OrionLock backend, connecting with the supplied
    /// <paramref name="connectionString"/> (e.g. <c>"http://localhost:2379"</c>).
    /// </summary>
    public static OrionLockBuilder UseEtcd(
        this OrionLockBuilder builder,
        string connectionString,
        Action<EtcdLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new EtcdLockOptions();
        configure?.Invoke(options);

        // AddSingleton (NOT TryAddSingleton) so the connection-string overload wins over
        // any previously-registered IEtcdClient. The TryAdd shape would silently swallow
        // the supplied connection string when a client was already in the DI container.
        builder.Services.AddSingleton<IEtcdClient>(_ => new EtcdClient(connectionString));
        builder.Services.TryAddSingleton<IEtcdClientAdapter>(
            sp => new DefaultEtcdClientAdapter(sp.GetRequiredService<IEtcdClient>()));
        builder.Services.RemoveAll<IDistributedLockProvider>();
        builder.Services.AddSingleton<IDistributedLockProvider>(
            sp => new EtcdLockProvider(sp.GetRequiredService<IEtcdClientAdapter>(), options));

        return builder;
    }

    /// <summary>
    /// Uses etcd as the OrionLock backend over an already-registered
    /// <see cref="IEtcdClient"/>.
    /// </summary>
    public static OrionLockBuilder UseEtcd(
        this OrionLockBuilder builder,
        Action<EtcdLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new EtcdLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IEtcdClientAdapter>(
            sp => new DefaultEtcdClientAdapter(sp.GetRequiredService<IEtcdClient>()));
        builder.Services.RemoveAll<IDistributedLockProvider>();
        builder.Services.AddSingleton<IDistributedLockProvider>(
            sp => new EtcdLockProvider(sp.GetRequiredService<IEtcdClientAdapter>(), options));

        return builder;
    }
}
