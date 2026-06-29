namespace PowerCulprit.Core.Analysis;

public enum GapKind
{
    /// <summary>
    /// Confirmed by a matching PowerCulprit
    /// Suspend → Resume / ResumeAutomatic pair.
    /// </summary>
    ConfirmedSleep,

    /// <summary>
    /// A monitoring gap with no evidence of sleep — may be a crash, restart,
    /// app termination, or unhandled system state.
    /// </summary>
    UnknownGap
}

/// <summary>
/// A time interval in which no process-level telemetry was available.
/// ConfirmedSleep intervals are backed by PowerCulprit suspend/resume
/// events; UnknownGap intervals are inferred from sample continuity gaps.
/// </summary>
public record PowerStateInterval
{
    public DateTime StartUtc { get; init; }

    public DateTime EndUtc { get; init; }

    public GapKind Kind { get; init; }
}
