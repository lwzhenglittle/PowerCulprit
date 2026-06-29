using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class CpuTimelineBuilderTests
{
    private readonly DateTime _baseTime = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_EmptyInput_ReturnsEmpty()
    {
        var result = CpuTimelineBuilder.Build(Array.Empty<SystemPowerSample>(), Array.Empty<HardwareSensorSample>());

        Assert.Empty(result);
    }

    [Fact]
    public void Build_AggregatesCpuSensorsAndExcludesBusSpeed()
    {
        var power = new[]
        {
            new SystemPowerSample
            {
                TimestampUtc = _baseTime,
                IsAcOnline = false,
                BatteryPercent = 90,
                ChargeRateMilliwatts = -10000
            }
        };
        var hardware = new[]
        {
            Sensor("Bus Speed", "Clock", 100, "MHz"),
            Sensor("P-Core #1", "Clock", 4000, "MHz"),
            Sensor("E-Core #1", "Clock", 3000, "MHz"),
            Sensor("CPU Total", "Load", 25, "%"),
            Sensor("CPU Package", "Power", 12, "W")
        };

        var result = CpuTimelineBuilder.Build(power, hardware);

        var sample = Assert.Single(result);
        Assert.Equal(3500, sample.CpuAverageClockMhz);
        Assert.Equal(25, sample.CpuLoadPercent);
        Assert.Equal(12, sample.CpuPackagePowerWatts);
        Assert.Equal(90, sample.BatteryPercent);
    }

    [Fact]
    public void Analyze_ComputesCpuEnergyAndCorrelation()
    {
        var samples = new[]
        {
            new CpuTimelineSample(_baseTime, 90, 0.0, 3000, 10, 10),
            new CpuTimelineSample(_baseTime.AddHours(1), 88, 20.0, 3500, 20, 20),
            new CpuTimelineSample(_baseTime.AddHours(2), 85, 50.0, 4000, 30, 30),
            new CpuTimelineSample(_baseTime.AddHours(3), 81, 90.0, 4500, 40, 40)
        };

        var result = CpuAttributionAnalyzer.Analyze(samples);

        Assert.Equal(9, result.BatteryDropPercent);
        Assert.Equal(90, result.EnergyUsedWh);
        Assert.Equal(25, result.AvgCpuPackagePowerWatts);
        Assert.Equal(40, result.MaxCpuPackagePowerWatts);
        Assert.Equal(75, result.CpuPackageEnergyWh);
        Assert.True(result.CpuPowerDischargeCorrelation > 0.9);
        Assert.Contains("CPU package", result.Summary);
    }

    [Fact]
    public void Analyze_MissingCpuPower_ReportsUnavailable()
    {
        var samples = new[]
        {
            new CpuTimelineSample(_baseTime, 90, 0.0, 3000, 10, null),
            new CpuTimelineSample(_baseTime.AddMinutes(1), 90, 0.1, 3100, 12, null)
        };

        var result = CpuAttributionAnalyzer.Analyze(samples);

        Assert.Null(result.AvgCpuPackagePowerWatts);
        Assert.Null(result.CpuPackageEnergyWh);
        Assert.Contains("CPU package power is unavailable", result.Summary);
    }

    private HardwareSensorSample Sensor(string name, string metric, double value, string unit)
        => new()
        {
            TimestampUtc = _baseTime,
            Source = "LibreHardwareMonitor",
            DeviceName = "Intel Core Ultra X7 358H",
            SensorName = name,
            MetricName = metric,
            Value = value,
            Unit = unit
        };
}
