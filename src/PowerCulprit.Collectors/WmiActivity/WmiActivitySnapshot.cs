using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

public sealed record WmiActivityCounters
{
    public long EventsRead { get; init; }

    public long EventsWithInvalidPid { get; init; }

    public long ParseErrors { get; init; }

    public long TruncatedReads { get; init; }
}

public sealed record WmiActivitySnapshot
{
    public static readonly WmiActivitySnapshot Empty = new()
    {
        TimestampUtc = DateTime.MinValue,
        Interval = TimeSpan.Zero,
        Samples = Array.Empty<WmiActivitySample>(),
        Counters = new WmiActivityCounters()
    };

    public DateTime TimestampUtc { get; init; }

    public TimeSpan Interval { get; init; }

    public IReadOnlyList<WmiActivitySample> Samples { get; init; } = Array.Empty<WmiActivitySample>();

    public WmiActivityCounters Counters { get; init; } = new();
}
