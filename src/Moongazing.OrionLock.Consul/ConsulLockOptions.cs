namespace Moongazing.OrionLock.Consul;

/// <summary>
/// Configuration for the Consul-backed <see cref="ConsulLockProvider"/>.
/// </summary>
public sealed class ConsulLockOptions
{
    /// <summary>
    /// KV-path prefix under which lock keys are stored. Default <c>"orionlock/"</c>; full
    /// key becomes <c>"orionlock/{lockKey}"</c>. Override to namespace multiple OrionLock
    /// consumers sharing one Consul cluster.
    /// </summary>
    public string KeyPrefix { get; set; } = "orionlock/";

    /// <summary>
    /// Behaviour applied when a Consul session expires (e.g. node loss). <c>"release"</c>
    /// drops the lock back to the pool; <c>"delete"</c> wipes the KV key entirely. Default
    /// <c>"release"</c>, which matches the OrionLock contract: a stale lease becomes
    /// available again so blocking waiters can proceed.
    /// </summary>
    public string SessionBehavior { get; set; } = "release";

    /// <summary>
    /// Consul session TTL refresh window above the OrionLock lease duration. Consul rejects
    /// session TTLs shorter than 10 seconds, so the provider takes <c>max(LeaseDuration,
    /// MinSessionTtl)</c> as the actual session TTL and renews on
    /// <c>IDistributedLockProvider.TryRenewAsync</c>. Default 10 seconds, the Consul-enforced
    /// floor.
    /// </summary>
    public TimeSpan MinSessionTtl { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Consul session <c>LockDelay</c>: after a session is INVALIDATED (node loss, TTL expiry), the
    /// locks it held stay unavailable to any other session for this long. Default 5 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the mechanism that makes a Consul session lock safe under partition, not a tuning knob.
    /// Consul invalidating a session does not stop the process that held it: that process finds out only
    /// on its next renewal attempt. With no lock delay the key is free the instant Consul gives up on the
    /// session, so a new holder can enter the critical section while the old one is still inside it.
    /// The delay is the window in which the old holder notices and stands down.
    /// </para>
    /// <para>
    /// <b>It is only long enough if it outlasts that window.</b> With OrionLock's defaults the holder
    /// surrenders when <c>RenewalFailureGracePeriod</c> (default = <c>LeaseDuration</c>, 30s) elapses
    /// without a successful renewal, and Consul invalidates the session when its TTL
    /// (<c>max(LeaseDuration, MinSessionTtl)</c>, also 30s) elapses - the two coincide, so the delay only
    /// has to cover scheduling and clock jitter, which 5 seconds does comfortably. Raise
    /// <c>RenewalFailureGracePeriod</c> above the session TTL and you MUST raise this by the same amount,
    /// or the guarantee is gone.
    /// </para>
    /// <para>
    /// <b>It does not slow down a normal release.</b> <c>ReleaseAsync</c> releases the KV entry before
    /// destroying the session, so the session holds no lock when it is invalidated and the delay never
    /// applies - the key is back in the pool immediately. The delay is paid only on the crash and
    /// partition paths, which is exactly where it is wanted. Consul's own default is 15 seconds; 5 keeps
    /// half of OrionLock's default 10-second <c>WaitTimeout</c> usable even on that path.
    /// </para>
    /// <para>
    /// Set to <see cref="TimeSpan.Zero"/> only if you accept that a partitioned holder and its successor
    /// can overlap.
    /// </para>
    /// </remarks>
    public TimeSpan LockDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether every acquire also reports a fencing token, exposed as
    /// <see cref="IDistributedLockHandle.FencingToken"/>. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is the key's <c>ModifyIndex</c>: Consul's Raft log index, which is cluster-global,
    /// advances on every committed write and is never reset - so it is strictly increasing per key
    /// across acquisitions and processes. See <see cref="IConsulFencingAdapter"/> for why the
    /// obvious-looking <c>LockIndex</c> is NOT usable.
    /// </para>
    /// <para>
    /// <b>It costs a round trip, which is why it is opt-in.</b> Consul's acquire returns a bare
    /// <c>true</c>/<c>false</c> with no index attached, so the provider reads the entry back after a
    /// successful acquire - one extra GET per acquire, paid only by callers that asked for a token. The
    /// read is safe because we already hold the key at that point: nobody else can acquire it, and the
    /// next holder must write again, which necessarily lands above whatever we read.
    /// </para>
    /// <para>
    /// If that read fails, the acquire fails: the provider releases the key and destroys the session
    /// before rethrowing, rather than returning a hold with a silently missing token. A read that comes
    /// back EMPTY counts as a failure too - it means the entry vanished between the acquire and the
    /// read, so the session was invalidated or the key deleted and the lock is not really held; that
    /// surfaces as <see cref="OrionLockBackendException"/>.
    /// </para>
    /// </remarks>
    public bool FencingTokens { get; set; }
}
