using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

public sealed class WmiActivityCollector : IWmiActivityCollector
{
    public const string SourceName = "WindowsWmiActivity";
    public const string LogName = "Microsoft-Windows-WMI-Activity/Operational";
    private static readonly TimeSpan CollectInterval = TimeSpan.FromSeconds(30);
    private const int MaxEventsPerRead = 200;

    private readonly ILogger<WmiActivityCollector> _logger;
    private readonly WmiActivityAccumulator _accumulator = new();
    private readonly object _lock = new();
    private SourceStatus _status = DisabledStatus("WMI Activity collector has not been started");
    private bool _started;
    private long _lastRecordId;
    private DateTime _lastReadUtc = DateTime.MinValue;
    private WmiActivityCounters _lastCounters = new();

    public WmiActivityCollector(ILogger<WmiActivityCollector> logger)
    {
        _logger = logger;
    }

    public void SetLastRecordId(long recordId)
    {
        if (recordId < 0)
            recordId = 0;

        lock (_lock)
        {
            _lastRecordId = Math.Max(_lastRecordId, recordId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _started = true;
            _status = new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = SourceName,
                IsAvailable = true,
                Status = SourceStatusStrings.Available,
                Details = "WMI Activity event log will be read incrementally",
                RequiresAdmin = false
            };
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            _started = false;
            _status = DisabledStatus("WMI Activity collector is stopped");
        }

        return Task.CompletedTask;
    }

    public WmiActivitySnapshot SnapshotAndReset(DateTime nowUtc)
    {
        try
        {
            var shouldRead = false;
            lock (_lock)
            {
                shouldRead = _started && nowUtc - _lastReadUtc >= CollectInterval;
                if (shouldRead)
                    _lastReadUtc = nowUtc;
            }

            if (shouldRead)
                ReadNewEvents();

            var snapshot = _accumulator.SnapshotAndReset(nowUtc);
            lock (_lock)
            {
                _lastCounters = snapshot.Counters;
                if (_started && _status.Status is "Available" or "Partial")
                    _status = BuildAvailableStatus(snapshot.Counters);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI Activity snapshot failed");
            SetUnavailable(ex.Message);
            return WmiActivitySnapshot.Empty with { TimestampUtc = nowUtc };
        }
    }

    public SourceStatus GetStatus()
    {
        lock (_lock) { return _status; }
    }

    /// <summary>
    /// Cold probe of the WMI Activity event log for <c>--diagnose</c>. Unlike
    /// <see cref="GetStatus"/> (which returns the running collector's cached
    /// status), this never requires the collector to have been started: it
    /// checks whether the Microsoft-Windows-WMI-Activity/Operational log exists
    /// and is readable, and surfaces the latest 5858/5860 record id as proof.
    /// </summary>
    public static SourceStatus ProbeStatus()
    {
        try
        {
            using var config = new EventLogConfiguration(LogName);
            var enabled = config.IsEnabled;

            string details;
            var status = enabled ? SourceStatusStrings.Available : SourceStatusStrings.Disabled;
            try
            {
                var query = new EventLogQuery(LogName, PathType.LogName,
                    "*[System[EventID=5858 or EventID=5860]]")
                {
                    ReverseDirection = true
                };
                using var reader = new EventLogReader(query);
                using var latest = reader.ReadEvent();
                details = latest is null
                    ? "event log is readable; no recent WMI client events"
                    : $"event log is readable; latest record {latest.RecordId}";
            }
            catch (Exception ex)
            {
                status = SourceStatusStrings.Unavailable;
                details = ex.Message;
            }

            return new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = SourceName,
                IsAvailable = status == SourceStatusStrings.Available,
                Status = status,
                Details = details,
                RequiresAdmin = false
            };
        }
        catch (EventLogNotFoundException)
        {
            return new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = SourceName,
                IsAvailable = false,
                Status = SourceStatusStrings.Unavailable,
                Details = "Microsoft-Windows-WMI-Activity/Operational not found",
                RequiresAdmin = null
            };
        }
        catch (Exception ex)
        {
            return new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = SourceName,
                IsAvailable = false,
                Status = SourceStatusStrings.Unavailable,
                Details = ex.Message,
                RequiresAdmin = null
            };
        }
    }

    internal static WmiActivitySample? ParseEventXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        XNamespace eventNs = "http://schemas.microsoft.com/win/2004/08/events/event";
        var system = doc.Root?.Element(eventNs + "System");
        if (system is null)
            return null;

        var eventIdText = system.Element(eventNs + "EventID")?.Value;
        if (!int.TryParse(eventIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
            return null;

        long? eventRecordId = null;
        var eventRecordIdText = system.Element(eventNs + "EventRecordID")?.Value;
        if (long.TryParse(eventRecordIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRecordId))
            eventRecordId = parsedRecordId;

        var timeText = system.Element(eventNs + "TimeCreated")?.Attribute("SystemTime")?.Value;
        var timestampUtc = DateTime.TryParse(
            timeText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsedTime)
            ? parsedTime
            : DateTime.UtcNow;

        var values = ExtractUserData(doc);
        var pidText = GetFirst(values, "ClientProcessId", "ClientProcessID", "ProcessId", "ProcessID");
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clientPid))
            clientPid = 0;

        var operation = GetFirst(values, "Operation", "NotificationQuery", "ProviderName");
        var (namespaceName, queryText) = ParseOperation(operation);

        return new WmiActivitySample
        {
            TimestampUtc = timestampUtc,
            EventRecordId = eventRecordId,
            ClientProcessId = clientPid,
            EventId = eventId,
            User = GetFirst(values, "User", "UserName"),
            Operation = operation,
            NamespaceName = namespaceName,
            QueryText = queryText,
            ResultCode = GetFirst(values, "ResultCode"),
            PossibleCause = GetFirst(values, "PossibleCause")
        };
    }

    private void ReadNewEvents()
    {
        try
        {
            EnsureLogAvailable();
            var queryText = _lastRecordId > 0
                ? $"*[System[EventRecordID>{_lastRecordId}]]"
                : "*[System[EventID=5857 or EventID=5858 or EventID=5860 or EventID=5861]]";
            var query = new EventLogQuery(LogName, PathType.LogName, queryText)
            {
                ReverseDirection = false
            };

            var readCount = 0;
            using var reader = new EventLogReader(query);
            for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (readCount >= MaxEventsPerRead)
                    {
                        _accumulator.RecordTruncatedRead();
                        break;
                    }

                    try
                    {
                        var sample = ParseEventXml(record.ToXml());
                        if (sample is not null)
                        {
                            sample = sample with { EventRecordId = record.RecordId ?? sample.EventRecordId };
                            _accumulator.RecordSample(sample);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse WMI Activity event");
                        _accumulator.RecordParseError();
                    }

                    _lastRecordId = Math.Max(_lastRecordId, record.RecordId ?? _lastRecordId);
                    readCount++;
                }
            }

            lock (_lock)
            {
                if (_started && _status.Status != SourceStatusStrings.Available)
                    _status = BuildAvailableStatus(_lastCounters);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "WMI Activity event log requires administrator privileges");
            SetRequiresAdmin(ex.Message);
        }
        catch (EventLogNotFoundException ex)
        {
            _logger.LogWarning(ex, "WMI Activity event log is unavailable");
            SetUnavailable("Microsoft-Windows-WMI-Activity/Operational is unavailable");
        }
        catch (EventLogException ex) when (LooksLikeAccessDenied(ex))
        {
            _logger.LogWarning(ex, "WMI Activity event log requires administrator privileges");
            SetRequiresAdmin(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI Activity event log read failed");
            SetUnavailable(ex.Message);
        }
    }

    private static Dictionary<string, string> ExtractUserData(XDocument doc)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in doc.Descendants())
        {
            if (!element.HasElements)
                values[element.Name.LocalName] = element.Value;
        }

        return values;
    }

    private static string? GetFirst(IReadOnlyDictionary<string, string> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static (string? NamespaceName, string? QueryText) ParseOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            return (null, null);

        var marker = " - ";
        var markerIndex = operation.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return (null, operation.Length > 220 ? operation[..220] : operation);

        var target = operation[(markerIndex + marker.Length)..].Trim();
        var colonIndex = target.IndexOf(" : ", StringComparison.Ordinal);
        if (colonIndex < 0)
            return (target, null);

        var namespaceName = target[..colonIndex].Trim();
        var queryText = target[(colonIndex + 3)..].Trim();
        if (queryText.Length > 220)
            queryText = queryText[..220];
        return (namespaceName, queryText);
    }

    private void EnsureLogAvailable()
    {
        using var config = new EventLogConfiguration(LogName);
        if (!config.IsEnabled)
            throw new EventLogNotFoundException($"{LogName} is disabled");
    }

    private SourceStatus BuildAvailableStatus(WmiActivityCounters counters)
    {
        var details = counters.EventsRead > 0
            ? $"Read {counters.EventsRead} WMI Activity events in last poll"
            : "WMI Activity event log is readable; no new events in last poll";
        if (counters.TruncatedReads > 0)
            details += $"; truncated reads {counters.TruncatedReads}";
        if (counters.ParseErrors > 0)
            details += $"; parse errors {counters.ParseErrors}";

        return new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = SourceName,
            IsAvailable = true,
            Status = counters.ParseErrors > 0 || counters.TruncatedReads > 0 ? SourceStatusStrings.Partial : SourceStatusStrings.Available,
            Details = details,
            RequiresAdmin = false
        };
    }

    private static SourceStatus DisabledStatus(string details)
        => new()
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = SourceName,
            IsAvailable = false,
            Status = SourceStatusStrings.Disabled,
            Details = details,
            RequiresAdmin = false
        };

    private void SetUnavailable(string details)
    {
        lock (_lock)
        {
            _status = new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = SourceName,
                IsAvailable = false,
                Status = SourceStatusStrings.Unavailable,
                Details = details,
                RequiresAdmin = null
            };
        }
    }

    private void SetRequiresAdmin(string details)
    {
        lock (_lock)
        {
            _status = new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = SourceName,
                IsAvailable = false,
                Status = SourceStatusStrings.RequiresAdmin,
                Details = details,
                RequiresAdmin = true
            };
        }
    }

    private static bool LooksLikeAccessDenied(Exception ex)
        => ex.HResult == unchecked((int)0x80070005) ||
           ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase);
}
