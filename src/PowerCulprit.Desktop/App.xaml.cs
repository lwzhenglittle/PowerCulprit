using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PowerCulprit.Collectors;
using PowerCulprit.Core.Services;
using PowerCulprit.Desktop.Logging;
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
    private int _shutdownStarted;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _monitorStartTask;
    private Task? _initialHistoryTask;

    public App()
    {
        InitializeComponent();

        // Install global crash handlers as early as possible. Desktop is a
        // WinExe with no console and there are many fire-and-forget tasks
        // (monitoring start, history load, tray menu actions) whose
        // unobserved exceptions would otherwise vanish. Each writes to
        // %LocalAppData%\PowerCulprit\logs\crash-*.log via CrashLog, which has
        // no dependency on the DI logging pipeline and never throws.
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception, "UnhandledException");
        e.Handled = true; // keep the app alive so the log actually flushes
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved(); // do not crash the process over a forgotten task
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
        _lifetimeCts = new CancellationTokenSource();

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

        _monitorStartTask = mainViewModel.StartMonitoringCommand.ExecuteAsync(null);
        _initialHistoryTask = LoadInitialHistoryAfterFirstFrameAsync(
            mainViewModel,
            _window.DispatcherQueue,
            _lifetimeCts.Token);
    }

    private static async Task LoadInitialHistoryAfterFirstFrameAsync(
        MainViewModel viewModel,
        DispatcherQueue dispatcher,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        return;
                    }

                    await viewModel.LatestCommand.ExecuteAsync(null);
                    completion.TrySetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            {
                return;
            }

            // Await the dispatched command so shutdown can wait for an already
            // started load instead of disposing its dependencies underneath it.
            await completion.Task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal during application shutdown.
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "InitialHistoryLoad");
        }
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
            StartShutdown(closeWindowOnUiThread: false);
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            StartShutdown(closeWindowOnUiThread: true);
            return;
        }

        if (!dispatcher.TryEnqueue(() => StartShutdown(closeWindowOnUiThread: true)))
        {
            // The UI queue may already be shutting down. Do not silently lose
            // the tray Exit command: clean up non-UI resources and terminate.
            StartShutdown(closeWindowOnUiThread: false);
        }
    }

    private void StartShutdown(bool closeWindowOnUiThread)
    {
        if (closeWindowOnUiThread)
        {
            _ = RunShutdownAndObserveAsync(closeWindowOnUiThread: true);
        }
        else
        {
            // The fallback can be called from the tray STA thread. Run it on
            // the pool so TrayManager.Dispose() never tries to join itself.
            _ = Task.Run(() => RunShutdownAndObserveAsync(closeWindowOnUiThread: false));
        }
    }

    private async Task RunShutdownAndObserveAsync(bool closeWindowOnUiThread)
    {
        try
        {
            await DoExitAsync(closeWindowOnUiThread);
        }
        catch (Exception ex)
        {
            // DoExitAsync protects each cleanup stage, but keep a final guard
            // around the fire-and-forget entry point so an unexpected failure
            // cannot leave a hidden process holding the single-instance lock.
            CrashLog.Write(ex, "ApplicationShutdown");
            Environment.Exit(1);
        }
    }

    private async Task DoExitAsync(bool closeWindowOnUiThread)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;
        var window = _window as MainWindow;
        try
        {
            try { _lifetimeCts?.Cancel(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.CancelStartup"); }

            var initialHistoryTask = Interlocked.Exchange(ref _initialHistoryTask, null);
            if (initialHistoryTask is not null)
            {
                try
                {
                    await initialHistoryTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException ex)
                {
                    CrashLog.Write(ex, "ApplicationShutdown.InitialHistoryTimeout");
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "ApplicationShutdown.InitialHistory");
                }
            }

            var monitorStartTask = Interlocked.Exchange(ref _monitorStartTask, null);
            if (monitorStartTask is not null)
            {
                try
                {
                    await monitorStartTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException ex)
                {
                    CrashLog.Write(ex, "ApplicationShutdown.MonitorStartTimeout");
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "ApplicationShutdown.MonitorStart");
                }
            }

            try { _trayManager?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.Tray"); }
            _trayManager = null;

            // Detach power state monitor before stopping monitoring
            try { _powerStateMonitor?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.PowerStateMonitor"); }
            _powerStateMonitor = null;

            // Stop monitoring before disposing the service provider.
            try
            {
                var monitor = _serviceProvider?.GetService<IMonitoringService>();
                if (monitor is not null)
                    await monitor.StopAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "ApplicationShutdown.Monitoring");
            }
        }
        finally
        {
            // Every cleanup stage above is best effort. This finally block is
            // the last line of defense if an unexpected stage throws, and it
            // guarantees that no hidden instance keeps the mutex alive.
            try { _trayManager?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.Tray.Finally"); }
            _trayManager = null;

            try { _powerStateMonitor?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.PowerStateMonitor.Finally"); }
            _powerStateMonitor = null;

            try { _serviceProvider?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.Services"); }
            _serviceProvider = null;

            try { _singleInstanceGuard?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.SingleInstance"); }
            _singleInstanceGuard = null;

            try { _lifetimeCts?.Dispose(); }
            catch (Exception ex) { CrashLog.Write(ex, "ApplicationShutdown.Lifetime"); }
            _lifetimeCts = null;

            _window = null;
            if (closeWindowOnUiThread && window is not null)
            {
                try
                {
                    window.CloseForReal();
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "ApplicationShutdown.Window");
                    Environment.Exit(1);
                }
            }
            else
            {
                // No usable UI dispatcher remains, so there is no safe way to
                // invoke Window.Close from this thread. All owned resources have
                // already been released; terminate instead of leaving a zombie.
                Environment.Exit(0);
            }
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
            // Desktop is a WinExe with no console, so AddConsole() alone writes
            // nowhere. The file provider persists everything to
            // %LocalAppData%\PowerCulprit\logs\desktop-YYYYMMDD.log so crashes
            // and sampling failures leave a trail.
            builder.AddProvider(new FileLoggerProvider(DatabaseManager.GetDefaultLogPath()));
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
        services.AddSingleton<HistorySelectionState>();
        services.AddSingleton<CpuAttributionViewModel>();
        services.AddSingleton<WmiAttributionViewModel>();
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());

        return services.BuildServiceProvider();
    }
}
