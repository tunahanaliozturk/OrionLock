using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class InMemorySharedExclusiveLockProviderTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ManySharedHolders_Coexist()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        for (var i = 0; i < 50; i++)
        {
            Assert.True(await p.TryAcquireAsync("k", $"reader-{i}", LockMode.Shared, Lease, default));
        }
    }

    [Fact]
    public async Task Exclusive_Fails_WhileSharedHeld()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Exclusive_Succeeds_AfterAllSharedDrain()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);
        await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, Lease, default);
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync("k", "reader-1", LockMode.Shared, default);
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        await p.ReleaseAsync("k", "reader-2", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Shared_Fails_WhileExclusiveHeld()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default);
        Assert.False(await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task Shared_Succeeds_AfterExclusiveReleased()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default);
        await p.ReleaseAsync("k", "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task SecondExclusive_Fails_WhileExclusiveHeld()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default);
        Assert.False(await p.TryAcquireAsync("k", "writer-2", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Shared_Succeeds_AfterExclusiveLeaseExpires()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(50), default);
        await Task.Delay(120);
        Assert.True(await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task Exclusive_Succeeds_AfterSharedLeasesExpire()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(50), default);
        await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, TimeSpan.FromMilliseconds(50), default);
        await Task.Delay(120);
        Assert.True(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Renew_Shared_OnlyForHolder()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);
        Assert.True(await p.TryRenewAsync("k", "reader-1", LockMode.Shared, Lease, default));
        Assert.False(await p.TryRenewAsync("k", "reader-2", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task Renew_Exclusive_OnlyForHolder()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default);
        Assert.True(await p.TryRenewAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryRenewAsync("k", "writer-2", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Renew_Shared_KeepsHolderAlivePastOriginalLease()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, TimeSpan.FromMilliseconds(80), default);
        await Task.Delay(40);
        Assert.True(await p.TryRenewAsync("k", "reader-1", LockMode.Shared, TimeSpan.FromSeconds(30), default));
        await Task.Delay(80);
        // Original lease would have lapsed by now; renewal kept it alive so a writer is still blocked.
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Release_Shared_OnlyRemovesThatHolder()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);
        await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, Lease, default);
        await p.ReleaseAsync("k", "reader-1", LockMode.Shared, default);
        // reader-2 still holds, so a writer is still blocked.
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task Release_Exclusive_WrongOwner_IsNoOp()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default);
        await p.ReleaseAsync("k", "writer-2", LockMode.Exclusive, default);   // wrong owner, no-op
        Assert.False(await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default));
        await p.ReleaseAsync("k", "writer-1", LockMode.Exclusive, default);   // real owner
        Assert.True(await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task PendingWriter_BlocksNewReaders_NoWriterStarvation()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);

        // Writer fails (reader present) but now reserves the key.
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        // A NEW reader is now refused so the existing reader can drain - the writer is not starved.
        Assert.False(await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, Lease, default));

        // Existing reader drains; writer then wins.
        await p.ReleaseAsync("k", "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
    }

    [Fact]
    public async Task ExistingReader_MayRefreshOwnLease_WhileWriterPending()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        // The reader the writer is waiting on is allowed to refresh its own shared lease.
        Assert.True(await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task PendingWriterReservation_Expires_SoReadersAreNotBlockedForever()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);

        // Writer reserves with a very short lease, then abandons (never retries).
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, TimeSpan.FromMilliseconds(50), default));

        // New readers are briefly held off...
        Assert.False(await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, Lease, default));

        await Task.Delay(120);

        // ...but the reservation lapses, so new readers can proceed again (writer crashed/gave up).
        Assert.True(await p.TryAcquireAsync("k", "reader-3", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task GrantingExclusive_ClearsAnotherWritersPendingReservation()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);

        // writer-1 fails (reader present) and reserves the key.
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        // Reader drains. A DIFFERENT writer (writer-2) now wins the exclusive hold.
        await p.ReleaseAsync("k", "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync("k", "writer-2", LockMode.Exclusive, Lease, default));

        // writer-2 releases. No stale writer-1 reservation must survive to deny a new reader.
        await p.ReleaseAsync("k", "writer-2", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task ContendedExclusive_AfterRelease_AllowsImmediateSharedReacquire()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        await p.TryAcquireAsync("k", "reader-1", LockMode.Shared, Lease, default);

        // Same writer retries twice (stable owner token), reserving the key each attempt.
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));
        Assert.False(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        // Reader drains; the retrying writer wins and clears its own reservation.
        await p.ReleaseAsync("k", "reader-1", LockMode.Shared, default);
        Assert.True(await p.TryAcquireAsync("k", "writer-1", LockMode.Exclusive, Lease, default));

        // After the writer releases, a new reader must succeed immediately - no stale reservation.
        await p.ReleaseAsync("k", "writer-1", LockMode.Exclusive, default);
        Assert.True(await p.TryAcquireAsync("k", "reader-2", LockMode.Shared, Lease, default));
    }

    [Fact]
    public async Task ConcurrentSharedAcquires_AllSucceed()
    {
        var p = new InMemorySharedExclusiveLockProvider();
        var tasks = Enumerable.Range(0, 64)
            .Select(i => p.TryAcquireAsync("k", $"reader-{i}", LockMode.Shared, Lease, default))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }
}
