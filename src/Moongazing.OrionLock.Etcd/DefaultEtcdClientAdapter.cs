namespace Moongazing.OrionLock.Etcd;

using global::dotnet_etcd.interfaces;
using Etcdserverpb;
using Mvccpb;

/// <summary>
/// Default <see cref="IEtcdClientAdapter"/> over the official <c>dotnet-etcd</c> client.
/// Production wiring; unit tests substitute their own adapter.
/// </summary>
public sealed class DefaultEtcdClientAdapter : IEtcdClientAdapter, IEtcdFencingAdapter
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
    /// <remarks>
    /// <para>
    /// Single-shot keep-alive ping over dotnet-etcd's <c>LeaseKeepAlive</c> helper, which takes a request
    /// plus a per-response callback and a cancellation token. etcd's contract: a response with
    /// <c>TTL &gt; 0</c> means the lease was refreshed for that many seconds; <c>TTL == 0</c> means the
    /// lease is gone.
    /// </para>
    /// <para>
    /// <b>Only the server decides.</b> The <see langword="false"/> this method returns is a CONFIRMED
    /// lease loss - the caller's handle trips its lost-token and revokes the lease while the application
    /// may still be inside its critical section - so it is returned ONLY when etcd actually said so
    /// (<c>TTL == 0</c>, or a gRPC <c>NotFound</c>). The previous implementation resolved its
    /// <see cref="TaskCompletionSource{TResult}"/> to <see langword="false"/> the moment the keep-alive
    /// call returned, racing its own response callback: whichever ran first won, so a SUCCESSFUL renewal
    /// could be reported as a lost lease. Now the callback stops the stream as soon as it has an answer,
    /// which orders the two, and a stream that ends with no response at all is treated as what it is - a
    /// transient backend fault - and throws, so the core watchdog retries under its grace period instead
    /// of surrendering a lease that is very likely still held.
    /// </para>
    /// </remarks>
    public async Task<bool> LeaseKeepAliveAsync(long leaseId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnResponse(LeaseKeepAliveResponse response)
        {
            // First response is the answer; stop the stream so the call returns promptly and cannot
            // resolve the outcome behind this callback's back.
            if (!tcs.TrySetResult(response.TTL > 0))
            {
                return;
            }

            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The call already returned and the linked source is gone; the answer is recorded, which
                // is all that matters.
            }
        }

        try
        {
            await client.LeaseKeepAlive(
                new[] { new LeaseKeepAliveRequest { ID = leaseId } },
                new Action<LeaseKeepAliveResponse>[] { OnResponse },
                stop.Token).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException rpc) when (rpc.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // gRPC NotFound is the conventional "lease not found" mapping from etcd: a confirmed
            // lease-loss the OrionLock watchdog should act on.
            return false;
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            // Cancellation of the keep-alive stream is NEVER by itself an outcome, so it is swallowed
            // here and the checks below decide what actually happened. Both shapes have to be caught: a
            // cancelled Grpc.Core response stream can complete by throwing OperationCanceledException OR
            // an RpcException carrying StatusCode.Cancelled, and the cancellation is usually OUR OWN -
            // OnResponse stops the stream the moment it has the answer. Letting the RpcException
            // propagate would turn every successful renewal into a transient backend failure, and the
            // handle's renewal loop would count those until the grace period ran out and declared the
            // lease lost: the exact failure this method was fixed to prevent, re-entering through the fix.
        }

        if (tcs.Task.IsCompletedSuccessfully)
        {
            // The server answered before the stream stopped, so the answer stands however the stream
            // ended.
            return tcs.Task.Result;
        }

        // The caller gave up; surface that rather than dressing it as a backend fault.
        cancellationToken.ThrowIfCancellationRequested();

        throw new InvalidOperationException(
            $"etcd closed the keep-alive stream for lease {leaseId} without sending a response, so the "
            + "lease could neither be confirmed refreshed nor confirmed lost. Treated as a transient "
            + "backend fault: the caller retries the renew instead of surrendering a lease that is "
            + "probably still held.");

        // Other exceptions (transient gRPC errors, caller cancellation) bubble up for the same reason.
    }

    // Every way a cancelled keep-alive stream can surface. Grpc.Core reports cancellation as an
    // RpcException with StatusCode.Cancelled at least as often as it throws OperationCanceledException,
    // and which one arrives depends on where in the call the cancellation landed - so neither shape may
    // be treated as an outcome on its own.
    private static bool IsCancellation(Exception ex)
        => ex is OperationCanceledException
            || (ex is Grpc.Core.RpcException rpc && rpc.StatusCode == Grpc.Core.StatusCode.Cancelled);

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
        => (await KvPutIfAbsentFencedAsync(key, value, leaseId, cancellationToken).ConfigureAwait(false))
            .Acquired;

    /// <inheritdoc />
    /// <remarks>
    /// The revision comes from the transaction's OWN response header, so it is the revision this put
    /// committed at - not one read back afterwards, which could already have moved on.
    /// </remarks>
    public async Task<Moongazing.OrionLock.Providers.LockAcquisition> KvPutIfAbsentFencedAsync(
        string key, string value, long leaseId, CancellationToken cancellationToken)
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
        if (!response.Succeeded)
        {
            return Moongazing.OrionLock.Providers.LockAcquisition.NotAcquired;
        }

        // A real etcd response always carries a header. If one somehow does not, we still took the lock -
        // report it held with no token rather than losing a lock we are holding.
        return response.Header is { } header
            ? Moongazing.OrionLock.Providers.LockAcquisition.Fenced(header.Revision)
            : Moongazing.OrionLock.Providers.LockAcquisition.Unfenced;
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
