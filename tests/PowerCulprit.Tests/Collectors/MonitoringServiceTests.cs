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
                new NoOpWmiActivityCollector(),
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

    [Fact]
    public async Task Startup_DbInitFailure_TransitionsToNotRunning()
    {
        // Pointing DatabaseManager at an existing directory makes
        // InitializeAsync fail (SQLite cannot open a directory as a DB file).
        // This exercises the startup-failure path: IsRunning must flip back to
        // false instead of staying true with a dead loop.
        var dirPath = Path.GetTempPath();

        using var db = new DatabaseManager(dirPath);
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
            new NoOpWmiActivityCollector(),
            db,
            NullLogger<MonitoringService>.Instance);

        var stopped = new TaskCompletionSource<bool>();
        service.RunningChanged += running =>
        {
            if (!running)
                stopped.TrySetResult(true);
        };

        try
        {
            await service.StartAsync();
            Assert.True(service.IsRunning); // set synchronously by StartAsync

            // The loop task hits InitializeAsync, fails, and transitions to stopped.
            await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.False(service.IsRunning);
        }
        finally
        {
            try { await service.StopAsync(); }
            catch { /* best-effort cleanup of writer task / CTS */ }
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
