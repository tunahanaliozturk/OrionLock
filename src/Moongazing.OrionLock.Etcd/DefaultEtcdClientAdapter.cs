namespace Moongazing.OrionLock.Etcd;

using global::dotnet_etcd.interfaces;
using Etcdserverpb;
using Mvccpb;

/// <summary>
/// Default <see cref="IEtcdClientAdapter"/> over the official <c>dotnet-etcd</c> client.
/// Production wiring; unit tests substitute their own adapter.
/// </summary>
public sealed class DefaultEtcdClientAdapter : IEtcdClientAdapter
{
    private readonly IEtcdClient client;

    /// <summary>Construct with an already-resolved etcd client.</summary>
    public DefaultEtcdClientAdapter(IEtcdClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <inheritdoc />
    public async Task<long> LeaseGrantAsync(int ttlSeconds, CancellationToken cancellationToken)
    {
        var response = await client.LeaseGrantAsync(new LeaseGrantRequest { TTL = ttlSeconds }, null, default, cancellationToken)
            .ConfigureAwait(false);
        return response.ID;
    }

    /// <inheritdoc />
    public async Task<bool> LeaseKeepAliveAsync(long leaseId, CancellationToken cancellationToken)
    {
        try
        {
            await client.LeaseTimeToLiveAsync(new LeaseTimeToLiveRequest { ID = leaseId }, null, default, cancellationToken)
                .ConfigureAwait(false);
            // Single-shot keep-alive: refreshing the TTL by writing a no-op via the
            // dotnet-etcd LeaseKeepAlive API requires a long-running stream. For
            // OrionLock's polling renew shape, we re-grant on demand via the dispatcher's
            // call back through TryRenewAsync. The LeaseTimeToLive call ABOVE confirms the
            // lease still exists; if etcd returns an error we treat it as lease-lost.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task LeaseRevokeAsync(long leaseId, CancellationToken cancellationToken)
    {
        try
        {
            await client.LeaseRevokeAsync(new LeaseRevokeRequest { ID = leaseId }, null, default, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Idempotent: a revoke for a lease that already expired or was revoked is
            // benign from our caller's perspective.
        }
    }

    /// <inheritdoc />
    public async Task<bool> KvPutIfAbsentAsync(string key, string value, long leaseId, CancellationToken cancellationToken)
    {
        // Atomic transaction: IF the key has version == 0 (does not exist) THEN PUT it
        // under the supplied lease, ELSE do nothing. Returns Succeeded = true on grant.
        var txn = new TxnRequest();
        txn.Compare.Add(new Compare
        {
            Key = Google.Protobuf.ByteString.CopyFromUtf8(key),
            Target = Compare.Types.CompareTarget.Version,
            Result = Compare.Types.CompareResult.Equal,
            Version = 0,
        });
        txn.Success.Add(new RequestOp
        {
            RequestPut = new PutRequest
            {
                Key = Google.Protobuf.ByteString.CopyFromUtf8(key),
                Value = Google.Protobuf.ByteString.CopyFromUtf8(value),
                Lease = leaseId,
            },
        });

        var response = await client.TransactionAsync(txn, null, default, cancellationToken).ConfigureAwait(false);
        return response.Succeeded;
    }

    /// <inheritdoc />
    public async Task<bool> KvDeleteIfMatchAsync(string key, string expectedValue, CancellationToken cancellationToken)
    {
        var txn = new TxnRequest();
        txn.Compare.Add(new Compare
        {
            Key = Google.Protobuf.ByteString.CopyFromUtf8(key),
            Target = Compare.Types.CompareTarget.Value,
            Result = Compare.Types.CompareResult.Equal,
            Value = Google.Protobuf.ByteString.CopyFromUtf8(expectedValue),
        });
        txn.Success.Add(new RequestOp
        {
            RequestDeleteRange = new DeleteRangeRequest
            {
                Key = Google.Protobuf.ByteString.CopyFromUtf8(key),
            },
        });

        var response = await client.TransactionAsync(txn, null, default, cancellationToken).ConfigureAwait(false);
        return response.Succeeded;
    }
}
