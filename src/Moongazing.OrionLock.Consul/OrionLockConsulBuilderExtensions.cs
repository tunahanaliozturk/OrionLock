namespace Moongazing.OrionLock.Consul;

using global::Consul;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

/// <summary>DI helpers for the Consul OrionLock backend.</summary>
public static class OrionLockConsulBuilderExtensions
{
    /// <summary>
    /// Uses Consul as the OrionLock backend, connecting with the supplied
    /// <paramref name="address"/> (e.g. <c>http://localhost:8500</c>).
    /// </summary>
    public static OrionLockBuilder UseConsul(
        this OrionLockBuilder builder,
        string address,
        Action<ConsulLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var options = new ConsulLockOptions();
        configure?.Invoke(options);
        // Validate at DI registration time rather than waiting for the provider's ctor to surface the
        // error on the first acquire. A KeyPrefix that escapes the KV namespace is a startup-time
        // mistake; failing here keeps the stack trace pointing at the consumer's UseConsul(...) call.
        options.ValidateAndNormalise();

        // AddSingleton (NOT TryAddSingleton) so the address-overload's wiring wins over any
        // previously-registered IConsulClient. The TryAdd shape would have silently
        // swallowed the consumer's `address` argument when a client was already in the DI
        // container, contradicting the docstring above.
        builder.Services.AddSingleton<IConsulClient>(_ =>
            new ConsulClient(cfg => cfg.Address = new Uri(address)));
        builder.Services.TryAddSingleton<IConsulClientAdapter>(
            sp => new DefaultConsulClientAdapter(sp.GetRequiredService<IConsulClient>()));

        return builder.UseBackend(
            "consul", sp => new ConsulLockProvider(sp.GetRequiredService<IConsulClientAdapter>(), options));
    }

    /// <summary>
    /// Uses Consul as the OrionLock backend over an already-registered
    /// <see cref="IConsulClient"/>.
    /// </summary>
    public static OrionLockBuilder UseConsul(
        this OrionLockBuilder builder,
        Action<ConsulLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ConsulLockOptions();
        configure?.Invoke(options);
        options.ValidateAndNormalise();

        builder.Services.TryAddSingleton<IConsulClientAdapter>(
            sp => new DefaultConsulClientAdapter(sp.GetRequiredService<IConsulClient>()));

        return builder.UseBackend(
            "consul", sp => new ConsulLockProvider(sp.GetRequiredService<IConsulClientAdapter>(), options));
    }
}
