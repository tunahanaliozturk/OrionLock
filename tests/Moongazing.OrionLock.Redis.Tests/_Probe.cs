using System.Diagnostics;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Redis;
using Moq;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

public sealed class Probe
{
    [Fact]
    public async Task Measure()
    {
        var lines = new List<string>();
        lines.Add($"PROBE cores={Environment.ProcessorCount}");
        for (var i = 0; i < 25; i++)
        {
            var db = new Mock<IDatabase>();
            var attempts = 0;
            var stamps = new List<long>();
            var sw = Stopwatch.StartNew();
            db.Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                    It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(() => { Interlocked.Increment(ref attempts); lock (stamps) { stamps.Add(sw.ElapsedMilliseconds); } return false; });
            db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((TimeSpan?)null);

            var sub = new Mock<ISubscriber>();
            sub.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>())).Returns(Task.CompletedTask);
            sub.Setup(s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>())).Returns(Task.CompletedTask);
            var mux = new Mock<IConnectionMultiplexer>();
            mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
            mux.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(sub.Object);

            var sut = new RedisLockProvider(mux.Object, new RedisLockOptions());
            await sut.WaitForAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(300), new LockWaitPolicy(TimeSpan.FromMilliseconds(50)), default);
            sw.Stop();
            lines.Add($"PROBE i={i} attempts={attempts} elapsed={sw.ElapsedMilliseconds} stamps=[{string.Join(",", stamps)}]");
        }
        Assert.Fail(string.Join("\n", lines));
    }
}
