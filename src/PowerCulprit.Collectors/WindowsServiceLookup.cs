using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PowerCulprit.Collectors;

/// <summary>
/// Wraps the Windows Service Control Manager to answer "what services live in PID N?"
///
/// One <c>EnumServicesStatusEx</c> call returns the full snapshot of installed
/// SERVICE_WIN32 services in any state and includes each service's host PID.
/// We filter to services that have a non-zero process id (i.e., currently running)
/// and bucket by PID. Multiple services can share one svchost.exe process — the
/// classic case is the LocalServiceNetworkRestricted group on memory-constrained
/// machines — so the value is a list, joined with ", " when stamped onto a sample.
///
/// SCM query access (SC_MANAGER_ENUMERATE_SERVICE) does not require admin on
/// standard Windows installs, so this works for an unelevated PowerCulprit run.
/// </summary>
public class WindowsServiceLookup
{
    private readonly ILogger<WindowsServiceLookup>? _logger;

    public WindowsServiceLookup(ILogger<WindowsServiceLookup>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns a snapshot mapping PID → service name(s) for every running
    /// Win32 service. Returns an empty map on failure rather than throwing so
    /// callers can degrade gracefully.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> GetServicesByPid()
    {
        var result = new Dictionary<int, List<string>>();

        var scm = NativeMethods.OpenSCManager(
            null, null,
            NativeMethods.SC_MANAGER_CONNECT | NativeMethods.SC_MANAGER_ENUMERATE_SERVICE);

        if (scm == IntPtr.Zero)
        {
            _logger?.LogDebug(
                "OpenSCManager failed (error {Code}); service-to-PID map will be empty",
                Marshal.GetLastWin32Error());
            return EmptyMap;
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            // Probe first to learn the required buffer size, then allocate and call again.
            // We don't reuse a static buffer across calls because the service list size
            // changes (services come and go) and this is invoked at most once per cache
            // refresh interval (~30 s) — allocation pressure is negligible.
            uint resumeHandle = 0;
            uint bytesNeeded = 0;
            uint servicesReturned;

            var probed = NativeMethods.EnumServicesStatusEx(
                scm,
                NativeMethods.SC_ENUM_PROCESS_INFO,
                NativeMethods.SERVICE_WIN32,
                NativeMethods.SERVICE_STATE_ALL,
                IntPtr.Zero, 0,
                out bytesNeeded, out servicesReturned, ref resumeHandle, null);

            // First call MUST fail with ERROR_MORE_DATA, telling us how big to allocate.
            // Anything else means a real error.
            var lastError = Marshal.GetLastWin32Error();
            if (probed || (lastError != NativeMethods.ERROR_MORE_DATA && bytesNeeded == 0))
            {
                _logger?.LogDebug(
                    "EnumServicesStatusEx size probe gave unexpected result (probed={Probed}, err={Err}, needed={Needed})",
                    probed, lastError, bytesNeeded);
                return EmptyMap;
            }

            buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
            resumeHandle = 0;

            if (!NativeMethods.EnumServicesStatusEx(
                    scm,
                    NativeMethods.SC_ENUM_PROCESS_INFO,
                    NativeMethods.SERVICE_WIN32,
                    NativeMethods.SERVICE_STATE_ALL,
                    buffer, bytesNeeded,
                    out _, out servicesReturned, ref resumeHandle, null))
            {
                _logger?.LogDebug(
                    "EnumServicesStatusEx real call failed (error {Code})",
                    Marshal.GetLastWin32Error());
                return EmptyMap;
            }

            var structSize = Marshal.SizeOf<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>();
            for (uint i = 0; i < servicesReturned; i++)
            {
                var entryPtr = IntPtr.Add(buffer, checked((int)i * structSize));
                var entry = Marshal.PtrToStructure<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>(entryPtr);

                var pid = entry.ServiceStatusProcess.dwProcessId;
                if (pid == 0) continue; // service is not currently running

                var name = entry.lpServiceName != IntPtr.Zero
                    ? Marshal.PtrToStringUni(entry.lpServiceName)
                    : null;
                if (string.IsNullOrEmpty(name)) continue;

                if (!result.TryGetValue((int)pid, out var list))
                {
                    list = new List<string>(1);
                    result[(int)pid] = list;
                }
                list.Add(name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WindowsServiceLookup.GetServicesByPid threw");
            return EmptyMap;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            NativeMethods.CloseServiceHandle(scm);
        }

        // Stabilise ordering so that "Dnscache, NlaSvc" stays the same between
        // cycles for the same group — keeps the analyzer's group key deterministic.
        foreach (var list in result.Values)
            list.Sort(StringComparer.OrdinalIgnoreCase);

        return result.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value);
    }

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> EmptyMap
        = new Dictionary<int, IReadOnlyList<string>>();
}
