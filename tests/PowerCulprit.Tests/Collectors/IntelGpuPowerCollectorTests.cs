using Microsoft.Extensions.Logging.Abstractions;
using PowerCulprit.Collectors;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Collectors;

public class IntelGpuPowerCollectorTests
{
    [Fact]
    public void GetIgpuPowerWatts_ReturnsDirectHardwarePower()
    {
        var collector = new IntelGpuPowerCollector(NullLogger<IntelGpuPowerCollector>.Instance);
        var hardware = new[]
        {
            new HardwareSensorSample
            {
                DeviceName = "Intel Arc GPU", SensorName = "GPU Power",
                MetricName = "Power", Unit = "W", Value = 4.5
            }
        };

        Assert.Equal(4.5, collector.GetIgpuPowerWatts(hardware));
    }

    [Fact]
    public void GetIgpuPowerWatts_DoesNotReturnGpuUtilizationAsWatts()
    {
        var collector = new IntelGpuPowerCollector(NullLogger<IntelGpuPowerCollector>.Instance);
        var gpuActivity = new[]
        {
            new GpuProcessSample
            {
                ProcessName = "app.exe", EngineName = "3D",
                UtilizationPercent = 42, EngineType = GpuEngineType.ThreeD
            }
        };

        Assert.Null(collector.GetIgpuPowerWatts(
            Array.Empty<HardwareSensorSample>(), gpuActivity));
        Assert.Equal(42, collector.GetIgpuActivityPercent(gpuActivity));
    }
}
