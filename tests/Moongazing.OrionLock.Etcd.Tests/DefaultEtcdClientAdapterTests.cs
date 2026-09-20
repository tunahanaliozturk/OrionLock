using dotnet_etcd.interfaces;
using Etcdserverpb;
using Grpc.Core;
using Moongazing.OrionLock.Etcd;
using Moq;

namespace Moongazing.OrionLock.Etcd.Tests;

/// <summary>
/// Coverage for the PRODUCTION adapter's own logic. Every other etcd test substitutes
/// <see cref="IEtcdClientAdapter"/>, so nothing exercised this class at all - which is how a keep-alive
/// that resolved its result in a race with its own response callback survived. The etcd client is
/// substituted here, and each test drives the callback/stream timing by hand so the orderings that matter
/// are deterministic rather than a coin flip.
/// </summary>
public sealed class DefaultEtcdClientAdapterTests
{
    private const long LeaseId = 7;

    [Fact]
    public async Task KeepAlive_ReportsTheRefresh_AndStopsTheStreamOnceTheServerHasAnswered()
    {
        // The real client holds the keep-alive stream open until it is told to stop, so this fake only
        // returns when its token is cancelled. A renewal that does not stop the stream therefore HANGS
        // here rather than quietly passing - which is the pre-fix behaviour, since it passed the caller's
        // token straight through and nothing ever cancelled it.
        var client = NewClient(async (methods, ct) =>
        {
            Respond(methods, ttl: 30);
            await UntilCancelled(ct);
        });

        var sut = new DefaultEtcdClientAdapter(client);

        var renewed = await sut.LeaseKeepAliveAsync(LeaseId, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(renewed);
    }

    [Fact]
    public async Task KeepAlive_ReportsTheRefresh_WhenTheStoppedStreamFaultsWithRpcCancelled()
    {
        // Grpc.Core reports a cancelled response stream as an RpcException carrying StatusCode.Cancelled
        // at least as often as it throws OperationCanceledException - and the cancellation here is OUR
        // OWN, the adapter stopping the stream the moment it had the answer. Letting that propagate turns
        // every successful renewal into a transient backend failure, which the handle's renewal loop
        // counts until the grace period runs out and declares the lease lost. That is the very failure
        // this method exists to prevent, so the answer must win over how the stream happened to end.
        var client = NewClient((methods, ct) =>
        {
            Respond(methods, ttl: 30);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellationBecomes: () => new RpcException(new Status(StatusCode.Cancelled, "Cancelled")));

        var sut = new DefaultEtcdClientAdapter(client);

        Assert.True(await sut.LeaseKeepAliveAsync(LeaseId, default).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task KeepAlive_ReportsLeaseLost_WhenTheStoppedStreamFaultsWithRpcCancelled_AfterTtlZero()
    {
        // Same ordering the other way round: an answer of "the lease is gone" must also survive the
        // stream ending as a gRPC cancellation.
        var client = NewClient((methods, ct) =>
        {
            Respond(methods, ttl: 0);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellationBecomes: () => new RpcException(new Status(StatusCode.Cancelled, "Cancelled")));

        var sut = new DefaultEtcdClientAdapter(client);

        Assert.False(await sut.LeaseKeepAliveAsync(LeaseId, default).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task KeepAlive_StillReportsCallerCancellation_WhenItArrivesAsRpcCancelled()
    {
        // No answer arrived, so gRPC cancellation is not an outcome to report - the caller gave up, and
        // that is what the caller should see, not a backend fault and certainly not a lost lease.
        var client = NewClient((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellationBecomes: () => new RpcException(new Status(StatusCode.Cancelled, "Cancelled")));
        var sut = new DefaultEtcdClientAdapter(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.LeaseKeepAliveAsync(LeaseId, cts.Token));
    }

    [Fact]
    public async Task KeepAlive_ReportsLeaseLost_WhenTheServerAnswersWithTtlZero()
    {
        var client = NewClient(async (methods, ct) =>
        {
            Respond(methods, ttl: 0);
            await UntilCancelled(ct);
        });

        var sut = new DefaultEtcdClientAdapter(client);

        Assert.False(await sut.LeaseKeepAliveAsync(LeaseId, default).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task KeepAlive_ReportsLeaseLost_OnGrpcNotFound()
    {
        var client = NewClient((_, _) =>
            Task.FromException(new RpcException(new Status(StatusCode.NotFound, "lease not found"))));

        var sut = new DefaultEtcdClientAdapter(client);

        Assert.False(await sut.LeaseKeepAliveAsync(LeaseId, default));
    }

    [Fact]
    public async Task KeepAlive_DoesNotConfirmLeaseLoss_WhenTheResponseArrivesAfterTheCallReturns()
    {
        // The defect, reproduced deterministically: the stream completes and the response callback fires
        // afterwards. The pre-fix adapter resolved its result to `false` the instant the call returned, so
        // this renewal was reported as a CONFIRMED lease loss - the handle trips its lost-token and
        // revokes the lease while the caller is still inside its critical section. Not knowing must never
        // be reported as knowing the lease is gone; an unanswered stream is a transient fault.
        var gate = new TaskCompletionSource();
        var stray = new TaskCompletionSource();
        var client = NewClient((methods, _) =>
        {
            var late = Task.Run(async () =>
            {
                await gate.Task;
                Respond(methods, ttl: 30);
                stray.TrySetResult();
            }, CancellationToken.None);
            Assert.NotNull(late);
            return Task.CompletedTask;
        });

        var sut = new DefaultEtcdClientAdapter(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.LeaseKeepAliveAsync(LeaseId, default));

        // Let the late callback run against the now-finished call: it must not blow up a pool thread.
        gate.SetResult();
        await stray.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task KeepAlive_TreatsAnUnansweredStreamAsTransient_NotAsAConfirmedLoss()
    {
        var client = NewClient((_, _) => Task.CompletedTask);

        var sut = new DefaultEtcdClientAdapter(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.LeaseKeepAliveAsync(LeaseId, default));
    }

    [Fact]
    public async Task KeepAlive_PropagatesCallerCancellation()
    {
        var client = NewClient(async (_, ct) => await UntilCancelled(ct));
        var sut = new DefaultEtcdClientAdapter(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.LeaseKeepAliveAsync(LeaseId, cts.Token));
    }

    [Fact]
    public async Task LeaseGrant_ReturnsTheServersLeaseId()
    {
        var client = new Mock<IEtcdClient>();
        client.Setup(c => c.LeaseGrantAsync(
                It.IsAny<LeaseGrantRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseGrantResponse { ID = 42 });

        Assert.Equal(42, await new DefaultEtcdClientAdapter(client.Object).LeaseGrantAsync(30, default));
        client.Verify(c => c.LeaseGrantAsync(
            It.Is<LeaseGrantRequest>(r => r.TTL == 30), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LeaseRevoke_IsIdempotent_AndSwallowsAnAlreadyGoneLease()
    {
        var client = new Mock<IEtcdClient>();
        client.Setup(c => c.LeaseRevokeAsync(
                It.IsAny<LeaseRevokeRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.NotFound, "lease not found")));

        // Revoke is called on cleanup paths that must not fail; a lease that is already gone is a success.
        await new DefaultEtcdClientAdapter(client.Object).LeaseRevokeAsync(LeaseId, default);
    }

    [Fact]
    public async Task KvPutIfAbsent_ComparesOnVersionZero_AndReportsTheTransactionOutcome()
    {
        var client = new Mock<IEtcdClient>();
        TxnRequest? sent = null;
        client.Setup(c => c.TransactionAsync(
                It.IsAny<TxnRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback((TxnRequest r, Metadata _, DateTime? _, CancellationToken _) => sent = r)
            .ReturnsAsync(new TxnResponse { Succeeded = true });

        Assert.True(await new DefaultEtcdClientAdapter(client.Object)
            .KvPutIfAbsentAsync("orionlock/k", "owner-1", LeaseId, default));

        // Put-if-absent is "version == 0", the etcd idiom for "the key does not exist".
        var compare = Assert.Single(sent!.Compare);
        Assert.Equal(Compare.Types.CompareTarget.Version, compare.Target);
        Assert.Equal(Compare.Types.CompareResult.Equal, compare.Result);
        Assert.Equal(0, compare.Version);
        Assert.Equal(LeaseId, Assert.Single(sent.Success).RequestPut.Lease);
    }

    [Fact]
    public async Task KvDeleteIfMatch_ComparesOnTheOwnerToken_SoAnotherOwnersKeyIsNeverDeleted()
    {
        var client = new Mock<IEtcdClient>();
        TxnRequest? sent = null;
        client.Setup(c => c.TransactionAsync(
                It.IsAny<TxnRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback((TxnRequest r, Metadata _, DateTime? _, CancellationToken _) => sent = r)
            .ReturnsAsync(new TxnResponse { Succeeded = false });

        Assert.False(await new DefaultEtcdClientAdapter(client.Object)
            .KvDeleteIfMatchAsync("orionlock/k", "owner-1", default));

        var compare = Assert.Single(sent!.Compare);
        Assert.Equal(Compare.Types.CompareTarget.Value, compare.Target);
        Assert.Equal("owner-1", compare.Value.ToStringUtf8());
    }

    // ---- fakes ----------------------------------------------------------------------------------------

    /// <summary>
    /// An etcd client whose keep-alive behaviour is supplied by the test: it receives the response
    /// callbacks the adapter registered and the token the adapter passed, so each test decides exactly
    /// when - and whether - the server answers, and whether the stream outlives the answer.
    /// </summary>
    private static IEtcdClient NewClient(
        Func<Action<LeaseKeepAliveResponse>[], CancellationToken, Task> stream,
        Func<Exception>? cancellationBecomes = null)
    {
        var client = new Mock<IEtcdClient>();
        client.Setup(c => c.LeaseKeepAlive(
                It.IsAny<LeaseKeepAliveRequest[]>(),
                It.IsAny<Action<LeaseKeepAliveResponse>[]>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>()))
            .Returns((LeaseKeepAliveRequest[] _, Action<LeaseKeepAliveResponse>[] methods, CancellationToken ct, Metadata _, DateTime? _)
                => Run(stream, methods, cancellationBecomes, ct));
        return client.Object;
    }

    // Lets a test choose how the underlying stream REPORTS cancellation. Grpc.Core does not settle on one
    // shape, so the adapter must not either.
    private static async Task Run(
        Func<Action<LeaseKeepAliveResponse>[], CancellationToken, Task> stream,
        Action<LeaseKeepAliveResponse>[] methods,
        Func<Exception>? cancellationBecomes,
        CancellationToken ct)
    {
        try
        {
            await stream(methods, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationBecomes is not null)
        {
            throw cancellationBecomes();
        }
    }

    private static void Respond(Action<LeaseKeepAliveResponse>[] methods, long ttl)
    {
        foreach (var method in methods)
        {
            method(new LeaseKeepAliveResponse { ID = LeaseId, TTL = ttl });
        }
    }

    private static async Task UntilCancelled(CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        using var registration = ct.Register(() => done.TrySetResult());
        await done.Task;
        ct.ThrowIfCancellationRequested();
    }
}
