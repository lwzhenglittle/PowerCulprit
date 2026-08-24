using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class ProcessResourceCollectorTests
{
    private static readonly DateTime BaseTimeUtc = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsSameProcessForDelta_DifferentCreateTimes_ReturnsFalse()
    {
        // PID reuse between snapshots: the cumulative CPU/IO counters must not
        // be diffed, or the exited process's history leaks into the new sample.
        var previous = BaseTimeUtc;
        var current = BaseTimeUtc.AddMinutes(3);

        Assert.False(ProcessResourceCollector.IsSameProcessForDelta(previous, current));
    }

    [Fact]
    public void IsSameProcessForDelta_SameCreateTime_ReturnsTrue()
    {
        Assert.True(ProcessResourceCollector.IsSameProcessForDelta(BaseTimeUtc, BaseTimeUtc));
    }

    [Fact]
    public void IsSameProcessForDelta_PreviousCreateTimeUnknown_KeepsPidOnlyMatching()
    {
        Assert.True(ProcessResourceCollector.IsSameProcessForDelta(null, BaseTimeUtc));
    }

    [Fact]
    public void IsSameProcessForDelta_CurrentCreateTimeUnknown_KeepsPidOnlyMatching()
    {
        Assert.True(ProcessResourceCollector.IsSameProcessForDelta(BaseTimeUtc, null));
    }

    [Fact]
    public void IsSameProcessForDelta_BothCreateTimesUnknown_KeepsPidOnlyMatching()
    {
        Assert.True(ProcessResourceCollector.IsSameProcessForDelta(null, null));
    }
}
