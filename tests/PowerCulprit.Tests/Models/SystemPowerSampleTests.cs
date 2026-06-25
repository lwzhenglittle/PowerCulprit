using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Models;

public class SystemPowerSampleTests
{
    [Fact]
    public void Constructor_SetsDefaultValues()
    {
        var sample = new SystemPowerSample();

        Assert.Equal(default, sample.TimestampUtc);
        Assert.False(sample.IsAcOnline);
        Assert.Null(sample.BatteryPercent);
        Assert.Null(sample.ChargeRateMilliwatts);
        Assert.Null(sample.RemainingCapacityMWh);
        Assert.Null(sample.FullChargeCapacityMWh);
        Assert.Null(sample.EstimatedDischargeWatts);
        Assert.Null(sample.PowerMode);
    }

    [Fact]
    public void WithExpression_AllowsSettingAllFields()
    {
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        var sample = new SystemPowerSample
        {
            TimestampUtc = ts,
            IsAcOnline = false,
            BatteryPercent = 85.5,
            ChargeRateMilliwatts = -15000.0,
            RemainingCapacityMWh = 50000.0,
            FullChargeCapacityMWh = 58000.0,
            EstimatedDischargeWatts = 15.0,
            PowerMode = "Balanced"
        };

        Assert.Equal(ts, sample.TimestampUtc);
        Assert.False(sample.IsAcOnline);
        Assert.Equal(85.5, sample.BatteryPercent);
        Assert.Equal(-15000.0, sample.ChargeRateMilliwatts);
        Assert.Equal(50000.0, sample.RemainingCapacityMWh);
        Assert.Equal(58000.0, sample.FullChargeCapacityMWh);
        Assert.Equal(15.0, sample.EstimatedDischargeWatts);
        Assert.Equal("Balanced", sample.PowerMode);
    }

    [Fact]
    public void NegativeChargeRate_IndicatesDischarging()
    {
        var sample = new SystemPowerSample
        {
            TimestampUtc = DateTime.UtcNow,
            ChargeRateMilliwatts = -20000.0
        };

        Assert.True(sample.ChargeRateMilliwatts < 0);
    }

    [Fact]
    public void NullableFields_RemainNull_WhenNotSet()
    {
        var sample = new SystemPowerSample
        {
            TimestampUtc = DateTime.UtcNow,
            IsAcOnline = true
        };

        Assert.Null(sample.BatteryPercent);
        Assert.Null(sample.ChargeRateMilliwatts);
        Assert.Null(sample.PowerMode);
    }
}
