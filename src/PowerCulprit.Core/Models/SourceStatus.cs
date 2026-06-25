namespace PowerCulprit.Core.Models;

/// <summary>
/// Status of a data source at a point in time, used for UI status panel and --diagnose.
/// </summary>
public record SourceStatus
{
    /// <summary>UTC timestamp of this status check.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Identifier of the data source.</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>Whether this data source is currently available.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>
    /// High-level status label:
    /// "Available", "Unavailable", "Partial", "Requires admin", or "Disabled".
    /// </summary>
    public string Status { get; init; } = "Unavailable";

    /// <summary>Additional detail about the status, or null.</summary>
    public string? Details { get; init; }

    /// <summary>Whether administrator privileges are required to access this source.</summary>
    public bool? RequiresAdmin { get; init; }
}
