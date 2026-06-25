using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Services;

namespace PowerCulprit.Desktop;

/// <summary>
/// System tray icon with context menu using Win32 Shell_NotifyIcon.
/// Runs a dedicated message-only window on a background thread.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly IMonitoringService _monitor;
    private readonly ILogger<TrayManager> _logger;
    private readonly Action _showWindow;
    private readonly Action _exitApplication;

    [ThreadStatic]
    private static TrayManager? _current;

    private Thread? _msgThread;
    private IntPtr _trayHwnd;
    private uint _taskbarRestartMsg;
    private volatile bool _running;
    private bool _disposed;

    private const int WM_TRAYICON = 0x8000;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CREATE = 0x0001;

    public TrayManager(
        IMonitoringService monitor,
        ILogger<TrayManager> logger,
        Action showWindow,
        Action exitApplication)
    {
        _monitor = monitor;
        _logger = logger;
        _showWindow = showWindow;
        _exitApplication = exitApplication;
    }

    public void Start()
    {
        _msgThread = new Thread(RunMessageLoop)
        {
            Name = "TrayIcon",
            IsBackground = true
        };
        _msgThread.SetApartmentState(ApartmentState.STA);
        _msgThread.Start();
    }

    private unsafe void RunMessageLoop()
    {
        _current = this;
        _taskbarRestartMsg = Native.RegisterWindowMessage("TaskbarCreated");

        var wndProc = new Native.WndProc(StaticWndProc);
        var handle = GCHandle.Alloc(wndProc);
        var wndProcPtr = Marshal.GetFunctionPointerForDelegate(wndProc);

        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = wndProcPtr,
            hInstance = Marshal.GetHINSTANCE(typeof(TrayManager).Module),
            lpszClassName = "PowerCulpritTrayWnd"
        };

        var atom = Native.RegisterClassExW(ref wc);
        if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

        _trayHwnd = Native.CreateWindowExW(
            0, "PowerCulpritTrayWnd", "", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_trayHwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        AddTrayIcon();
        _running = true;

        while (_running)
        {
            if (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            else break;
        }

        RemoveTrayIcon();
        Native.DestroyWindow(_trayHwnd);
        handle.Free();
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is null) return Native.DefWindowProcW(hWnd, msg, wParam, lParam);

        if (msg == (uint)WM_TRAYICON)
        {
            var lp = (int)lParam;
            if (lp == 0x0203 || lp == 0x0202) // double-click or left-click
                self._showWindow();
            else if (lp == 0x0205) // right-click
                self.ShowContextMenu();
            return IntPtr.Zero;
        }

        if (msg == self._taskbarRestartMsg && self._taskbarRestartMsg != 0)
        {
            self.RemoveTrayIcon();
            self.AddTrayIcon();
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            Native.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = Native.CreatePopupMenu();
        var running = _monitor.IsRunning;

        const uint mfGrayed = 0x0800;
        Native.AppendMenuW(menu, 0u, 1, "Show Dashboard");
        Native.AppendMenuW(menu, running ? mfGrayed : 0u, 2, "Start Monitoring");
        Native.AppendMenuW(menu, running ? 0u : mfGrayed, 3, "Stop Monitoring");
        Native.AppendMenuW(menu, mfGrayed, 0, null);
        Native.AppendMenuW(menu, 0u, 4, "Export Data");
        Native.AppendMenuW(menu, mfGrayed, 0, null);
        Native.AppendMenuW(menu, 0u, 5, "Exit");

        Native.SetForegroundWindow(_trayHwnd);
        Native.GetCursorPos(out var pt);
        var cmd = Native.TrackPopupMenu(menu, 0x0100 | 0x0002, pt.X, pt.Y, 0, _trayHwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);

        switch (cmd)
        {
            case 1: _showWindow(); break;
            case 2: _ = Task.Run(() => _monitor.StartAsync()); break;
            case 3: _ = Task.Run(() => _monitor.StopAsync()); break;
            case 4: OnExportRequested?.Invoke(); break;
            case 5: _exitApplication(); break;
        }
    }

    private unsafe void AddTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var hIcon = File.Exists(iconPath)
            ? Native.ExtractIconW(IntPtr.Zero, iconPath, 0)
            : Native.LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION

        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _trayHwnd,
            uID = 1,
            uFlags = 0x01 | 0x02 | 0x10,
            uCallbackMessage = (uint)WM_TRAYICON,
            hIcon = hIcon,
            szTip = "PowerCulprit"
        };

        Native.Shell_NotifyIconW(0, ref nid);
    }

    private void RemoveTrayIcon()
    {
        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _trayHwnd,
            uID = 1
        };
        Native.Shell_NotifyIconW(2, ref nid);
    }

    public event Action? OnExportRequested;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        if (_trayHwnd != IntPtr.Zero)
            Native.PostMessageW(_trayHwnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
    }

    // ── Native API ────────────────────────────────

    private static unsafe class Native
    {
        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateWindowExW(
            uint dwExStyle, [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
            IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern IntPtr PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem,
            [MarshalAs(UnmanagedType.LPWStr)] string? lpNewItem);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
            int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("shell32.dll")]
        public static extern IntPtr ExtractIconW(IntPtr hInst,
            [MarshalAs(UnmanagedType.LPWStr)] string lpszExeFileName, uint nIconIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        public struct POINT { public int X; public int Y; }

        public struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }
    }
}
