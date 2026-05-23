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
