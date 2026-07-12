using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class CpuAttributionAnalyzerTests
{
    private readonly DateTime _baseTime = new(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptySamples_DoesNotCrash()
    {
        var result = CpuAttributionAnalyzer.Analyze(Array.Empty<CpuTimelineSample>());
        Assert.NotNull(result);
        Assert.Contains("No CPU attribution data", result.Summary);
    }

    [Fact]
    public void GapDoesNotInflateCpuEnergy()
    {
        // Constant 5W CPU package power. A 2-hour gap (suspend) sits between
        // the second and third samples. CumulativeEnergyWh mirrors what
        // CpuTimelineBuilder produces: it advances only while awake and is
        // frozen across the gap.
        var samples = new List<CpuTimelineSample>
        {
            new(_baseTime, null, 0.0, null, null, 5.0),
            new(_baseTime.AddSeconds(2), null, 0.0028, null, null, 5.0),
            new(_baseTime.AddHours(2), null, 0.0028, null, null, 5.0),
            new(_baseTime.AddHours(2).AddSeconds(2), null, 0.0056, null, null, 5.0),
        };

        var result = CpuAttributionAnalyzer.Analyze(samples);

        // Two awake segments of 2s at 5W ≈ 0.0056 Wh. The old code integrated
        // 5W × 2h ≈ 10 Wh across the gap, three orders of magnitude larger.
        Assert.True(result.CpuPackageEnergyWh.HasValue, "CPU package energy should be computed");
        Assert.True(result.CpuPackageEnergyWh!.Value < 0.02,
            $"Expected gap to be skipped, got {result.CpuPackageEnergyWh} Wh");
    }

    [Fact]
    public void GapDoesNotDistortCpuPowerCorrelation()
    {
        // Power and cumulative-energy-delta are strongly correlated while awake,
        // then frozen across a gap. The correlation must come back finite and
        // non-NaN — the gap segment is skipped rather than paired as a bogus
        // zero-rate point.
        var samples = new List<CpuTimelineSample>
        {
            new(_baseTime, null, 0.0, null, null, 2.0),
            new(_baseTime.AddSeconds(2), null, 0.0012, null, null, 4.0),
            new(_baseTime.AddSeconds(4), null, 0.0036, null, null, 6.0),
            new(_baseTime.AddSeconds(6), null, 0.0072, null, null, 8.0),
            // 2-hour gap (suspend) — energy frozen at the last awake value.
            new(_baseTime.AddHours(2).AddSeconds(6), null, 0.0072, null, null, 8.0),
        };

        var result = CpuAttributionAnalyzer.Analyze(samples);

        if (result.CpuPowerDischargeCorrelation.HasValue)
        {
            var r = result.CpuPowerDischargeCorrelation.Value;
            Assert.False(double.IsNaN(r), "Correlation must not be NaN");
            Assert.False(double.IsInfinity(r), "Correlation must not be infinite");
        }
    }
}
