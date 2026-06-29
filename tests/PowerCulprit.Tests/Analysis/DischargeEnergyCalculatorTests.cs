using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class DischargeEnergyCalculatorTests
{
    private static readonly DateTime Base = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SystemPowerSample Sample(DateTime ts, double dischargeWatts)
        => new() { TimestampUtc = ts, ChargeRateMilliwatts = -dischargeWatts * 1000.0 };

    [Fact]
    public void FewerThanTwoSamples_ReturnsNull()
    {
        var samples = new List<SystemPowerSample> { Sample(Base, 10) };

        Assert.Null(DischargeEnergyCalculator.CalculateEnergyUsedWh(samples));
    }

    [Fact]
    public void ContiguousSamples_IntegrateTrapezoid()
    {
        var samples = new List<SystemPowerSample>
        {
            Sample(Base, 10),
            Sample(Base.AddSeconds(1), 10)
        };

        var wh = DischargeEnergyCalculator.CalculateEnergyUsedWh(samples);

        // (10 + 10) / 2 W sustained for 1 second.
        Assert.NotNull(wh);
        Assert.Equal(10.0 / 3600.0, wh!.Value, 5);
    }

    [Fact]
    public void GapLargerThanMaxSampleGap_IsNotIntegrated()
    {
        // Two samples 2 hours apart — an unobserved gap (sleep / hibernate).
        var samples = new List<SystemPowerSample>
        {
            Sample(Base, 10),
            Sample(Base.AddHours(2), 0.5)
        };

        var wh = DischargeEnergyCalculator.CalculateEnergyUsedWh(samples);

        // Pre-fix this returned ~10.5 Wh (5.25 W × 2 h across the gap).
        Assert.Null(wh);
    }

    [Fact]
    public void SleepBetweenContiguousRuns_OnlyAwakePortionsIntegrated()
    {
        // A pre-sleep run at 10 W, an 8 h sleep (bogus 0.5 W at the boundary
        // sample, as the capacity-delta estimate would produce), then a
        // post-resume run back at 10 W.
        var samples = new List<SystemPowerSample>
        {
            Sample(Base, 10),
            Sample(Base.AddSeconds(1), 10),
            Sample(Base.AddHours(8), 0.5),
            Sample(Base.AddHours(8).AddSeconds(1), 10)
        };

        var wh = DischargeEnergyCalculator.CalculateEnergyUsedWh(samples);

        // Only the two 1-second awake segments contribute (~0.0042 Wh).
        // Pre-fix the 8 h gap was integrated as 5.25 W × 8 h ≈ 42 Wh.
        Assert.NotNull(wh);
        Assert.InRange(wh!.Value, 0.004, 0.005);
    }

    [Fact]
    public void ChargingSamples_ContributeZeroEnergy()
    {
        // Positive charge rate → GetDischargeWatts returns 0, not null.
        var samples = new List<SystemPowerSample>
        {
            new() { TimestampUtc = Base, ChargeRateMilliwatts = 5000 },
            new() { TimestampUtc = Base.AddSeconds(1), ChargeRateMilliwatts = 5000 }
        };

        var wh = DischargeEnergyCalculator.CalculateEnergyUsedWh(samples);

        Assert.NotNull(wh);
        Assert.Equal(0.0, wh!.Value, 5);
    }
}
