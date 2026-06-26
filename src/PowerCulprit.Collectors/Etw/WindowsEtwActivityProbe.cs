using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>Short-lived ETW checks used by --diagnose.</summary>
public static class WindowsEtwActivityProbe
{
    public static Task<IReadOnlyList<SourceStatus>> ProbeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<SourceStatus>();
        var sessionName = $"PowerCulprit-ETW-Probe-{Environment.ProcessId}";
        TraceEventSession? session = null;
        var processEnabled = false;
        var tcpEnabled = false;

        try
        {
            session = new TraceEventSession(sessionName)
            {
                StopOnDispose = true
            };

            statuses.Add(Status("ETW Kernel Session", true, "Available", "Probe session created", false));

            try
            {
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Process |
                    KernelTraceEventParser.Keywords.NetworkTCPIP);
                processEnabled = true;
                tcpEnabled = true;
                statuses.Add(Status("ETW Process Events", true, "Available", "Process provider enabled; event payload parsing is checked during monitoring", false));
                statuses.Add(Status("ETW TCPIP Events", true, "Available", "TCP/IP provider enabled for TCP and UDP events; payload parsing is checked during monitoring", false));
            }
            catch (Exception ex)
            {
                var processStatus = ProviderFailureStatus("ETW Process Events", ex);
                var tcpStatus = ProviderFailureStatus("ETW TCPIP Events", ex);
                statuses.Add(processStatus);
                statuses.Add(tcpStatus);
            }

            if (!processEnabled && !tcpEnabled && statuses.Count == 1)
            {
                statuses.Add(Status("ETW Process Events", false, "Unavailable", "Process provider did not enable", null));
                statuses.Add(Status("ETW TCPIP Events", false, "Unavailable", "TCP/IP provider did not enable", null));
            }
        }
        catch (Exception ex) when (LooksLikeAccessDenied(ex))
        {
            statuses.Clear();
            statuses.Add(Status("ETW Kernel Session", false, "Requires admin", ex.Message, true));
            statuses.Add(Status("ETW Process Events", false, "Requires admin", "Kernel ETW session requires administrator privileges", true));
            statuses.Add(Status("ETW TCPIP Events", false, "Requires admin", "Kernel ETW session requires administrator privileges", true));
        }
        catch (Exception ex)
        {
            statuses.Clear();
            statuses.Add(Status("ETW Kernel Session", false, "Unavailable", ex.Message, null));
            statuses.Add(Status("ETW Process Events", false, "Unavailable", "Kernel ETW session unavailable", null));
            statuses.Add(Status("ETW TCPIP Events", false, "Unavailable", "Kernel ETW session unavailable", null));
        }
        finally
        {
            try { session?.Stop(); } catch { /* best effort */ }
            try { session?.Dispose(); } catch { /* best effort */ }
        }

        return Task.FromResult<IReadOnlyList<SourceStatus>>(statuses);
    }

    private static SourceStatus ProviderFailureStatus(string sourceName, Exception ex)
    {
        if (LooksLikeAccessDenied(ex))
            return Status(sourceName, false, "Requires admin", ex.Message, true);

        return Status(sourceName, false, "Partial", ex.Message, null);
    }

    private static SourceStatus Status(
        string sourceName,
        bool available,
        string status,
        string? details,
        bool? requiresAdmin) => new()
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = sourceName,
            IsAvailable = available,
            Status = status,
            Details = details,
            RequiresAdmin = requiresAdmin
        };

    private static bool LooksLikeAccessDenied(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current is UnauthorizedAccessException)
                return true;
            if (current.HResult == unchecked((int)0x80070005))
                return true;
            if (current.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                return true;
            if (current.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
