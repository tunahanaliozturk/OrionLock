using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DiagnosticsTests
{
    [Fact]
    public async Task Acquire_ShouldEmitActivity_OnTheOrionLockSource()
    {
        var activities = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionLockDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var locker = new DistributedLock(new InMemoryLockProvider());
        await using (await locker.AcquireAsync("k", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) })) { }

        Assert.Contains(activities, a => a.DisplayName.Contains('k'));
    }
}
