using Microsoft.Extensions.Logging.Abstractions;
using PowerCulprit.Core.Models;
using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class IntelCpuPowerCollectorTests
{
    [Fact]
    public void GetCpuPowerDomains_MatchExactRaplSensors()
    {
        var collector = new IntelCpuPowerCollector(
            NullLogger<IntelCpuPowerCollector>.Instance);
        var samples = new[]
        {
            Sensor("Intel Core CPU", "CPU Package", "Power", 18.2),
            Sensor("Intel Core CPU", "CPU Platform", "Power", 7.1),
            Sensor("Intel Core CPU", "CPU Cores", "Power", 4.7),
            Sensor("Intel Core CPU", "CPU Memory", "Power", 1.2)
        };

        Assert.Equal(7.1, collector.GetCpuPlatformPowerWatts(samples));
        Assert.Equal(4.7, collector.GetCpuCoresPowerWatts(samples));
        Assert.Equal(1.2, collector.GetCpuMemoryPowerWatts(samples));
    }

    [Fact]
    public void GetCpuPowerDomains_IgnoreNonPowerAndUnrelatedSensors()
    {
        var collector = new IntelCpuPowerCollector(
            NullLogger<IntelCpuPowerCollector>.Instance);
        var samples = new[]
        {
            Sensor("Intel Core CPU", "CPU Platform", "Temperature", 48.0),
            Sensor("Motherboard", "CPU Platform", "Power", 3.0),
            Sensor("Intel GPU", "GPU Power", "Power", 12.0)
        };

        Assert.Null(collector.GetCpuPlatformPowerWatts(samples));
        Assert.Null(collector.GetCpuCoresPowerWatts(samples));
        Assert.Null(collector.GetCpuMemoryPowerWatts(samples));
    }

    private static HardwareSensorSample Sensor(
        string deviceName,
        string sensorName,
        string metricName,
        double value)
    {
        return new HardwareSensorSample
        {
            TimestampUtc = DateTime.UtcNow,
            Source = "LibreHardwareMonitor",
            DeviceName = deviceName,
            SensorName = sensorName,
            MetricName = metricName,
            Value = value,
            Unit = metricName == "Power" ? "W" : ""
        };
    }
}
