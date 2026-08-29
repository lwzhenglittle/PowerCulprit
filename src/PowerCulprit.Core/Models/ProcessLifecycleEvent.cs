namespace PowerCulprit.Core.Models;

public enum ProcessLifecycleEventKind
{
    Start,
    Stop
}

/// <summary>
/// One ETW-observed process lifecycle transition. Start time is retained on
/// stop events when known so a PID can be matched to the correct instance.
/// </summary>
public sealed record ProcessLifecycleEvent
{
    public ProcessLifecycleEventKind Kind { get; init; }
    public DateTime TimestampUtc { get; init; }
    public int Pid { get; init; }
    public DateTime? StartTimeUtc { get; init; }
    public string? ProcessName { get; init; }
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public int? ParentPid { get; init; }
    public DateTime? ParentStartTimeUtc { get; init; }
    public bool CaptureReliable { get; init; } = true;
}

/// <summary>A persisted process instance, uniquely identified by PID and start time.</summary>
public sealed record ProcessInstance
{
    public long Id { get; init; }
    public int Pid { get; init; }
    public DateTime StartTimeUtc { get; init; }
    public DateTime? StopTimeUtc { get; init; }
    public bool StartObserved { get; init; }
    public bool StopObserved { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public int? ParentPid { get; init; }
    public DateTime? ParentStartTimeUtc { get; init; }
    public long? ParentInstanceId { get; init; }
    public string? ParentProcessName { get; init; }
    public bool CaptureReliable { get; init; }

    public TimeSpan? Lifetime => StopTimeUtc.HasValue && StopTimeUtc.Value >= StartTimeUtc
        ? StopTimeUtc.Value - StartTimeUtc
        : null;
}
