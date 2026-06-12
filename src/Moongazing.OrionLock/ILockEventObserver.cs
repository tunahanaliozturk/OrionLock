namespace Moongazing.OrionLock;

using System;

/// <summary>
/// v0.3.24 consumer-supplied observer invoked on lock lifecycle events. Useful for
/// application audit trails of distributed lock acquisition (compliance / incident
/// triage) without coupling the audit logic to the load-bearing acquire/release path.
/// </summary>
/// <remarks>
/// <para>
/// All methods fire synchronously inside the lock pipeline. A throwing observer does
/// NOT roll back the lock state; observer exceptions are caught and swallowed so an
/// audit-side outage cannot disrupt the lock path.
/// </para>
/// <para>
/// No observer is registered by default. Consumers wire one via
/// <c>services.AddSingleton&lt;ILockEventObserver, MyObserver&gt;()</c>. The default
/// <see cref="NullLockEventObserver"/> is used when no consumer registration is
/// present; the observer field treats <c>NullLockEventObserver</c> as 'no observer' to
/// skip the call site entirely.
/// </para>
/// </remarks>
public interface ILockEventObserver
{
    /// <summary>Notify the observer of a successful acquire.</summary>
    /// <param name="key">Lock key.</param>
    /// <param name="durationMs">Wall-clock between the AcquireAsync call entry and handle creation.</param>
    void OnAcquired(string key, double durationMs);

    /// <summary>Notify the observer of an acquire timeout (the caller gave up).</summary>
    /// <param name="key">Lock key.</param>
    /// <param name="waitMs">Wall-clock spent waiting before the timeout fired.</param>
    void OnAcquireTimedOut(string key, double waitMs);

    /// <summary>Notify the observer of a lease lost event (backend-confirmed via renewal=false).</summary>
    /// <param name="key">Lock key.</param>
    void OnLeaseLost(string key);

    /// <summary>Notify the observer of a normal release via <c>DisposeAsync</c>.</summary>
    /// <param name="key">Lock key.</param>
    void OnReleased(string key);
}

/// <summary>Default no-op observer used when no consumer-registered observer is present.</summary>
public sealed class NullLockEventObserver : ILockEventObserver
{
    /// <inheritdoc />
    public void OnAcquired(string key, double durationMs) { }

    /// <inheritdoc />
    public void OnAcquireTimedOut(string key, double waitMs) { }

    /// <inheritdoc />
    public void OnLeaseLost(string key) { }

    /// <inheritdoc />
    public void OnReleased(string key) { }
}
