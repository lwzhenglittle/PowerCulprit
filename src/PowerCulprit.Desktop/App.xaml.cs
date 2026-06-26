using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PowerCulprit.Collectors;
using PowerCulprit.Core.Services;
using PowerCulprit.Desktop.ViewModels;
using PowerCulprit.Storage;

namespace PowerCulprit.Desktop;

public partial class App : Application
{
    private Window? _window;
    private ServiceProvider? _serviceProvider;
    private TrayManager? _trayManager;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _serviceProvider = BuildServiceProvider();

        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        _window = new MainWindow(mainViewModel);

        // Start tray icon
        var monitor = _serviceProvider.GetRequiredService<IMonitoringService>();
        var logger = _serviceProvider.GetRequiredService<ILogger<TrayManager>>();
        _trayManager = new TrayManager(
            monitor,
            logger,
            ShowWindow,
            ExitApplication);
        _trayManager.OnExportRequested += () => _ = ExportDataAsync();
        _trayManager.Start();

        _window.Activate();

        // Begin monitoring immediately; the UI reads historical data from SQLite.
        _ = mainViewModel.StartMonitoringCommand.ExecuteAsync(null);
        _ = mainViewModel.LatestCommand.ExecuteAsync(null);
    }

    private void ShowWindow()
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
        {
            DoShowWindow();
            return;
        }

        if (dispatcher.HasThreadAccess)
            DoShowWindow();
        else
            dispatcher.TryEnqueue(DoShowWindow);
    }

    private void DoShowWindow()
    {
        if (_window is MainWindow mw)
        {
            mw.ShowFromTray();
        }
    }

    private void ExitApplication()
    {
        // Tray menu callbacks run on the tray's STA thread, but Window.Close()
        // (and most WinUI APIs) require the UI thread. Marshal the whole exit
        // sequence onto the window's dispatcher so the final close doesn't
        // throw RPC_E_WRONG_THREAD.
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
        {
            DoExitOnUiThread();
            return;
        }

        if (dispatcher.HasThreadAccess)
            DoExitOnUiThread();
        else
            dispatcher.TryEnqueue(DoExitOnUiThread);
    }

    private void DoExitOnUiThread()
    {
        // Stop monitoring
        try
        {
            var monitor = _serviceProvider?.GetService<IMonitoringService>();
            monitor?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* best effort */ }

        // Dispose tray BEFORE closing window
        _trayManager?.Dispose();
        _trayManager = null;

        // Dispose DI container — must happen before final window close so
        // singletons get a chance to release native handles (LHM, PDH, etc.).
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        // Actually close the window
        if (_window is MainWindow mw)
        {
            mw.CloseForReal();
        }
    }

    private async Task ExportDataAsync()
    {
        try
        {
            var dispatcher = _window?.DispatcherQueue;
            if (dispatcher is null || dispatcher.HasThreadAccess)
            {
                await DoExportDataAsync();
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await DoExportDataAsync();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue CSV export on the UI thread."));
            }

            await tcs.Task;
        }
        catch { /* logged inside the command */ }
    }

    private async Task DoExportDataAsync()
    {
        var viewModel = _serviceProvider?.GetService<MainViewModel>();
        if (viewModel is not null)
        {
            await viewModel.ExportCsvCommand.ExecuteAsync(null);
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddConsole();
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
#else
            builder.SetMinimumLevel(LogLevel.Warning);
#endif
        });

        services.AddSingleton<DatabaseManager>();
        services.AddSingleton<BatteryPowerCollector>();
        services.AddSingleton<ProcessResourceCollector>();
        services.AddSingleton<WindowsGpuEngineCollector>();
        services.AddSingleton<LibreHardwareMonitorCollector>();
        services.AddSingleton<IntelCpuPowerCollector>();
        services.AddSingleton<IntelGpuPowerCollector>();
        services.AddSingleton<WindowsEtwActivityCollector>();
        services.AddSingleton<IWindowsEtwActivityCollector>(sp => sp.GetRequiredService<WindowsEtwActivityCollector>());

        services.AddSingleton<MonitoringService>();
        services.AddSingleton<IMonitoringService>(sp => sp.GetRequiredService<MonitoringService>());

        services.AddSingleton<MainViewModel>();
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());

        return services.BuildServiceProvider();
    }
}
