namespace PowerCulprit.Core.Models;

/// <summary>
/// Parsed WMI Activity event-log sample identifying a client process that called WMI.
/// </summary>
public record WmiActivitySample
{
    public DateTime TimestampUtc { get; init; }

    public long? EventRecordId { get; init; }

    public int ClientProcessId { get; init; }

    public int EventId { get; init; }

    public string? User { get; init; }

    public string? Operation { get; init; }

    public string? NamespaceName { get; init; }

    public string? QueryText { get; init; }

    public string? ResultCode { get; init; }

    public string? PossibleCause { get; init; }
}
