using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.DependencyInjection;

/// <summary>
/// Returned by <c>AddOrionLock</c>. Backend packages add <c>Use*</c> extension methods on this
/// type to register an <see cref="Providers.IDistributedLockProvider"/> through
/// <see cref="UseBackend"/>.
/// </summary>
public sealed class OrionLockBuilder
{
    private string? backend;

    /// <summary>Creates a builder over the given service collection.</summary>
    public OrionLockBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Registers <paramref name="factory"/> as THE <see cref="IDistributedLockProvider"/> for this
    /// builder. Every backend's <c>Use*</c> method goes through here, so all of them behave the same
    /// way.
    /// </summary>
    /// <param name="backendName">
    /// The backend being registered, as it appears in the exception when two of them are asked for
    /// (for example <c>"redis"</c>).
    /// </param>
    /// <param name="factory">Builds the provider from the resolved service provider.</param>
    /// <exception cref="InvalidOperationException">
    /// A different backend was already registered on this builder. OrionLock resolves exactly one
    /// <see cref="IDistributedLock"/> from exactly one backend, so two of them is a composition-root
    /// mistake, not a choice to make silently.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This replaced two opposite conventions that produced opposite outcomes from the same code shape:
    /// Redis, SQL Server, PostgreSQL, EF Core and Testing registered with <c>TryAddSingleton</c> (first
    /// registration won), while Consul, etcd and ZooKeeper used <c>RemoveAll</c> + <c>AddSingleton</c>
    /// (last registration won). <c>UseInMemory().UseRedis(...)</c> therefore ran the in-memory fake in
    /// production, with no error, while <c>UseInMemory().UseConsul(...)</c> ran Consul - decided purely
    /// by which backend you picked.
    /// </para>
    /// <para>
    /// Re-registering the SAME backend is allowed and replaces the previous registration, so a later
    /// <c>UseRedis(...)</c> with different options wins as you would expect. To swap the backend
    /// deliberately - a test host overriding the production registration - start a fresh builder with
    /// another <c>AddOrionLock()</c> call, which replaces the provider without the guard.
    /// </para>
    /// </remarks>
    public OrionLockBuilder UseBackend(
        string backendName, Func<IServiceProvider, IDistributedLockProvider> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
        ArgumentNullException.ThrowIfNull(factory);

        if (backend is not null && !string.Equals(backend, backendName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"OrionLock is already registered with the '{backend}' backend; '{backendName}' would "
                + "replace it. Exactly one backend backs one IDistributedLock, so registering two is a "
                + "composition-root mistake rather than a preference: keep the one you meant and delete "
                + "the other Use* call. To override deliberately (a test host replacing the production "
                + "registration), start a fresh builder with another AddOrionLock() call.");
        }

        backend = backendName;
        Services.RemoveAll<IDistributedLockProvider>();
        Services.AddSingleton(factory);
        return this;
    }
}
