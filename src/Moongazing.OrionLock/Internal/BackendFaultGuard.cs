using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// Wraps an inner <see cref="IDistributedLockProvider"/> so a raw driver failure reaches the caller as
/// <see cref="OrionLockBackendException"/> instead of as whatever the backend's client library happens
/// to throw.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDistributedLock"/> documented one exception and could raise a dozen: a
/// <c>SqlException</c> from SQL Server, a <c>PostgresException</c> from PostgreSQL, an
/// <c>RpcException</c> from etcd, a <c>KeeperException</c> from ZooKeeper, a <c>RedisException</c> from
/// Redis, a <c>DbException</c> through EF Core, an HTTP failure from Consul. Catching backend trouble
/// therefore meant referencing every backend's driver package and writing a different <c>catch</c> per
/// registration, which defeats the point of a backend-agnostic interface. SQL Server already modelled
/// the right shape by raising <see cref="OrionLockBackendException"/> for an unexpected
/// <c>sp_getapplock</c> return code; this applies that shape to all of them.
/// </para>
/// <para>
/// The rule is provider-agnostic, so it lives here once rather than as seven copies that would drift:
/// anything that is not already part of OrionLock's own contract is a backend fault. What passes
/// through untouched is exactly that contract - <see cref="OperationCanceledException"/> (cancellation
/// is control flow, not a failure), <see cref="OrionLockBackendException"/> (already wrapped, including
/// SQL Server's own), <see cref="ArgumentException"/> and friends (the caller's mistake, not the
/// backend's), <see cref="InvalidOperationException"/> (an OrionLock invariant, such as the ownerToken
/// collision SQL Server and PostgreSQL detect) and <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
internal sealed class BackendFaultGuard : IDistributedLockProvider
{
    private readonly IDistributedLockProvider inner;

    public BackendFaultGuard(IDistributedLockProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;

    public TimeSpan MinimumLeaseDuration => inner.MinimumLeaseDuration;

    public TimeSpan EffectiveLeaseDuration(TimeSpan requested) => inner.EffectiveLeaseDuration(requested);

    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => GuardAsync(key, "acquire", () => inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken));

    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => GuardAsync(key, "renew", () => inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken));

    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        => GuardAsync(key, "release", async () =>
        {
            await inner.ReleaseAsync(key, ownerToken, cancellationToken).ConfigureAwait(false);
            return true;
        });

    private static async Task<T> GuardAsync<T>(string key, string operation, Func<Task<T>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw new OrionLockBackendException(key, $"{operation} failed: {ex.Message}", ex);
        }
    }

    // The exception filter runs before any stack unwinding, so a pass-through exception keeps its
    // original stack trace exactly as if this decorator were not here.
    private static bool IsBackendFault(Exception ex)
        => ex is not OperationCanceledException
            and not OrionLockBackendException
            and not ArgumentException
            and not InvalidOperationException
            and not ObjectDisposedException;
}
