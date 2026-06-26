using Microsoft.Extensions.Logging.Abstractions;
using PowerCulprit.Collectors;
using PowerCulprit.Storage;

namespace PowerCulprit.Tests.Collectors;

public class MonitoringServiceTests
{
    [Fact]
    public void GpuSampling_DefaultsOff_AndCanBeToggled()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"powerculprit_monitor_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new DatabaseManager(dbPath);
            using var gpuCollector = new WindowsGpuEngineCollector(
                NullLogger<WindowsGpuEngineCollector>.Instance);
            using var lhmCollector = new LibreHardwareMonitorCollector(
                NullLogger<LibreHardwareMonitorCollector>.Instance);

            var service = new MonitoringService(
                new BatteryPowerCollector(NullLogger<BatteryPowerCollector>.Instance),
                new ProcessResourceCollector(NullLogger<ProcessResourceCollector>.Instance),
                gpuCollector,
                lhmCollector,
                new IntelCpuPowerCollector(NullLogger<IntelCpuPowerCollector>.Instance),
                new IntelGpuPowerCollector(NullLogger<IntelGpuPowerCollector>.Instance),
                new NoOpWindowsEtwActivityCollector(),
                db,
                NullLogger<MonitoringService>.Instance);

            Assert.False(service.IsGpuSamplingEnabled);

            service.SetGpuSamplingEnabled(true);
            Assert.True(service.IsGpuSamplingEnabled);

            service.SetGpuSamplingEnabled(false);
            Assert.False(service.IsGpuSamplingEnabled);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for test artifacts.
        }
    }
}
