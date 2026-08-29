using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerCulprit.Collectors;
using PowerCulprit.Core.Models;
using PowerCulprit.Core.Services;
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

        if (SingleInstanceGuard.IsAnotherInstanceRunning())
        {
            Console.Error.WriteLine("PowerCulprit is already running.");
            return 1;
        }

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

    private static async Task<int> RunDiagnoseAsync()
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

        Console.WriteLine();

        // ── Data source status (reuses each collector's GetStatus / probe) ──
        // Diagnose used to re-implement every availability probe inline, which
        // drifted from the collectors' own GetStatus() and from the Desktop
        // status panel. Now it instantiates each collector and reads the same
        // SourceStatus the runtime uses, so there is a single source of truth.
        Console.WriteLine("Data sources:");
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // BatteryAPI, ProcessResource — no native handles, safe to cold-probe.
        PrintStatus(new BatteryPowerCollector(
            loggerFactory.CreateLogger<BatteryPowerCollector>()).GetStatus());
        PrintStatus(new ProcessResourceCollector(
            loggerFactory.CreateLogger<ProcessResourceCollector>()).GetStatus());

        // WindowsGpuEngine — creates a PerformanceCounterCategory (cheap probe).
        using (var gpuEngine = new WindowsGpuEngineCollector(
            loggerFactory.CreateLogger<WindowsGpuEngineCollector>()))
        {
            PrintStatus(gpuEngine.GetStatus());
        }

        // LibreHardwareMonitor — must Initialize() before GetStatus() reflects
        // real sensor availability; releases the LHM Computer on Dispose.
        IReadOnlyList<HardwareSensorSample> hardwareSamples;
        using (var lhm = new LibreHardwareMonitorCollector(
            loggerFactory.CreateLogger<LibreHardwareMonitorCollector>()))
        {
            lhm.Initialize();
            hardwareSamples = lhm.Collect();
            PrintStatus(lhm.GetStatus());
        }

        // Intel CPU / iGPU power — derived from LHM + GPU Engine samples; their
        // GetStatus() probes LHM sensor filters and Intel tool paths.
        var cpuPower = new IntelCpuPowerCollector(
            loggerFactory.CreateLogger<IntelCpuPowerCollector>());
        PrintStatus(cpuPower.GetStatus(hardwareSamples));

        var gpuPower = new IntelGpuPowerCollector(
            loggerFactory.CreateLogger<IntelGpuPowerCollector>());
        PrintStatus(gpuPower.GetStatus(hardwareSamples));

        // ETW — a real (short-lived) probe session is more informative than the
        // collector's cached "not started" status, so keep the dedicated probe.
        try
        {
            var statuses = await WindowsEtwActivityProbe.ProbeAsync(TimeSpan.FromSeconds(2));
            foreach (var status in statuses)
                PrintStatus(status);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ETW Kernel Session: {SourceStatusStrings.Unavailable} — {ex.Message}");
        }

        // WMI Activity — static cold probe (no need to start the collector).
        PrintStatus(WmiActivityCollector.ProbeStatus());

        Console.WriteLine();

        // Admin status
        var isAdmin = System.Security.Principal.WindowsIdentity.GetCurrent()
            .Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ?? false;
        Console.WriteLine($"Administrator:     {(isAdmin ? "Yes" : "No")}");

        // Paths and persistence policy
        Console.WriteLine($"Default DB path:   {DatabaseManager.GetDefaultDatabasePath()}");
        Console.WriteLine($"Default log path:  {DatabaseManager.GetDefaultLogPath()}");
        Console.WriteLine("History policy:    24h raw process/GPU/hardware samples; 7d aggregate history");
        Console.WriteLine("Process history:   Active + TopN persisted for attribution, not a per-process wattmeter");
        Console.WriteLine("GPU history:       Non-zero Intel GPU Engine activity persisted when GPU sampling is enabled");

        Console.WriteLine();
        Console.WriteLine("Diagnose complete.");
        return 0;
    }

    private static void PrintStatus(SourceStatus status)
    {
        var suffix = string.IsNullOrWhiteSpace(status.Details)
            ? string.Empty
            : $" ({status.Details})";
        Console.WriteLine($"  {status.SourceName}: {status.Status}{suffix}");
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
        Console.WriteLine("Storage:   24h raw high-volume samples + 7d aggregate history; process rows use active + TopN persistence.");
        Console.WriteLine("Press Ctrl+C to stop early.");
        Console.WriteLine();

        using var singleInstanceGuard = new SingleInstanceGuard();
        if (!singleInstanceGuard.TryAcquire())
        {
            Console.Error.WriteLine("PowerCulprit is already running.");
            return 1;
        }

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
        services.AddSingleton<WindowsEtwActivityCollector>();
        services.AddSingleton<IWindowsEtwActivityCollector>(sp => sp.GetRequiredService<WindowsEtwActivityCollector>());
        services.AddSingleton<WmiActivityCollector>();
        services.AddSingleton<IWmiActivityCollector>(sp => sp.GetRequiredService<WmiActivityCollector>());
        services.AddSingleton<MonitoringService>();

        // await using so the ServiceProvider is disposed on exit (normal,
        // Ctrl+C, or fatal). That runs Dispose on the singleton IDisposable
        // collectors (LHM Computer.Close, GPU Engine PDH counters, ETW session)
        // — without it every headless run leaks those native handles.
        await using var provider = services.BuildServiceProvider();
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
