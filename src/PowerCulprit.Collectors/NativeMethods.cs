using System.Runtime.InteropServices;

namespace PowerCulprit.Collectors;

/// <summary>
/// P/Invoke declarations for Win32/NT APIs used by collectors.
/// </summary>
internal static class NativeMethods
{
    public const int SystemProcessInformationClass = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public IntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder lpExeName,
        ref uint lpdwSize);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ──────────────────────────────────────────────
    //  Service Control Manager — used by WindowsServiceLookup to map
    //  running services back to their host PIDs (mainly for svchost).
    // ──────────────────────────────────────────────

    public const uint SC_MANAGER_CONNECT          = 0x0001;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    public const uint SERVICE_WIN32        = 0x00000030;
    public const uint SERVICE_STATE_ALL    = 0x00000003;
    public const int  SC_ENUM_PROCESS_INFO = 0;
    public const int  ERROR_MORE_DATA      = 234;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(
        string? lpMachineName,
        string? lpDatabaseName,
        uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    // Pre-call: pcbBytesNeeded out tells us how big the buffer needs to be.
    // We always pass the buffer and length; the function returns false +
    // ERROR_MORE_DATA the first time so the caller knows to resize.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumServicesStatusEx(
        IntPtr hSCManager,
        int InfoLevel,                // SC_ENUM_PROCESS_INFO
        uint dwServiceType,            // SERVICE_WIN32 (or | with driver bits)
        uint dwServiceState,           // SERVICE_STATE_ALL / _ACTIVE / _INACTIVE
        IntPtr lpServices,             // ENUM_SERVICE_STATUS_PROCESS[] (packed)
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    // Mirrors SERVICE_STATUS_PROCESS — only dwProcessId matters for our use.
    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    // CharSet=Unicode + LayoutKind.Sequential makes the two LPWSTR pointers
    // marshal correctly. We only consume the C-string pointers, so the actual
    // memory is owned by the buffer returned from EnumServicesStatusEx.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }
}
