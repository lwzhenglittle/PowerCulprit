namespace PowerCulprit.Core.Models;

/// <summary>
/// Aggregated WMI caller activity for a selected time window.
/// </summary>
public record WmiCallerAggregate
{
    public int ClientProcessId { get; init; }

    public string? ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    public int CallCount { get; init; }

    public int FailureCount { get; init; }

    public int UniqueOperationCount { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime LastSeenUtc { get; init; }

    public string? LastOperation { get; init; }

    public string? LastResultCode { get; init; }

    public string? LastPossibleCause { get; init; }
}
