namespace Moongazing.OrionLock.Diagnostics;

/// <summary>
/// Tags an <see cref="Providers.IDistributedLockProvider"/> implementation with a stable
/// backend identifier (e.g. <c>redis</c>, <c>sqlserver</c>, <c>postgres</c>, <c>efcore</c>,
/// <c>inmemory</c>). The core layer reads this to label per-backend telemetry; sibling
/// packages should keep the value lowercase, ASCII, and dot-free so it composes cleanly
/// into a metric tag.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class BackendNameAttribute : Attribute
{
    /// <summary>The stable backend identifier.</summary>
    public string Name { get; }

    /// <summary>Initializes the attribute with the backend identifier.</summary>
    public BackendNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}

/// <summary>Reflection helper for resolving the backend name of an <see cref="Providers.IDistributedLockProvider"/>.</summary>
public static class BackendNameResolver
{
    /// <summary>The identifier used when a provider does not carry a <see cref="BackendNameAttribute"/>.</summary>
    public const string UnknownBackend = "unknown";

    /// <summary>
    /// Returns the backend identifier declared by <see cref="BackendNameAttribute"/> on the
    /// provider's runtime type, or <see cref="UnknownBackend"/> if none is declared.
    /// </summary>
    public static string Resolve(Providers.IDistributedLockProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var attr = (BackendNameAttribute?)Attribute.GetCustomAttribute(provider.GetType(), typeof(BackendNameAttribute));
        return attr?.Name ?? UnknownBackend;
    }
}
