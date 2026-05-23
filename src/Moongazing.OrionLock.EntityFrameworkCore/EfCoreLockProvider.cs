using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// EF Core lock-table <see cref="IDistributedLockProvider"/>. Each call resolves a scoped
/// <see cref="DbContext"/> and runs provider-agnostic SQL against the <c>OrionLock_Locks</c> table.
/// </summary>
public sealed class EfCoreLockProvider : IDistributedLockProvider
{
    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>Creates the provider. A scoped <see cref="DbContext"/> is resolved per call.</summary>
    public EfCoreLockProvider(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now + leaseDuration;

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        // Take a free or expired row.
        var updated = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE OrionLock_Locks
                  SET OwnerToken = {ownerToken}, ExpiresOnUtc = {expires}
                WHERE Key = {key} AND (OwnerToken IS NULL OR ExpiresOnUtc <= {now})",
            cancellationToken).ConfigureAwait(false);

        if (updated == 0)
        {
            // First-ever use of this key: insert if absent.
            try
            {
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO OrionLock_Locks (Key, OwnerToken, ExpiresOnUtc)
                       SELECT {key}, {ownerToken}, {expires}
                       WHERE NOT EXISTS (SELECT 1 FROM OrionLock_Locks WHERE Key = {key})",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                return false;
            }
        }

        // Owner-check: did this caller win?
        var owner = await ctx.Set<OrionLockRow>().AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.OwnerToken)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return owner == ownerToken;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var expires = DateTime.UtcNow + leaseDuration;
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        var updated = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE OrionLock_Locks
                  SET ExpiresOnUtc = {expires}
                WHERE Key = {key} AND OwnerToken = {ownerToken}",
            cancellationToken).ConfigureAwait(false);

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE OrionLock_Locks
                  SET OwnerToken = NULL, ExpiresOnUtc = {now}
                WHERE Key = {key} AND OwnerToken = {ownerToken}",
            cancellationToken).ConfigureAwait(false);
    }
}
