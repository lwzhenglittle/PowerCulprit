using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class BatteryCycleBuilderTests
{
    [Fact]
    public void AcToBattery_StartsCycle_AndBatteryToAcEndsIt()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 100),
            Power(start.AddMinutes(1), ac: false, percent: 100),
            Power(start.AddMinutes(2), ac: false, percent: 90),
            Power(start.AddMinutes(3), ac: true, percent: 90)
        });

        var cycle = Assert.Single(result.RawCycles);
        Assert.Equal(start.AddMinutes(1), cycle.StartUtc);
        Assert.Equal(start.AddMinutes(3), cycle.EndUtc);
        Assert.Equal(start.AddMinutes(2), cycle.LastSampleUtc);
        Assert.Equal(10, cycle.DischargePercent);
        Assert.False(cycle.IsOpen);
        Assert.Equal(BatteryCycleConfidence.High, cycle.Confidence);
    }

    [Fact]
    public void FirstOfflineSample_CreatesPartialLowConfidenceCycle()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: false, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 77)
        });

        var cycle = Assert.Single(result.RawCycles);
        Assert.True(cycle.IsOpen);
        Assert.Null(cycle.EndUtc);
        Assert.Equal(BatteryCycleConfidence.Low, cycle.Confidence);
    }

    [Fact]
    public void StillOfflineAtLatestSample_CreatesOpenCycle()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 92),
            Power(start.AddMinutes(1), ac: false, percent: 92),
            Power(start.AddMinutes(2), ac: false, percent: 88)
        });

        var cycle = Assert.Single(result.RawCycles);
        Assert.True(cycle.IsOpen);
        Assert.Null(cycle.EndUtc);
        Assert.Equal(start.AddMinutes(2), cycle.EffectiveEndUtc);
    }

    [Fact]
    public void FullChargeThenUnplug_StartsNewDisplayCycle()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 60),
            Power(start.AddMinutes(1), ac: false, percent: 60),
            Power(start.AddMinutes(2), ac: false, percent: 55),
            Power(start.AddMinutes(3), ac: true, percent: 55),
            Power(start.AddMinutes(4), ac: true, percent: 100),
            Power(start.AddMinutes(4).AddSeconds(30), ac: true, percent: 99),
            Power(start.AddMinutes(5), ac: false, percent: 99),
            Power(start.AddMinutes(6), ac: false, percent: 95),
            Power(start.AddMinutes(7), ac: true, percent: 95)
        });

        Assert.Equal(2, result.RawCycles.Count);
        Assert.Equal(2, result.DisplayCycles.Count);
        Assert.False(result.RawCycles[0].StartedAtFullCharge);
        Assert.True(result.RawCycles[1].StartedAtFullCharge);
    }

    [Fact]
    public void ConsecutiveSmallCycles_RemainSeparateDisplayCycles()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            Power(start.AddMinutes(2), ac: false, percent: 75),
            Power(start.AddMinutes(3), ac: true, percent: 75),
            Power(start.AddMinutes(3).AddSeconds(1), ac: true, percent: 75),
            Power(start.AddMinutes(4), ac: false, percent: 75),
            Power(start.AddMinutes(5), ac: false, percent: 65),
            Power(start.AddMinutes(6), ac: true, percent: 65),
            Power(start.AddMinutes(6).AddSeconds(1), ac: true, percent: 65),
            Power(start.AddMinutes(7), ac: false, percent: 65),
            Power(start.AddMinutes(8), ac: false, percent: 59),
            Power(start.AddMinutes(9), ac: true, percent: 59)
        });

        Assert.Equal(3, result.RawCycles.Count);
        Assert.Equal(3, result.DisplayCycles.Count);
        Assert.All(result.DisplayCycles, display => Assert.Equal(1, display.RawCycleCount));
        Assert.Equal(5, result.DisplayCycles[0].DischargePercent);
        Assert.Equal(10, result.DisplayCycles[1].DischargePercent);
        Assert.Equal(6, result.DisplayCycles[2].DischargePercent);
    }

    [Fact]
    public void SingleAcSampleBetweenBatteryRuns_IsTreatedAsStateJitter()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            Power(start.AddMinutes(2), ac: false, percent: 75),
            Power(start.AddMinutes(3), ac: true, percent: 75),
            Power(start.AddMinutes(4), ac: false, percent: 74),
            Power(start.AddMinutes(5), ac: false, percent: 70),
            Power(start.AddMinutes(6), ac: true, percent: 70)
        });

        var cycle = Assert.Single(result.RawCycles);
        Assert.Single(result.DisplayCycles);
        Assert.False(cycle.IsOpen);
        Assert.Equal(10, cycle.DischargePercent);
    }

    [Fact]
    public void UnknownAcState_DoesNotStartAFakeBatteryCycle()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: false, percent: 80) with { IsAcOnline = null },
            Power(start.AddMinutes(1), ac: false, percent: 75) with { IsAcOnline = null }
        });

        Assert.Empty(result.RawCycles);
        Assert.Empty(result.DisplayCycles);
    }

    [Fact]
    public void LargeSampleGap_MarksLowConfidence_AndPreventsMerge()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            Power(start.AddMinutes(12), ac: false, percent: 75),
            Power(start.AddMinutes(13), ac: false, percent: 75),
            Power(start.AddMinutes(14), ac: true, percent: 75),
            Power(start.AddMinutes(15), ac: false, percent: 75),
            Power(start.AddMinutes(16), ac: false, percent: 70),
            Power(start.AddMinutes(17), ac: true, percent: 70)
        });

        Assert.Equal(2, result.DisplayCycles.Count);
        Assert.Equal(BatteryCycleConfidence.Low, result.RawCycles[0].Confidence);
        Assert.Equal(BatteryCycleConfidence.Low, result.RawCycles[1].Confidence);
        Assert.Equal(start.AddMinutes(1), result.RawCycles[0].LastSampleUtc);
        Assert.Equal(start.AddMinutes(12), result.RawCycles[1].StartUtc);
        Assert.Equal(1, result.DisplayCycles[0].RawCycleCount);
        Assert.Equal(1, result.DisplayCycles[1].RawCycleCount);
    }

    [Fact]
    public void SessionBoundaryInOfflineStretch_DoesNotSplitContinuousCycle()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(
            new[]
            {
                Power(start, ac: true, percent: 90),
                Power(start.AddMinutes(1), ac: false, percent: 90),
                Power(start.AddMinutes(2), ac: false, percent: 85),
                Power(start.AddMinutes(3), ac: false, percent: 80),
                Power(start.AddMinutes(4), ac: false, percent: 75)
            },
            new[] { start.AddMinutes(2).AddSeconds(30) });

        var cycle = Assert.Single(result.RawCycles);
        Assert.Single(result.DisplayCycles);
        Assert.Equal(start.AddMinutes(1), cycle.StartUtc);
        Assert.True(cycle.IsOpen);
        Assert.False(cycle.StartedAtSessionBoundary);
        Assert.Equal(BatteryCycleConfidence.High, cycle.Confidence);
    }

    [Fact]
    public void MultipleSessionBoundaries_DoNotSplitContinuousOfflineStretch()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(
            new[]
            {
                Power(start, ac: true, percent: 90),
                Power(start.AddMinutes(1), ac: false, percent: 90),
                Power(start.AddMinutes(2), ac: false, percent: 85),
                Power(start.AddMinutes(3), ac: false, percent: 80),
                Power(start.AddMinutes(4), ac: false, percent: 75),
                Power(start.AddMinutes(5), ac: false, percent: 70)
            },
            new[]
            {
                start.AddMinutes(2).AddSeconds(30),
                start.AddMinutes(4).AddSeconds(30)
            });

        var cycle = Assert.Single(result.RawCycles);
        Assert.Single(result.DisplayCycles);
        Assert.Equal(start.AddMinutes(1), cycle.StartUtc);
        Assert.True(cycle.IsOpen);
        Assert.False(cycle.StartedAtSessionBoundary);
        Assert.Equal(BatteryCycleConfidence.High, cycle.Confidence);
    }

    [Fact]
    public void EmptySessionBoundaries_MatchesOriginalBuildOverload()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var samples = new[]
        {
            Power(start, ac: true, percent: 90),
            Power(start.AddMinutes(1), ac: false, percent: 90),
            Power(start.AddMinutes(2), ac: false, percent: 80),
            Power(start.AddMinutes(3), ac: true, percent: 80)
        };

        var original = BatteryCycleBuilder.Build(samples);
        var withEmptyBoundaries = BatteryCycleBuilder.Build(samples, Array.Empty<DateTime>());

        Assert.Equal(original.RawCycles.Count, withEmptyBoundaries.RawCycles.Count);
        Assert.Equal(original.DisplayCycles.Count, withEmptyBoundaries.DisplayCycles.Count);
        Assert.Equal(original.RawCycles[0].StartUtc, withEmptyBoundaries.RawCycles[0].StartUtc);
        Assert.Equal(original.RawCycles[0].EndUtc, withEmptyBoundaries.RawCycles[0].EndUtc);
        Assert.Equal(original.RawCycles[0].Confidence, withEmptyBoundaries.RawCycles[0].Confidence);
    }

    [Fact]
    public void LargeSampleGap_CoveredBySleep_Overlap_KeepsHighConfidence()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var samples = new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            // Sleep happens here; next sample is more than 10 minutes later.
            Power(start.AddMinutes(11).AddSeconds(1), ac: false, percent: 75),
            Power(start.AddMinutes(12), ac: true, percent: 75)
        };

        var sleepIntervals = new[]
        {
            new PowerStateInterval
            {
                StartUtc = start.AddMinutes(2),
                EndUtc = start.AddMinutes(10),
                Kind = GapKind.ConfirmedSleep
            }
        };

        var result = BatteryCycleBuilder.Build(samples, Array.Empty<DateTime>(), sleepIntervals);

        var cycle = Assert.Single(result.RawCycles);
        Assert.Equal(BatteryCycleConfidence.High, cycle.Confidence);
        Assert.Equal(5, cycle.DischargePercent);
        Assert.Equal(5, cycle.DischargeWh);
    }

    [Fact]
    public void LargeSampleGap_NotCoveredBySleep_SplitsAndMarksBothSidesLowConfidence()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var samples = new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            Power(start.AddMinutes(11).AddSeconds(1), ac: false, percent: 75),
            Power(start.AddMinutes(12), ac: true, percent: 75)
        };

        var result = BatteryCycleBuilder.Build(samples);

        Assert.Equal(2, result.RawCycles.Count);
        Assert.Equal(BatteryCycleConfidence.Low, result.RawCycles[0].Confidence);
        Assert.Equal(BatteryCycleConfidence.Low, result.RawCycles[1].Confidence);
        Assert.Equal(start.AddMinutes(1), result.RawCycles[0].LastSampleUtc);
        Assert.Equal(start.AddMinutes(11).AddSeconds(1), result.RawCycles[1].StartUtc);
    }

    private static SystemPowerSample Power(DateTime timestampUtc, bool ac, double percent)
    {
        return new SystemPowerSample
        {
            TimestampUtc = timestampUtc,
            IsAcOnline = ac,
            BatteryPercent = percent,
            RemainingCapacityMWh = percent * 1000.0,
            FullChargeCapacityMWh = 100000.0,
            ChargeRateMilliwatts = ac ? 5000 : -10000
        };
    }
}
