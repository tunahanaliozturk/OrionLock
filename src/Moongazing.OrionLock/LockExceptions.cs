namespace Moongazing.OrionLock;

/// <summary>Thrown when a blocking acquire cannot obtain the lock before the wait timeout.</summary>
public sealed class LockAcquisitionTimeoutException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public LockAcquisitionTimeoutException(string key, TimeSpan elapsed)
        : base($"Could not acquire distributed lock '{key}' within {elapsed}.")
    {
        Key = key;
        Elapsed = elapsed;
    }

    /// <summary>The lock key.</summary>
    public string Key { get; }

    /// <summary>How long the acquire waited before giving up.</summary>
    public TimeSpan Elapsed { get; }
}

/// <summary>Thrown when an operation requires a held lease that is no longer owned.</summary>
public sealed class LeaseLostException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public LeaseLostException(string key)
        : base($"The lease for distributed lock '{key}' was lost.")
    {
        Key = key;
    }

    /// <summary>The lock key.</summary>
    public string Key { get; }
}

/// <summary>
/// Thrown when the distributed lock backend reports a non-contention failure
/// (e.g. <c>sp_getapplock</c> deadlock victim, parameter validation error, or other
/// backend-specific error code that is not a simple "lock not available" outcome).
/// </summary>
public sealed class OrionLockBackendException : Exception
{
    /// <summary>Initializes the exception with a key and a backend-specific reason.</summary>
    public OrionLockBackendException(string key, string reason)
        : base($"OrionLock backend failure for key '{key}': {reason}")
    {
        Key = key;
        Reason = reason;
    }

    /// <summary>Initializes the exception with a key, reason, and inner exception.</summary>
    public OrionLockBackendException(string key, string reason, Exception inner)
        : base($"OrionLock backend failure for key '{key}': {reason}", inner)
    {
        Key = key;
        Reason = reason;
    }

    /// <summary>The lock key that caused the backend failure.</summary>
    public string Key { get; }

    /// <summary>A human-readable reason describing the backend failure.</summary>
    public string Reason { get; }
}
