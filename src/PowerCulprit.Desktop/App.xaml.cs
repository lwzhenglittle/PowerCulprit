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
    private PowerStateMonitor? _powerStateMonitor;
    private SingleInstanceGuard? _singleInstanceGuard;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));

        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.TryAcquire())
        {
            SingleInstanceGuard.SignalExistingInstance();
            _singleInstanceGuard.Dispose();
            _singleInstanceGuard = null;
            Exit();
            return;
        }

        _serviceProvider = BuildServiceProvider();

        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        var cpuViewModel = _serviceProvider.GetRequiredService<CpuAttributionViewModel>();
        var wmiViewModel = _serviceProvider.GetRequiredService<WmiAttributionViewModel>();
        _window = new MainWindow(mainViewModel, cpuViewModel, wmiViewModel);
        _singleInstanceGuard.StartShowWindowListener(ShowWindow);

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

        // Attach power state monitor to receive suspend/resume notifications
        try
        {
            var psLogger = _serviceProvider.GetRequiredService<ILogger<PowerStateMonitor>>();
            _powerStateMonitor = new PowerStateMonitor(
                (Window)_window,
                monitor,
                psLogger);
        }
        catch (Exception ex)
        {
            var fallbackLogger = _serviceProvider.GetService<ILogger<App>>();
            fallbackLogger?.LogWarning(ex,
                "PowerStateMonitor failed to initialize — sleep/hibernate detection disabled");
        }

        _window.Activate();

        _ = mainViewModel.StartMonitoringCommand.ExecuteAsync(null);
        _ = LoadInitialHistoryAfterFirstFrameAsync(mainViewModel, _window.DispatcherQueue);
    }

    private static async Task LoadInitialHistoryAfterFirstFrameAsync(
        MainViewModel viewModel,
        DispatcherQueue dispatcher)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        dispatcher.TryEnqueue(() => _ = viewModel.LatestCommand.ExecuteAsync(null));
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
            _ = DoExitOnUiThreadAsync();
            return;
        }

        if (dispatcher.HasThreadAccess)
            _ = DoExitOnUiThreadAsync();
        else
            dispatcher.TryEnqueue(() => _ = DoExitOnUiThreadAsync());
    }

    private async Task DoExitOnUiThreadAsync()
    {
        if (_isExiting)
            return;
        _isExiting = true;

        _trayManager?.Dispose();
        _trayManager = null;

        // Detach power state monitor before stopping monitoring
        _powerStateMonitor?.Dispose();
        _powerStateMonitor = null;

        // Stop monitoring
        try
        {
            var monitor = _serviceProvider?.GetService<IMonitoringService>();
            if (monitor is not null)
                await monitor.StopAsync();
        }
        catch { /* best effort */ }

        // Dispose tray BEFORE closing window
        _trayManager = null;

        // Dispose DI container — must happen before final window close so
        // singletons get a chance to release native handles (LHM, PDH, etc.).
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;

        if (_window is MainWindow window)
        {
            window.CloseForReal();
            _window = null;
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
        services.AddSingleton<WmiActivityCollector>();
        services.AddSingleton<IWmiActivityCollector>(sp => sp.GetRequiredService<WmiActivityCollector>());

        services.AddSingleton<MonitoringService>();
        services.AddSingleton<IMonitoringService>(sp => sp.GetRequiredService<MonitoringService>());

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<CpuAttributionViewModel>();
        services.AddSingleton<WmiAttributionViewModel>();
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());

        return services.BuildServiceProvider();
    }
}
