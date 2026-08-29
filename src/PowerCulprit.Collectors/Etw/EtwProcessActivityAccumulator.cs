using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Thread-safe accumulator for ETW process lifecycle and TCP/IP byte events.
/// </summary>
public sealed class EtwProcessActivityAccumulator
{
    private static readonly TimeSpan MetadataRetention = TimeSpan.FromMinutes(20);
    private readonly object _lock = new();
    private readonly Dictionary<int, MutableActivity> _activities = new();
    private readonly Dictionary<int, MutableMetadata> _metadata = new();
    private readonly List<ProcessLifecycleEvent> _lifecycleEvents = new();
    private DateTime _lastSnapshotUtc;
    private EtwActivityCounters _periodCounters = new();

    public void RecordProcessStart(
        int pid,
        string? processName = null,
        string? imagePath = null,
        string? commandLine = null,
        int? parentPid = null,
        DateTime? startTimeUtc = null)
    {
        if (pid <= 0)
            return;

        lock (_lock)
        {
            var normalizedStartTimeUtc = NormalizeUtc(startTimeUtc);
            DateTime? parentStartTimeUtc = null;
            if (parentPid is > 0 &&
                _metadata.TryGetValue(parentPid.Value, out var parentMetadata) &&
                (!parentMetadata.StopTimeUtc.HasValue ||
                 !normalizedStartTimeUtc.HasValue ||
                 parentMetadata.StopTimeUtc.Value >= normalizedStartTimeUtc.Value))
            {
                parentStartTimeUtc = parentMetadata.StartTimeUtc;
            }

            var metadata = new MutableMetadata(pid)
            {
                ProcessName = NormalizeText(processName),
                ImagePath = NormalizeText(imagePath),
                CommandLine = NormalizeText(commandLine),
                ParentPid = parentPid,
                StartTimeUtc = normalizedStartTimeUtc,
                StopTimeUtc = null,
                LastSeenUtc = NormalizeUtc(startTimeUtc) ?? DateTime.UtcNow
            };
            _metadata[pid] = metadata;
            _lifecycleEvents.Add(new ProcessLifecycleEvent
            {
                Kind = ProcessLifecycleEventKind.Start,
                TimestampUtc = metadata.StartTimeUtc ?? DateTime.UtcNow,
                Pid = pid,
                StartTimeUtc = metadata.StartTimeUtc,
                ProcessName = metadata.ProcessName,
                ImagePath = metadata.ImagePath,
                CommandLine = metadata.CommandLine,
                ParentPid = metadata.ParentPid,
                ParentStartTimeUtc = parentStartTimeUtc
            });

            var activity = GetOrCreateActivity(pid);
            activity.ProcessStartCount++;
            _periodCounters = _periodCounters with
            {
                ProcessStartEvents = _periodCounters.ProcessStartEvents + 1
            };
        }
    }

    public void RecordProcessStop(int pid, DateTime? stopTimeUtc = null)
    {
        if (pid <= 0)
            return;

        lock (_lock)
        {
            var utc = NormalizeUtc(stopTimeUtc) ?? DateTime.UtcNow;
            MutableMetadata? lifecycleMetadata = null;
            if (_metadata.TryGetValue(pid, out var metadata))
            {
                metadata.StopTimeUtc = utc;
                metadata.LastSeenUtc = utc;
                lifecycleMetadata = metadata;
            }
            else
            {
                lifecycleMetadata = new MutableMetadata(pid)
                {
                    StopTimeUtc = utc,
                    LastSeenUtc = utc
                };
                _metadata[pid] = lifecycleMetadata;
            }

            _lifecycleEvents.Add(new ProcessLifecycleEvent
            {
                Kind = ProcessLifecycleEventKind.Stop,
                TimestampUtc = utc,
                Pid = pid,
                StartTimeUtc = lifecycleMetadata.StartTimeUtc,
                ProcessName = lifecycleMetadata.ProcessName,
                ImagePath = lifecycleMetadata.ImagePath,
                CommandLine = lifecycleMetadata.CommandLine,
                ParentPid = lifecycleMetadata.ParentPid
            });

            var activity = GetOrCreateActivity(pid);
            activity.ProcessStopCount++;
            _periodCounters = _periodCounters with
            {
                ProcessStopEvents = _periodCounters.ProcessStopEvents + 1
            };
        }
    }

    public void RecordPolledProcess(int pid)
    {
        if (pid <= 0)
            return;

        lock (_lock)
        {
            if (_activities.TryGetValue(pid, out var activity))
                activity.WasObservedInPoll = true;
        }
    }

    public void RecordTcpSend(int pid, long bytes) => AddNetworkBytes(pid, sendBytes: bytes, receiveBytes: 0, NetworkProtocol.Tcp, send: true);

    public void RecordTcpReceive(int pid, long bytes) => AddNetworkBytes(pid, sendBytes: 0, receiveBytes: bytes, NetworkProtocol.Tcp, send: false);

    public void RecordUdpSend(int pid, long bytes) => AddNetworkBytes(pid, sendBytes: bytes, receiveBytes: 0, NetworkProtocol.Udp, send: true);

    public void RecordUdpReceive(int pid, long bytes) => AddNetworkBytes(pid, sendBytes: 0, receiveBytes: bytes, NetworkProtocol.Udp, send: false);

    public void RecordIgnoredNetworkEventInvalidPid()
    {
        lock (_lock)
        {
            _periodCounters = _periodCounters with
            {
                NetworkEventsIgnoredInvalidPid = _periodCounters.NetworkEventsIgnoredInvalidPid + 1
            };
        }
    }

    public void RecordIgnoredNetworkEventMissingBytes()
    {
        lock (_lock)
        {
            _periodCounters = _periodCounters with
            {
                NetworkEventsIgnoredMissingBytes = _periodCounters.NetworkEventsIgnoredMissingBytes + 1
            };
        }
    }

    public void RecordLostEvents(long count = 1)
    {
        if (count <= 0)
            return;

        lock (_lock)
        {
            _periodCounters = _periodCounters with
            {
                LostEventCount = _periodCounters.LostEventCount + count
            };
        }
    }

    public void RecordParseError()
    {
        lock (_lock)
        {
            _periodCounters = _periodCounters with
            {
                ParseErrorCount = _periodCounters.ParseErrorCount + 1
            };
        }
    }

    public EtwProcessActivitySnapshot SnapshotAndReset(DateTime nowUtc)
    {
        nowUtc = NormalizeUtc(nowUtc) ?? DateTime.UtcNow;

        lock (_lock)
        {
            var interval = _lastSnapshotUtc == DateTime.MinValue
                ? TimeSpan.Zero
                : nowUtc - _lastSnapshotUtc;

            PruneMetadata(nowUtc);

            var snapshotActivities = _activities
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => ToImmutable(kvp.Value))
                .ToArray();
            var captureReliable = _periodCounters.LostEventCount == 0;
            var lifecycleEvents = _lifecycleEvents
                .Select(evt => evt with { CaptureReliable = captureReliable })
                .ToArray();

            var snapshot = new EtwProcessActivitySnapshot
            {
                TimestampUtc = nowUtc,
                Interval = interval,
                Activities = snapshotActivities,
                LifecycleEvents = lifecycleEvents,
                Counters = _periodCounters
            };

            _activities.Clear();
            _lifecycleEvents.Clear();
            _periodCounters = new EtwActivityCounters();
            _lastSnapshotUtc = nowUtc;
            return snapshot;
        }
    }

    private void AddNetworkBytes(int pid, long sendBytes, long receiveBytes, NetworkProtocol protocol, bool send)
    {
        if (pid <= 0)
        {
            RecordIgnoredNetworkEventInvalidPid();
            return;
        }

        if (sendBytes < 0 || receiveBytes < 0 || (sendBytes == 0 && receiveBytes == 0))
        {
            RecordIgnoredNetworkEventMissingBytes();
            return;
        }

        lock (_lock)
        {
            var activity = GetOrCreateActivity(pid);
            activity.NetworkSendBytes += sendBytes;
            activity.NetworkReceiveBytes += receiveBytes;
            if (_metadata.TryGetValue(pid, out var metadata))
                metadata.LastSeenUtc = DateTime.UtcNow;

            _periodCounters = protocol switch
            {
                NetworkProtocol.Tcp when send => _periodCounters with { TcpSendEvents = _periodCounters.TcpSendEvents + 1 },
                NetworkProtocol.Tcp => _periodCounters with { TcpReceiveEvents = _periodCounters.TcpReceiveEvents + 1 },
                NetworkProtocol.Udp when send => _periodCounters with { UdpSendEvents = _periodCounters.UdpSendEvents + 1 },
                NetworkProtocol.Udp => _periodCounters with { UdpReceiveEvents = _periodCounters.UdpReceiveEvents + 1 },
                _ => _periodCounters
            };
        }
    }

    private MutableActivity GetOrCreateActivity(int pid)
    {
        if (_activities.TryGetValue(pid, out var activity))
            return activity;

        activity = new MutableActivity(pid);
        _activities[pid] = activity;
        return activity;
    }

    private EtwProcessActivity ToImmutable(MutableActivity activity)
    {
        _metadata.TryGetValue(activity.Pid, out var metadata);
        return new EtwProcessActivity
        {
            Pid = activity.Pid,
            NetworkReceiveBytes = activity.NetworkReceiveBytes,
            NetworkSendBytes = activity.NetworkSendBytes,
            ProcessStartCount = activity.ProcessStartCount,
            ProcessStopCount = activity.ProcessStopCount,
            WasObservedInPoll = activity.WasObservedInPoll,
            Metadata = metadata is null ? null : new EtwProcessMetadata
            {
                Pid = metadata.Pid,
                ProcessName = metadata.ProcessName,
                ImagePath = metadata.ImagePath,
                CommandLine = metadata.CommandLine,
                ParentPid = metadata.ParentPid,
                StartTimeUtc = metadata.StartTimeUtc,
                StopTimeUtc = metadata.StopTimeUtc
            }
        };
    }

    private void PruneMetadata(DateTime nowUtc)
    {
        var cutoff = nowUtc - MetadataRetention;
        var activePids = _activities.Keys.ToHashSet();
        foreach (var (pid, metadata) in _metadata.ToArray())
        {
            if (!activePids.Contains(pid) && metadata.LastSeenUtc < cutoff)
                _metadata.Remove(pid);
        }
    }

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue || value.Value == DateTime.MinValue)
            return null;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private enum NetworkProtocol
    {
        Tcp,
        Udp
    }

    private sealed class MutableActivity(int pid)
    {
        public int Pid { get; } = pid;
        public long NetworkReceiveBytes { get; set; }
        public long NetworkSendBytes { get; set; }
        public long ProcessStartCount { get; set; }
        public long ProcessStopCount { get; set; }
        public bool WasObservedInPoll { get; set; }
    }

    private sealed class MutableMetadata(int pid)
    {
        public int Pid { get; } = pid;
        public string? ProcessName { get; init; }
        public string? ImagePath { get; init; }
        public string? CommandLine { get; init; }
        public int? ParentPid { get; init; }
        public DateTime? StartTimeUtc { get; init; }
        public DateTime? StopTimeUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}
