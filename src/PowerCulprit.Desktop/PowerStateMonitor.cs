using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PowerCulprit.Core.Models;
using PowerCulprit.Core.Services;

namespace PowerCulprit.Desktop;

/// <summary>
/// Subclasses the main WinUI window to receive WM_POWERBROADCAST and
/// record suspend/resume power state events through IMonitoringService.
/// </summary>
public sealed class PowerStateMonitor : IDisposable
{
    private readonly IMonitoringService _monitor;
    private readonly ILogger<PowerStateMonitor> _logger;
    private readonly IntPtr _hwnd;
    private readonly Window _window;

    // Keep delegates alive — they're passed to native code.
    private readonly Native.SubclassProc _subclassProc;

    private nint _subclassId;
    private GCHandle _procHandle;
    private bool _disposed;

    // Power broadcast constants
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    public PowerStateMonitor(
        Window window,
        IMonitoringService monitor,
        ILogger<PowerStateMonitor> logger)
    {
        _window = window;
        _monitor = monitor;
        _logger = logger;
        _hwnd = GetWindowHandle(window);

        _subclassProc = SubclassProc;

        // Prevent the delegate from being GC'd while native code holds a reference.
        _procHandle = GCHandle.Alloc(_subclassProc, GCHandleType.Normal);

        if (!Native.SetWindowSubclass(
                _hwnd,
                _subclassProc,
                _subclassId,
                IntPtr.Zero))
        {
            _procHandle.Free();
            throw new InvalidOperationException(
                $"SetWindowSubclass failed. Last error: {Marshal.GetLastWin32Error()}");
        }

        _subclassId = 0;
        _logger.LogInformation("PowerStateMonitor attached to HWND 0x{Handle:X}", _hwnd);
    }

    private IntPtr SubclassProc(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr uIdSubclass,
        IntPtr dwRefData)
    {
        try
        {
            if (msg == WM_POWERBROADCAST)
            {
                var powerEvent = wParam.ToInt32();
                HandlePowerBroadcast(powerEvent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in PowerStateMonitor subclass proc");
        }

        return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void HandlePowerBroadcast(int powerEvent)
    {
        switch (powerEvent)
        {
            case PBT_APMSUSPEND:
                _logger.LogInformation("WM_POWERBROADCAST: PBT_APMSUSPEND");
                // Fire-and-forget — the monitoring service handles the short timeout.
                _ = _monitor.RecordPowerStateEventAsync(
                    PowerStateEventKind.Suspend,
                    details: "PBT_APMSUSPEND (0x0004)");
                break;

            case PBT_APMRESUMESUSPEND:
                _logger.LogInformation("WM_POWERBROADCAST: PBT_APMRESUMESUSPEND");
                _ = _monitor.RecordPowerStateEventAsync(
                    PowerStateEventKind.Resume,
                    details: "PBT_APMRESUMESUSPEND (0x0007)");
                break;

            case PBT_APMRESUMEAUTOMATIC:
                _logger.LogInformation("WM_POWERBROADCAST: PBT_APMRESUMEAUTOMATIC");
                _ = _monitor.RecordPowerStateEventAsync(
                    PowerStateEventKind.ResumeAutomatic,
                    details: "PBT_APMRESUMEAUTOMATIC (0x0012)");
                break;

            default:
                _logger.LogDebug(
                    "WM_POWERBROADCAST: unknown power event 0x{Event:X4}",
                    powerEvent);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                Native.RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove window subclass");
        }

        if (_procHandle.IsAllocated)
            _procHandle.Free();
    }

    private static IntPtr GetWindowHandle(Window window)
    {
        // In WinUI 3 (Windows App SDK) the Window object implements
        // IWindowNative via COM — use COM interop to retrieve the HWND.
        var windowNative = (IWindowNative)window;
        var handle = windowNative.WindowHandle;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "Failed to get WinUI Window HWND for PowerStateMonitor");
        return handle;
    }

    // ── COM interop for IWindowNative ─────────────

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("EECDBF0E-BAE9-4CB6-A68E-9598E1CB57BB")]
    private interface IWindowNative
    {
        IntPtr WindowHandle { get; }
    }

    // ── Native API ────────────────────────────────

    private static class Native
    {
        public delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            IntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam);
    }
}
