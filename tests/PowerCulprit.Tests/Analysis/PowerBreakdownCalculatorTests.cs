using PowerCulprit.Core.Analysis;

namespace PowerCulprit.Tests.Analysis;

public class PowerBreakdownCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsPackageAndResidualShares()
    {
        var start = DateTime.UtcNow;
        var result = PowerBreakdownCalculator.Calculate(
        [
            new(start, 10, 4),
            new(start.AddSeconds(2), 20, 8)
        ]);

        Assert.NotNull(result);
        Assert.True(result.IsReliable);
        Assert.Equal(15, result.BatteryDischargeWatts, 6);
        Assert.Equal(6, result.CpuPackagePowerWatts, 6);
        Assert.Equal(9, result.NonCpuResidualWatts, 6);
        Assert.Equal(40, result.CpuSharePercent!.Value, 6);
        Assert.Equal(60, result.NonCpuSharePercent!.Value, 6);
    }

    [Fact]
    public void Calculate_TimeWeightsIrregularSamples()
    {
        var start = DateTime.UtcNow;
        var result = PowerBreakdownCalculator.Calculate(
        [
            new(start, 10, 2),
            new(start.AddSeconds(2), 10, 2),
            new(start.AddSeconds(8), 20, 10)
        ]);

        Assert.NotNull(result);
        Assert.Equal(13.75, result.BatteryDischargeWatts, 6);
        Assert.Equal(5, result.CpuPackagePowerWatts, 6);
        Assert.Equal(36.363636, result.CpuSharePercent!.Value, 5);
    }

    [Fact]
    public void Calculate_DoesNotBridgeMonitoringGaps()
    {
        var start = DateTime.UtcNow;
        var gap = GapDetector.MaxSampleGap + TimeSpan.FromSeconds(1);
        var result = PowerBreakdownCalculator.Calculate(
        [
            new(start, 10, 2),
            new(start.Add(gap), 20, 10)
        ]);

        Assert.NotNull(result);
        Assert.Equal(15, result.BatteryDischargeWatts, 6);
        Assert.Equal(6, result.CpuPackagePowerWatts, 6);
    }

    [Fact]
    public void Calculate_MarksLargeSensorMismatchUnreliable()
    {
        var result = PowerBreakdownCalculator.Calculate(
        [
            new(DateTime.UtcNow, 10, 12)
        ]);

        Assert.NotNull(result);
        Assert.False(result.IsReliable);
        Assert.Null(result.CpuSharePercent);
        Assert.Null(result.NonCpuSharePercent);
        Assert.Equal(0, result.NonCpuResidualWatts);
    }

    [Fact]
    public void Calculate_ReturnsNullWithoutPairedValidPower()
    {
        var now = DateTime.UtcNow;
        var result = PowerBreakdownCalculator.Calculate(
        [
            new(now, 0, 0),
            new(now.AddSeconds(2), 10, null),
            new(now.AddSeconds(4), null, 3)
        ]);

        Assert.Null(result);
    }
}
