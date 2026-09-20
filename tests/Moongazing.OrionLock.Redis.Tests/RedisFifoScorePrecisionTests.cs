using Moongazing.OrionLock.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// A Redis sorted-set score is a DOUBLE, which represents integers exactly only up to 2^53. The FIFO
/// coordinator's tiebreaker packed <c>(unixMs &lt;&lt; 16) | seq</c>, which needs 57 bits at a 2020s
/// epoch: the ULP there is 16, so runs of ~16 same-millisecond waiters quantized onto ONE score and fell
/// back to lexicographic Guid ordering - precisely the unfairness the tiebreaker exists to remove. These
/// tests assert the packing at the layer the defect lives, because quantization is invisible end-to-end
/// until two waiters happen to tie. No Redis server needed.
/// </summary>
public sealed class RedisFifoScorePrecisionTests
{
    private const long ExactIntegerLimit = 1L << 53;
    private static readonly long Now2026 = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void EveryScoreSurvivesTheRoundTripThroughADouble()
    {
        // The whole packed range must be under 2^53, otherwise the score Redis stores is not the score
        // we computed.
        for (var sequence = 0; sequence < 4096; sequence++)
        {
            var packed = RedisFifoWaiterCoordinator.PackScore(Now2026, sequence);
            Assert.True(packed < ExactIntegerLimit, $"score {packed} exceeds a double's exact-integer range");
            Assert.Equal(packed, (long)(double)packed);
        }
    }

    [Fact]
    public void WaitersArrivingInTheSameMillisecond_GetDistinctScores_AsDoubles()
    {
        // The old packing quantized in runs of 16 here. As doubles, so the assertion is about what Redis
        // actually stores rather than what the long said.
        var scores = Enumerable.Range(0, 4096)
            .Select(sequence => (double)RedisFifoWaiterCoordinator.PackScore(Now2026, sequence))
            .ToArray();

        Assert.Equal(scores.Length, scores.Distinct().Count());
    }

    [Fact]
    public void ArrivalMillisecondStillDominatesTheSequence()
    {
        // Fairness across processes rests on this: a waiter that arrived a millisecond later must never
        // sort ahead of one that arrived earlier, whatever their per-process sequence numbers are.
        var lastOfThisMs = (double)RedisFifoWaiterCoordinator.PackScore(Now2026, 4095);
        var firstOfNextMs = (double)RedisFifoWaiterCoordinator.PackScore(Now2026 + 1, 0);

        Assert.True(lastOfThisMs < firstOfNextMs);
    }

    [Fact]
    public void TheSequenceWraps_WithoutEverEscapingItsOwnMillisecond()
    {
        // The counter is process-wide and unbounded, so it wraps into its field. A wrap must not leak
        // into the millisecond bits and reorder waiters across milliseconds.
        var wrapped = RedisFifoWaiterCoordinator.PackScore(Now2026, 4096);
        var zero = RedisFifoWaiterCoordinator.PackScore(Now2026, 0);
        var firstOfNextMs = RedisFifoWaiterCoordinator.PackScore(Now2026 + 1, 0);

        Assert.Equal(zero, wrapped);
        Assert.True(wrapped < firstOfNextMs);
    }

    [Fact]
    public void ThePrecisionBudgetStillHolds_DecadesOut()
    {
        var y2080 = new DateTimeOffset(2080, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var packed = RedisFifoWaiterCoordinator.PackScore(y2080, 4095);

        Assert.True(packed < ExactIntegerLimit, $"score {packed} exceeds a double's exact-integer range");
        Assert.Equal(packed, (long)(double)packed);
    }

    [Fact]
    public void ThePruneCutoff_IsExpressedInTheSamePackingAsTheScores()
    {
        // A raw millisecond cutoff compared against packed scores prunes nothing at all.
        var cutoff = RedisFifoWaiterCoordinator.PackCutoff(Now2026);

        Assert.True(RedisFifoWaiterCoordinator.PackScore(Now2026 - 1, 4095) < cutoff);
        Assert.True(RedisFifoWaiterCoordinator.PackScore(Now2026 + 1, 0) > cutoff);
    }
}
