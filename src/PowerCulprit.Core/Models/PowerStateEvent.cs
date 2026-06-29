namespace PowerCulprit.Core.Models;

public enum PowerStateEventKind
{
    Suspend,
    Resume,
    ResumeAutomatic
}

/// <summary>
/// A recorded system power state transition captured by PowerCulprit
/// via WM_POWERBROADCAST.
/// </summary>
public record PowerStateEvent
{
    public long Id { get; init; }

    public DateTime TimestampUtc { get; init; }

    public PowerStateEventKind Kind { get; init; }

    public string Source { get; init; } = "PowerCulprit";

    public string? Details { get; init; }
}
