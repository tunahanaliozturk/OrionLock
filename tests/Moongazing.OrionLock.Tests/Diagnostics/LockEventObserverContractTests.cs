namespace Moongazing.OrionLock.Tests.Diagnostics;

using Moongazing.OrionLock;
using Xunit;

public sealed class LockEventObserverContractTests
{
    [Fact]
    public void NullLockEventObserver_all_methods_complete_without_throwing()
    {
        var sut = new NullLockEventObserver();

        sut.OnAcquired("k", 5.0);
        sut.OnAcquireTimedOut("k", 250.0);
        sut.OnLeaseLost("k");
        sut.OnReleased("k");
    }

    [Fact]
    public void Custom_observer_records_each_lifecycle_event_with_the_supplied_arguments()
    {
        var events = new System.Collections.Generic.List<string>();
        var sut = new CapturingObserver(e => events.Add(e));

        sut.OnAcquired("user:42", 7.5);
        sut.OnAcquireTimedOut("user:42", 502.0);
        sut.OnLeaseLost("user:42");
        sut.OnReleased("user:42");

        Assert.Equal(4, events.Count);
        Assert.Equal("acquired:user:42:7.5", events[0]);
        Assert.Equal("timeout:user:42:502", events[1]);
        Assert.Equal("lost:user:42", events[2]);
        Assert.Equal("released:user:42", events[3]);
    }

    private sealed class CapturingObserver : ILockEventObserver
    {
        private readonly System.Action<string> capture;
        public CapturingObserver(System.Action<string> capture) => this.capture = capture;
        public void OnAcquired(string key, double durationMs) => capture($"acquired:{key}:{durationMs}");
        public void OnAcquireTimedOut(string key, double waitMs) => capture($"timeout:{key}:{waitMs}");
        public void OnLeaseLost(string key) => capture($"lost:{key}");
        public void OnReleased(string key) => capture($"released:{key}");
    }
}
