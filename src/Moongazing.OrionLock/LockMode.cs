namespace Moongazing.OrionLock;

/// <summary>
/// The access mode requested when acquiring a reader-writer (shared/exclusive) lock via
/// <see cref="ISharedExclusiveLock"/>. Multiple <see cref="Shared"/> holders may coexist for a
/// given key, but a single <see cref="Exclusive"/> holder excludes all others.
/// </summary>
public enum LockMode
{
    /// <summary>
    /// A shared (read) hold. Any number of shared holders may hold the same key concurrently,
    /// provided no exclusive holder owns it.
    /// </summary>
    Shared = 0,

    /// <summary>
    /// An exclusive (write) hold. At most one exclusive holder may own the key, and only when no
    /// shared holders own it.
    /// </summary>
    Exclusive = 1,
}
