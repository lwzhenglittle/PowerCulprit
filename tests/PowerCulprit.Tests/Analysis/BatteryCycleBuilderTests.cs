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
    public void ConsecutiveSmallCycles_MergeUntilPercentAndWhThresholdsAreMet()
    {
        var start = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
        var result = BatteryCycleBuilder.Build(new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            Power(start.AddMinutes(2), ac: false, percent: 75),
            Power(start.AddMinutes(3), ac: true, percent: 75),
            Power(start.AddMinutes(4), ac: false, percent: 75),
            Power(start.AddMinutes(5), ac: false, percent: 65),
            Power(start.AddMinutes(6), ac: true, percent: 65),
            Power(start.AddMinutes(7), ac: false, percent: 65),
            Power(start.AddMinutes(8), ac: false, percent: 59),
            Power(start.AddMinutes(9), ac: true, percent: 59)
        });

        var display = Assert.Single(result.DisplayCycles);
        Assert.Equal(3, display.RawCycleCount);
        Assert.Equal(21, display.DischargePercent);
        Assert.Equal(21, display.DischargeWh);
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
            Power(start.AddMinutes(13), ac: true, percent: 75),
            Power(start.AddMinutes(14), ac: false, percent: 75),
            Power(start.AddMinutes(15), ac: false, percent: 70),
            Power(start.AddMinutes(16), ac: true, percent: 70)
        });

        Assert.Equal(2, result.DisplayCycles.Count);
        Assert.Equal(BatteryCycleConfidence.Low, result.RawCycles[0].Confidence);
        Assert.Equal(1, result.DisplayCycles[0].RawCycleCount);
        Assert.Equal(1, result.DisplayCycles[1].RawCycleCount);
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
