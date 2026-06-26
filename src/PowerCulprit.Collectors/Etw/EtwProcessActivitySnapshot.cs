namespace PowerCulprit.Collectors;

/// <summary>Metadata observed from ETW process lifecycle events.</summary>
public sealed record EtwProcessMetadata
{
    public int Pid { get; init; }
    public string? ProcessName { get; init; }
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public int? ParentPid { get; init; }
    public DateTime? StartTimeUtc { get; init; }
    public DateTime? StopTimeUtc { get; init; }
}

/// <summary>Aggregate ETW parser/accumulator health counters.</summary>
public sealed record EtwActivityCounters
{
    public long ProcessStartEvents { get; init; }
    public long ProcessStopEvents { get; init; }
    public long TcpSendEvents { get; init; }
    public long TcpReceiveEvents { get; init; }
    public long UdpSendEvents { get; init; }
    public long UdpReceiveEvents { get; init; }
    public long NetworkEventsIgnoredInvalidPid { get; init; }
    public long NetworkEventsIgnoredMissingBytes { get; init; }
    public long LostEventCount { get; init; }
    public long ParseErrorCount { get; init; }

    public long TcpEvents => TcpSendEvents + TcpReceiveEvents;
    public long UdpEvents => UdpSendEvents + UdpReceiveEvents;
    public long NetworkEvents => TcpEvents + UdpEvents;
    public long IgnoredNetworkEvents => NetworkEventsIgnoredInvalidPid + NetworkEventsIgnoredMissingBytes;
}

/// <summary>Per-PID ETW activity accumulated during one sampling window.</summary>
public sealed record EtwProcessActivity
{
    public int Pid { get; init; }
    public long NetworkReceiveBytes { get; init; }
    public long NetworkSendBytes { get; init; }
    public long ProcessStartCount { get; init; }
    public long ProcessStopCount { get; init; }
    public bool WasObservedInPoll { get; init; }
    public bool IsShortLived => ProcessStartCount > 0 && ProcessStopCount > 0 && !WasObservedInPoll;
    public bool HasNetworkActivity => NetworkReceiveBytes > 0 || NetworkSendBytes > 0;
    public EtwProcessMetadata? Metadata { get; init; }
}

/// <summary>Immutable snapshot returned by the ETW activity accumulator.</summary>
public sealed record EtwProcessActivitySnapshot
{
    public static readonly EtwProcessActivitySnapshot Empty = new()
    {
        TimestampUtc = DateTime.MinValue,
        Interval = TimeSpan.Zero,
        Activities = Array.Empty<EtwProcessActivity>(),
        Counters = new EtwActivityCounters()
    };

    public DateTime TimestampUtc { get; init; }
    public TimeSpan Interval { get; init; }
    public IReadOnlyList<EtwProcessActivity> Activities { get; init; } = Array.Empty<EtwProcessActivity>();
    public EtwActivityCounters Counters { get; init; } = new();
    public bool HadLostEvents => Counters.LostEventCount > 0;
    public long LostEventCount => Counters.LostEventCount;
    public long ParseErrorCount => Counters.ParseErrorCount;
}
