using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.SqlServer;

/// <summary>
/// SQL Server <c>sp_getapplock</c> backed <see cref="IDistributedLockProvider"/>. Holds one
/// dedicated <see cref="SqlConnection"/> per active lock — the lock lifetime IS the SQL session
/// lifetime, so a crashed process releases its locks automatically.
/// </summary>
public sealed class SqlServerLockProvider : IDistributedLockProvider, IDisposable
{
    private const int MaxResourceLength = 240;

    private readonly string connectionString;
    private readonly SqlServerLockOptions options;
    private readonly ConcurrentDictionary<string, SqlConnection> sessions = new();

    /// <summary>Creates the provider.</summary>
    public SqlServerLockProvider(string connectionString, SqlServerLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        this.connectionString = connectionString;
        this.options = options;
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        throw new NotImplementedException("TryAcquireAsync — implemented in Task 5.");
    }

    /// <inheritdoc />
    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => throw new NotImplementedException("TryRenewAsync — implemented in Task 6.");

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        => throw new NotImplementedException("ReleaseAsync — implemented in Task 8.");

    /// <inheritdoc />
    public void Dispose() { /* implemented in Task 9 */ }

    private void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var total = options.KeyPrefix.Length + key.Length;
        if (total > MaxResourceLength)
        {
            throw new ArgumentException(
                $"Lock key (with prefix) is {total} characters; SQL Server sp_getapplock @Resource " +
                $"is limited to ~{MaxResourceLength} characters. Hash long keys on the caller side or " +
                "shorten the prefix.", nameof(key));
        }
    }

    // Test-only accessor used by SqlServerLockProviderTests (InternalsVisibleTo).
    internal SqlConnection? GetSessionForTesting(string ownerToken)
        => sessions.TryGetValue(ownerToken, out var c) ? c : null;
}
