using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class GapDetectorTests
{
    private static readonly DateTime Base = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SystemPowerSample MakeSample(DateTime ts, double battery = 80)
        => new() { TimestampUtc = ts, BatteryPercent = battery, IsAcOnline = false };

    private static PowerStateEvent MakeEvent(DateTime ts, PowerStateEventKind kind)
        => new() { TimestampUtc = ts, Kind = kind };

    [Fact]
    public void SuspendResumePair_ProducesConfirmedSleep()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddHours(3), PowerStateEventKind.Resume)
        };

        // Samples far apart → the 5-hour gap creates an UnknownGap which
        // merges with the ConfirmedSleep. ConfirmedSleep takes priority.
        var samples = new List<SystemPowerSample>
        {
            MakeSample(Base),
            MakeSample(Base.AddHours(5))
        };

        var result = GapDetector.Detect(events, samples, Base, Base.AddHours(5));

        Assert.Single(result);
        Assert.Equal(GapKind.ConfirmedSleep, result[0].Kind);
        // Merged interval covers the whole span (sample gap + sleep).
        Assert.Equal(Base, result[0].StartUtc);
        Assert.Equal(Base.AddHours(5), result[0].EndUtc);
    }

    [Fact]
    public void UnpairedSuspend_NoInterval()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Suspend)
        };

        // Suspend-only → no intervals
        var result = GapDetector.DetectSleepOnly(events);

        Assert.Empty(result);
    }

    [Fact]
    public void UnpairedResume_NoInterval()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Resume)
        };

        // Resume-only → no sleep interval
        var result = GapDetector.DetectSleepOnly(events);

        Assert.Empty(result);
    }

    [Fact]
    public void MultipleSleepIntervals_MultipleOutputs()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddHours(2), PowerStateEventKind.Resume),
            MakeEvent(Base.AddHours(4), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddHours(5), PowerStateEventKind.Resume)
        };

        var result = GapDetector.DetectSleepOnly(events);

        Assert.Equal(2, result.Count);
        Assert.All(result, i => Assert.Equal(GapKind.ConfirmedSleep, i.Kind));
    }

    [Fact]
    public void ConsecutiveSuspend_UsesLast()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddMinutes(61), PowerStateEventKind.Suspend), // 1h1m later
            MakeEvent(Base.AddHours(3), PowerStateEventKind.Resume)
        };

        var result = GapDetector.DetectSleepOnly(events);

        Assert.Single(result);
        // Should use the second (last) suspend as the interval start.
        Assert.Equal(Base.AddMinutes(61), result[0].StartUtc);
        Assert.Equal(Base.AddHours(3), result[0].EndUtc);
    }

    [Fact]
    public void LargeSampleGap_NoEvents_ProducesUnknownGap()
    {
        var events = Array.Empty<PowerStateEvent>();

        var samples = new List<SystemPowerSample>
        {
            MakeSample(Base),
            // Gap > 10 minutes → UnknownGap
            MakeSample(Base.AddMinutes(15))
        };

        var result = GapDetector.Detect(events, samples, Base, Base.AddMinutes(30));

        Assert.Single(result);
        Assert.Equal(GapKind.UnknownGap, result[0].Kind);
    }

    [Fact]
    public void LargeSampleGap_CoveredBySleep_NotDuplicated()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddMinutes(1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddMinutes(14), PowerStateEventKind.Resume)
        };

        // Sample gap from t=0 to t=15min — covered by suspend→resume at 1→14min.
        var samples = new List<SystemPowerSample>
        {
            MakeSample(Base),
            MakeSample(Base.AddMinutes(15))
        };

        var result = GapDetector.Detect(events, samples, Base, Base.AddMinutes(30));

        // Should have only one interval (ConfirmedSleep from events, covering the gap).
        Assert.Single(result);
        Assert.Equal(GapKind.ConfirmedSleep, result[0].Kind);
    }

    [Fact]
    public void ResumeAutomatic_ProducesConfirmedSleep()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddHours(2), PowerStateEventKind.ResumeAutomatic)
        };

        var result = GapDetector.DetectSleepOnly(events);

        Assert.Single(result);
        Assert.Equal(GapKind.ConfirmedSleep, result[0].Kind);
    }

    [Fact]
    public void Detect_OnlyEventsInTimeRange()
    {
        var events = new List<PowerStateEvent>
        {
            MakeEvent(Base.AddHours(-1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddHours(1), PowerStateEventKind.Suspend),
            MakeEvent(Base.AddHours(3), PowerStateEventKind.Resume)
        };

        var samples = new List<SystemPowerSample>
        {
            MakeSample(Base),
            MakeSample(Base.AddHours(4))
        };

        var result = GapDetector.Detect(events, samples, Base, Base.AddHours(4));

        // Only pair 1h→3h should be captured (the -1h event is before the window
        // and has no matching resume in range).
        Assert.Single(result);
        Assert.Equal(GapKind.ConfirmedSleep, result[0].Kind);
    }
}
