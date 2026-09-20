using System.Reflection;
using org.apache.zookeeper;

namespace Moongazing.OrionLock.ZooKeeper.Tests;

/// <summary>
/// What the predecessor watch does with each connection state. The distinction it draws is the
/// difference between a waiter that keeps its place in the queue and one that abandons a znode at
/// the head of it.
/// </summary>
/// <remarks>
/// <see cref="WatchedEvent"/> has no public constructor - ZooKeeperNetEx builds them itself - so
/// the events are constructed reflectively. That is the whole cost of testing this without an
/// ensemble, and it is worth paying: the states are exactly where the bug was.
/// </remarks>
public sealed class ZooKeeperWatchStateTests
{
    [Fact]
    public async Task A_transient_disconnect_does_not_end_the_wait()
    {
        // Disconnected does NOT expire the session. The client is reconnecting, our ephemeral node
        // is still in the queue, and the client re-registers outstanding watches on reconnect - so
        // the watch will still fire. Ending the wait here made the provider give up and
        // best-effort-delete its node, a delete very likely to fail WHILE disconnected and silently
        // swallowed when it did. The node was then orphaned for the life of the session, and every
        // retry afterwards queued a newer node BEHIND it: the key was blocked for this waiter and
        // every later one until the process died.
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new DefaultZooKeeperClientAdapter.NodeDeletedWatcher(waiting);

        await watcher.process(Event(Watcher.Event.EventType.None, Watcher.Event.KeeperState.Disconnected, path: null));

        Assert.False(waiting.Task.IsCompleted);
    }

    [Fact]
    public async Task A_waiter_that_rode_out_a_disconnect_still_sees_the_delete()
    {
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new DefaultZooKeeperClientAdapter.NodeDeletedWatcher(waiting);

        await watcher.process(Event(Watcher.Event.EventType.None, Watcher.Event.KeeperState.Disconnected, path: null));
        await watcher.process(Event(Watcher.Event.EventType.None, Watcher.Event.KeeperState.SyncConnected, path: null));
        await watcher.process(Event(
            Watcher.Event.EventType.NodeDeleted, Watcher.Event.KeeperState.SyncConnected, "/orionlock/k/lock-0000000001"));

        Assert.True(await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_expired_session_does_end_the_wait()
    {
        // Expired is the one that is genuinely terminal: the session is gone, so our ephemeral node
        // went with it and no watch of ours will ever fire again. Waiting on would hang until the
        // budget ran out.
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new DefaultZooKeeperClientAdapter.NodeDeletedWatcher(waiting);

        await watcher.process(Event(Watcher.Event.EventType.None, Watcher.Event.KeeperState.Expired, path: null));

        Assert.False(await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_delete_is_the_only_event_that_reports_the_node_gone()
    {
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new DefaultZooKeeperClientAdapter.NodeDeletedWatcher(waiting);

        // A data change on the predecessor is not a release; neither is a child of it appearing.
        await watcher.process(Event(
            Watcher.Event.EventType.NodeDataChanged, Watcher.Event.KeeperState.SyncConnected, "/orionlock/k/lock-1"));
        await watcher.process(Event(
            Watcher.Event.EventType.NodeChildrenChanged, Watcher.Event.KeeperState.SyncConnected, "/orionlock/k/lock-1"));

        Assert.False(waiting.Task.IsCompleted);
    }

    private static WatchedEvent Event(Watcher.Event.EventType type, Watcher.Event.KeeperState state, string? path)
        => (WatchedEvent)Activator.CreateInstance(
            typeof(WatchedEvent),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [type, state, path],
            culture: null)!;
}
