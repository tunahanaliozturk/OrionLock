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
        // Single-shot keep-alive ping: dotnet-etcd 7.x's high-level LeaseKeepAlive helper
        // takes a request + a result-collecting callback and a cancellation token. The
        // callback fires for each refresh response; etcd's documented contract is that any
        // response with TTL > 0 means the lease was refreshed for that many seconds.
        // TTL == 0 means the lease expired and the server is reporting lease-lost. We use
        // an asynchronous TaskCompletionSource so the first response (or the stream's
        // completion without a response) unblocks the await without holding the keep-alive
        // call open forever.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<LeaseKeepAliveResponse> onResponse = r =>
        {
            // First response wins; subsequent refreshes (if any) are no-ops on the TCS.
            tcs.TrySetResult(r.TTL > 0);
        };
        try
        {
            await client.LeaseKeepAlive(
                new[] { new LeaseKeepAliveRequest { ID = leaseId } },
                new[] { onResponse },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException rpc) when (rpc.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // gRPC NotFound is the conventional "lease not found" mapping from etcd; treat
            // as confirmed lease-loss so the OrionLock watchdog can react.
            return false;
        }
        // The keep-alive call returned without throwing, but the callback fires
        // asynchronously. Resolve the TCS with the captured outcome, or fall back to
        // "false" when the server completed the stream without writing any response.
        tcs.TrySetResult(false);
        return await tcs.Task.ConfigureAwait(false);
        // Other exceptions (transient gRPC errors, cancellation) bubble up so the caller's
        // core watchdog retries the renew instead of misclassifying a temporary backend
        // fault as confirmed lease loss.
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
