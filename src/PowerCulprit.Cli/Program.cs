using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerCulprit.Collectors;
using PowerCulprit.Storage;

namespace PowerCulprit.Cli;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var headless = false;
        var diagnose = false;
        var enableGpuSampling = false;
        string? durationArg = null;
        int intervalSec = 2;
        string? dbPath = null;

        // Simple CLI argument parsing
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless":
                    headless = true;
                    break;
                case "--diagnose":
                    diagnose = true;
                    break;
                case "--enable-gpu":
                case "--gpu":
                    enableGpuSampling = true;
                    break;
                case "--duration" when i + 1 < args.Length:
                    durationArg = args[++i];
                    break;
                case "--interval" when i + 1 < args.Length:
                    int.TryParse(args[++i], out intervalSec);
                    break;
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
            }
        }

        if (diagnose)
        {
            return await RunDiagnoseAsync();
        }

        if (headless)
        {
            return await RunHeadlessAsync(durationArg, intervalSec, dbPath, enableGpuSampling);
        }

        return RunGui();
    }

    // ──────────────────────────────────────────────
    //  GUI launch mode
    // ──────────────────────────────────────────────

    private static int RunGui()
    {
        Console.WriteLine("Starting PowerCulprit Desktop (GUI)...");

        try
        {
            var desktopExe = FindDesktopExecutable();
            if (desktopExe is not null)
            {
                StartDetached(desktopExe, workingDirectory: Path.GetDirectoryName(desktopExe));
                Console.WriteLine($"Started: {desktopExe}");
                return 0;
            }

            var desktopProject = FindDesktopProject();
            if (desktopProject is not null)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = Path.GetDirectoryName(desktopProject),
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("--project");
                startInfo.ArgumentList.Add(desktopProject);

                Process.Start(startInfo);
                Console.WriteLine($"Started via dotnet run: {desktopProject}");
                return 0;
            }

            Console.Error.WriteLine("PowerCulprit Desktop executable or project could not be found.");
            Console.Error.WriteLine("Build the solution first, or run from the repository root.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start PowerCulprit Desktop: {ex.Message}");
            return 1;
        }
    }

    private static void StartDetached(string fileName, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    private static string? FindDesktopExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var sameDirectory = Path.Combine(baseDirectory, "PowerCulprit.Desktop.exe");
        if (File.Exists(sameDirectory))
            return sameDirectory;

        foreach (var root in GetSearchRoots())
        {
            var desktopBin = Path.Combine(root, "src", "PowerCulprit.Desktop", "bin");
            if (!Directory.Exists(desktopBin))
                continue;

            var exe = Directory
                .EnumerateFiles(desktopBin, "PowerCulprit.Desktop.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (exe is not null)
                return exe;
        }

        return null;
    }

    private static string? FindDesktopProject()
    {
        foreach (var root in GetSearchRoots())
        {
            var project = Path.Combine(root, "src", "PowerCulprit.Desktop", "PowerCulprit.Desktop.csproj");
            if (File.Exists(project))
                return project;
        }

        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (seen.Add(directory.FullName))
                    yield return directory.FullName;

                directory = directory.Parent;
            }
        }
    }

    // ──────────────────────────────────────────────
    //  Diagnose mode
    // ──────────────────────────────────────────────

    private static Task<int> RunDiagnoseAsync()
    {
        Console.WriteLine("=== PowerCulprit Diagnose ===");
        Console.WriteLine();

        // Windows version
        Console.WriteLine($"Windows Version: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"OS Build:        {Environment.OSVersion.Version}");

        // CPU name
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                Console.WriteLine($"CPU:             {obj["Name"]}");
            }
        }
        catch
        {
            Console.WriteLine("CPU:             <WMI unavailable>");
        }

        // GPU name
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                Console.WriteLine($"GPU:             {obj["Name"]}");
            }
        }
        catch
        {
            Console.WriteLine("GPU:             <WMI unavailable>");
        }

        // Battery API status
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report is null)
            {
                Console.WriteLine("Battery API:     Unavailable (no battery detected)");
            }
            else
            {
                Console.WriteLine("Battery API:     Available");
                Console.WriteLine($"  ChargeRate:    {(report.ChargeRateInMilliwatts.HasValue ? $"{report.ChargeRateInMilliwatts.Value} mW" : "Unavailable")}");
                Console.WriteLine($"  Remaining:     {(report.RemainingCapacityInMilliwattHours.HasValue ? $"{report.RemainingCapacityInMilliwattHours.Value} mWh" : "Unavailable")}");
                Console.WriteLine($"  FullCharge:    {(report.FullChargeCapacityInMilliwattHours.HasValue ? $"{report.FullChargeCapacityInMilliwattHours.Value} mWh" : "Unavailable")}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Battery API:     Error — {ex.Message}");
        }

        // GPU Engine counter status
        try
        {
            var category = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
            var hasUtil = category.CounterExists("Utilization Percentage");
            var names = category.GetInstanceNames();
            Console.WriteLine($"GPU Engine Counter: {(hasUtil ? "Available" : "Unavailable")} ({names.Length} instances)");
        }
        catch
        {
            Console.WriteLine("GPU Engine Counter: Unavailable");
        }

        // LibreHardwareMonitor status
        try
        {
            var computer = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsBatteryEnabled = true
            };
            computer.Open();
            var hasHardware = computer.Hardware.Count > 0;
            computer.Close();
            Console.WriteLine($"LibreHardwareMonitor: {(hasHardware ? "Available" : "No hardware detected")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LibreHardwareMonitor: Error — {ex.Message}");
        }

        // Intel tools
        var intelPowerGadget = File.Exists(@"C:\Program Files\Intel\Power Gadget 3.6\PowerLog3.0.exe");
        Console.WriteLine($"Intel Power Gadget: {(intelPowerGadget ? "Found" : "Not found")}");

        var intelPcm = File.Exists(@"C:\Windows\System32\pcm.exe") ||
                       File.Exists(@"C:\Program Files\Intel\PCM\pcm.exe");
        Console.WriteLine($"Intel PCM:         {(intelPcm ? "Found" : "Not found")}");

        var levelZero = File.Exists(@"C:\Windows\System32\ze_loader.dll");
        Console.WriteLine($"Level Zero Sysman: {(levelZero ? "ze_loader.dll found" : "Not found")}");

        // Admin status
        var isAdmin = System.Security.Principal.WindowsIdentity.GetCurrent()
            .Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ?? false;
        Console.WriteLine($"Administrator:     {(isAdmin ? "Yes" : "No")}");

        // Paths
        Console.WriteLine($"Default DB path:   {DatabaseManager.GetDefaultDatabasePath()}");
        Console.WriteLine($"Default log path:  {DatabaseManager.GetDefaultLogPath()}");

        Console.WriteLine();
        Console.WriteLine("Diagnose complete.");
        return Task.FromResult(0);
    }

    // ──────────────────────────────────────────────
    //  Headless sampling mode
    // ──────────────────────────────────────────────

    private static async Task<int> RunHeadlessAsync(
        string? durationArg, int intervalSec, string? dbPath, bool enableGpuSampling)
    {
        // Parse duration
        var duration = TimeSpan.FromMinutes(2); // default 2 min
        if (!string.IsNullOrEmpty(durationArg))
        {
            duration = ParseDuration(durationArg);
        }

        // Clamp interval
        intervalSec = Math.Clamp(intervalSec, 1, 5);

        Console.WriteLine("=== PowerCulprit Headless Sampling ===");
        Console.WriteLine($"Duration:  {duration}");
        Console.WriteLine($"Interval:  {intervalSec}s");
        Console.WriteLine($"GPU Engine sampling: {(enableGpuSampling ? "Enabled" : "Disabled")}");
        Console.WriteLine($"DB path:   {(dbPath ?? DatabaseManager.GetDefaultDatabasePath())}");
        Console.WriteLine("Press Ctrl+C to stop early.");
        Console.WriteLine();

        // Build DI container
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning); // quiet by default
        });
        services.AddSingleton(_ => new DatabaseManager(dbPath));
        services.AddSingleton<BatteryPowerCollector>();
        services.AddSingleton<ProcessResourceCollector>();
        services.AddSingleton<WindowsGpuEngineCollector>();
        services.AddSingleton<LibreHardwareMonitorCollector>();
        services.AddSingleton<IntelCpuPowerCollector>();
        services.AddSingleton<IntelGpuPowerCollector>();
        services.AddSingleton<MonitoringService>();

        var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<MonitoringService>();
        monitor.SetInterval(intervalSec);
        monitor.SetGpuSamplingEnabled(enableGpuSampling);

        using var cts = new CancellationTokenSource(duration);

        // Handle Ctrl+C
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Stopping...");
            cts.Cancel();
        };

        var startTime = DateTime.UtcNow;
        Console.WriteLine($"Sampling started at {startTime:yyyy-MM-dd HH:mm:ss} UTC");

        try
        {
            await monitor.StartAsync(cts.Token);

            // Wait for the duration or cancellation
            try
            {
                await Task.Delay(duration, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C or duration expiry
            }

            await monitor.StopAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }

        var endTime = DateTime.UtcNow;
        var elapsed = endTime - startTime;
        Console.WriteLine($"Sampling stopped at {endTime:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Total elapsed: {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Data written to: {dbPath ?? DatabaseManager.GetDefaultDatabasePath()}");
        Console.WriteLine("Headless sampling complete.");

        return 0;
    }

    private static TimeSpan ParseDuration(string arg)
    {
        arg = arg.Trim().ToLowerInvariant();

        if (arg.EndsWith("ms") && double.TryParse(arg[..^2], out var ms))
            return TimeSpan.FromMilliseconds(ms);
        if (arg.EndsWith('s') && double.TryParse(arg[..^1], out var sec))
            return TimeSpan.FromSeconds(sec);
        if (arg.EndsWith('m') && double.TryParse(arg[..^1], out var min))
            return TimeSpan.FromMinutes(min);
        if (arg.EndsWith('h') && double.TryParse(arg[..^1], out var hr))
            return TimeSpan.FromHours(hr);
        if (double.TryParse(arg, out var val))
            return TimeSpan.FromSeconds(val); // bare number = seconds

        return TimeSpan.FromMinutes(2);
    }
}
