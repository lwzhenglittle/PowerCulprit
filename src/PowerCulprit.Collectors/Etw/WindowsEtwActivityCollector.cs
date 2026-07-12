using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Optional ETW collector for process lifecycle and TCP/IP byte activity.
/// Failure to start ETW degrades to an empty snapshot and a SourceStatus entry.
/// </summary>
public sealed class WindowsEtwActivityCollector : IWindowsEtwActivityCollector, IDisposable
{
    private readonly ILogger<WindowsEtwActivityCollector> _logger;
    private readonly EtwProcessActivityAccumulator _accumulator = new();
    private readonly object _lock = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private CancellationTokenSource? _cts;
    private SourceStatus _status = DisabledStatus("ETW collector has not been started");
    private bool _processProviderEnabled;
    private bool _networkProviderEnabled;
    private bool _udpProviderEnabled;
    private long _parseErrors;
    private EtwActivityCounters _lastCounters = new();
    private int _disposed;

    public WindowsEtwActivityCollector(ILogger<WindowsEtwActivityCollector> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_processingTask is not null)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _processingTask = Task.Run(() => RunEtwSession(_cts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? processingTask;
        TraceEventSession? session;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            processingTask = _processingTask;
            session = _session;
            cts = _cts;
            _processingTask = null;
            _session = null;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { /* best effort */ }
        try { session?.Stop(); } catch (Exception ex) { _logger.LogDebug(ex, "Stopping ETW session failed"); }

        if (processingTask is not null)
        {
            try { await processingTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException ex) { _logger.LogWarning(ex, "Timed out waiting for ETW collector to stop"); }
            catch (Exception ex) { _logger.LogWarning(ex, "ETW collector stopped with an error"); }
        }

        try { session?.Dispose(); } catch { /* best effort */ }
        cts?.Dispose();

        lock (_lock)
        {
            _status = DisabledStatus("ETW collector is stopped");
            _processProviderEnabled = false;
            _networkProviderEnabled = false;
            _udpProviderEnabled = false;
        }
    }

    public EtwProcessActivitySnapshot SnapshotAndReset(DateTime nowUtc)
    {
        try
        {
            var snapshot = _accumulator.SnapshotAndReset(nowUtc);
            lock (_lock)
            {
                _lastCounters = snapshot.Counters;
                if (_session is not null)
                    UpdateRunningStatusUnderLock();
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW activity snapshot failed");
            return EtwProcessActivitySnapshot.Empty;
        }
    }

    public void RecordPolledProcess(int pid)
    {
        _accumulator.RecordPolledProcess(pid);
    }

    public SourceStatus GetStatus()
    {
        lock (_lock) { return _status; }
    }

    public void Dispose()
    {
        // Guard against double-dispose: the DI container disposes singletons on
        // teardown, and MonitoringService.StopAsync already stopped the ETW
        // session — a second Dispose would re-run the sync-over-async stop for
        // nothing. Interlocked.Exchange makes the guard thread-safe.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { StopAsync().GetAwaiter().GetResult(); }
        catch { /* best effort */ }
    }

    private void RunEtwSession(CancellationToken cancellationToken)
    {
        var sessionName = $"PowerCulprit-ETW-{Environment.ProcessId}";
        TraceEventSession? session = null;

        try
        {
            var cleanup = WindowsEtwSessionCleanup.StopPreviousPowerCulpritSessions(_logger);
            if (cleanup.Found > 0)
            {
                _logger.LogInformation(
                    "PowerCulprit ETW session cleanup found {Found}, stopped {Stopped}, failed {Failed}",
                    cleanup.Found,
                    cleanup.Stopped,
                    cleanup.Failed);
            }

            session = new TraceEventSession(sessionName)
            {
                StopOnDispose = true
            };

            lock (_lock)
            {
                _session = session;
                _status = new SourceStatus
                {
                    TimestampUtc = DateTime.UtcNow,
                    SourceName = "WindowsETW",
                    IsAvailable = false,
                    Status = "Partial",
                    Details = "ETW session created; enabling providers",
                    RequiresAdmin = null
                };
            }

            EnableKernelProviders(session);
            WireEvents(session.Source.Kernel);
            UpdateRunningStatus();

            using var registration = cancellationToken.Register(() =>
            {
                try { session.Stop(); } catch { /* best effort */ }
            });

            session.Source.Process();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "ETW requires administrator privileges");
            SetRequiresAdmin(ex.Message);
        }
        catch (Exception ex) when (LooksLikeAccessDenied(ex))
        {
            _logger.LogWarning(ex, "ETW requires administrator privileges");
            SetRequiresAdmin(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows ETW activity collector is unavailable");
            SetUnavailable(ex.Message);
        }
        finally
        {
            try { session?.Dispose(); } catch { /* best effort */ }
            lock (_lock)
            {
                if (ReferenceEquals(_session, session))
                    _session = null;

                if (!cancellationToken.IsCancellationRequested &&
                    (_status.Status == "Available" || _status.Status == "Partial"))
                {
                    _status = new SourceStatus
                    {
                        TimestampUtc = DateTime.UtcNow,
                        SourceName = "WindowsETW",
                        IsAvailable = false,
                        Status = "Unavailable",
                        Details = "ETW processing stopped",
                        RequiresAdmin = null
                    };
                }
            }
        }
    }

    private void EnableKernelProviders(TraceEventSession session)
    {
        try
        {
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process |
                KernelTraceEventParser.Keywords.NetworkTCPIP);

            lock (_lock)
            {
                _processProviderEnabled = true;
                _networkProviderEnabled = true;
                _udpProviderEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enable ETW Process/Network kernel providers");
            throw;
        }
    }

    private void WireEvents(KernelTraceEventParser kernel)
    {
        kernel.ProcessStart += data => SafeHandle(() =>
        {
            _accumulator.RecordProcessStart(
                data.ProcessID,
                data.ProcessName,
                TryGetStringPayload(data, "ImageFileName") ?? TryGetStringPayload(data, "FileName"),
                TryGetStringPayload(data, "CommandLine"),
                TryGetIntPayload(data, "ParentID") ?? TryGetIntPayload(data, "ParentProcessID"),
                data.TimeStamp.ToUniversalTime());
        });

        kernel.ProcessStop += data => SafeHandle(() =>
        {
            _accumulator.RecordProcessStop(data.ProcessID, data.TimeStamp.ToUniversalTime());
        });

        kernel.TcpIpSend += data => SafeHandle(() => RecordTcpEvent(data, send: true));
        kernel.TcpIpRecv += data => SafeHandle(() => RecordTcpEvent(data, send: false));
        kernel.TcpIpSendIPV6 += data => SafeHandle(() => RecordTcpEvent(data, send: true));
        kernel.TcpIpRecvIPV6 += data => SafeHandle(() => RecordTcpEvent(data, send: false));
        kernel.UdpIpSend += data => SafeHandle(() => RecordUdpEvent(data, send: true));
        kernel.UdpIpRecv += data => SafeHandle(() => RecordUdpEvent(data, send: false));
        kernel.UdpIpSendIPV6 += data => SafeHandle(() => RecordUdpEvent(data, send: true));
        kernel.UdpIpRecvIPV6 += data => SafeHandle(() => RecordUdpEvent(data, send: false));
    }

    private void RecordTcpEvent(TraceEvent data, bool send)
    {
        var pid = TryGetIntPayload(data, "PID")
            ?? TryGetIntPayload(data, "Pid")
            ?? TryGetIntPayload(data, "ProcessId")
            ?? TryGetIntPayload(data, "ProcessID")
            ?? data.ProcessID;
        var bytes = TryGetLongPayload(data, "size")
            ?? TryGetLongPayload(data, "Size")
            ?? TryGetLongPayload(data, "Bytes")
            ?? TryGetLongPayload(data, "TransferSize");

        if (pid <= 0)
        {
            _accumulator.RecordIgnoredNetworkEventInvalidPid();
            return;
        }

        if (!bytes.HasValue || bytes.Value <= 0)
        {
            _accumulator.RecordIgnoredNetworkEventMissingBytes();
            return;
        }

        if (send)
            _accumulator.RecordTcpSend(pid, bytes.Value);
        else
            _accumulator.RecordTcpReceive(pid, bytes.Value);
    }

    private void RecordUdpEvent(TraceEvent data, bool send)
    {
        var pid = TryGetIntPayload(data, "PID")
            ?? TryGetIntPayload(data, "Pid")
            ?? TryGetIntPayload(data, "ProcessId")
            ?? TryGetIntPayload(data, "ProcessID")
            ?? data.ProcessID;
        var bytes = TryGetLongPayload(data, "size")
            ?? TryGetLongPayload(data, "Size")
            ?? TryGetLongPayload(data, "Bytes")
            ?? TryGetLongPayload(data, "TransferSize");

        if (pid <= 0)
        {
            _accumulator.RecordIgnoredNetworkEventInvalidPid();
            return;
        }

        if (!bytes.HasValue || bytes.Value <= 0)
        {
            _accumulator.RecordIgnoredNetworkEventMissingBytes();
            return;
        }

        if (send)
            _accumulator.RecordUdpSend(pid, bytes.Value);
        else
            _accumulator.RecordUdpReceive(pid, bytes.Value);
    }

    private void SafeHandle(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            var errors = Interlocked.Increment(ref _parseErrors);
            _accumulator.RecordParseError();
            if (errors <= 3)
                _logger.LogDebug(ex, "Failed to parse ETW event");
            UpdateRunningStatus();
        }
    }

    private void UpdateRunningStatus()
    {
        lock (_lock)
        {
            UpdateRunningStatusUnderLock();
        }
    }

    private void UpdateRunningStatusUnderLock()
    {
        var enabled = new List<string>();
        var missing = new List<string>();
        if (_processProviderEnabled) enabled.Add("Process"); else missing.Add("Process");
        if (_networkProviderEnabled) enabled.Add("TCP/IP"); else missing.Add("TCP/IP");
        if (_udpProviderEnabled) enabled.Add("UDP/IP"); else missing.Add("UDP/IP");

        var counters = _lastCounters;
        var hasHealthIssues = counters.ParseErrorCount > 0 || counters.LostEventCount > 0 || counters.IgnoredNetworkEvents > 0;
        var status = missing.Count == 0 && !hasHealthIssues ? "Available" : "Partial";
        var details = $"Enabled providers: {string.Join(", ", enabled)}";
        if (missing.Count > 0)
            details += $"; unavailable providers: {string.Join(", ", missing)}";

        details += $"; process starts/stops: {counters.ProcessStartEvents}/{counters.ProcessStopEvents}";
        details += $"; TCP events: {counters.TcpEvents}";
        details += $"; UDP events: {counters.UdpEvents}";
        if (counters.IgnoredNetworkEvents > 0)
            details += $"; ignored network events: {counters.IgnoredNetworkEvents}";
        if (counters.ParseErrorCount > 0)
            details += $"; parse errors: {counters.ParseErrorCount}";
        if (counters.LostEventCount > 0)
            details += $"; lost events: {counters.LostEventCount}";

        _status = new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = "WindowsETW",
            IsAvailable = enabled.Count > 0,
            Status = status,
            Details = details,
            RequiresAdmin = false
        };
    }

    private void SetRequiresAdmin(string? details)
    {
        lock (_lock)
        {
            _status = new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = "WindowsETW",
                IsAvailable = false,
                Status = "Requires admin",
                Details = string.IsNullOrWhiteSpace(details) ? "Kernel ETW session requires administrator privileges" : details,
                RequiresAdmin = true
            };
        }
    }

    private void SetUnavailable(string? details)
    {
        lock (_lock)
        {
            _status = new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = "WindowsETW",
                IsAvailable = false,
                Status = "Unavailable",
                Details = string.IsNullOrWhiteSpace(details) ? "ETW collector unavailable" : details,
                RequiresAdmin = null
            };
        }
    }

    private static SourceStatus DisabledStatus(string details) => new()
    {
        TimestampUtc = DateTime.UtcNow,
        SourceName = "WindowsETW",
        IsAvailable = false,
        Status = "Disabled",
        Details = details,
        RequiresAdmin = false
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

    private static string? TryGetStringPayload(TraceEvent data, string name)
    {
        try
        {
            var value = data.PayloadByName(name);
            return value switch
            {
                null => null,
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => s,
                _ => value.ToString()
            };
        }
        catch { return null; }
    }

    private static int? TryGetIntPayload(TraceEvent data, string name)
    {
        try
        {
            var value = data.PayloadByName(name);
            if (value is null) return null;
            return Convert.ToInt32(value);
        }
        catch { return null; }
    }

    private static long? TryGetLongPayload(TraceEvent data, string name)
    {
        try
        {
            var value = data.PayloadByName(name);
            if (value is null) return null;
            return Convert.ToInt64(value);
        }
        catch { return null; }
    }
}
