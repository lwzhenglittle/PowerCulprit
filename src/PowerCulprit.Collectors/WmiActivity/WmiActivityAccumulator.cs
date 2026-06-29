using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

public sealed class WmiActivityAccumulator
{
    private readonly object _lock = new();
    private readonly List<WmiActivitySample> _samples = new();
    private DateTime? _lastSnapshotUtc;
    private long _eventsRead;
    private long _eventsWithInvalidPid;
    private long _parseErrors;
    private long _truncatedReads;

    public void RecordSample(WmiActivitySample sample)
    {
        lock (_lock)
        {
            _eventsRead++;
            if (sample.ClientProcessId <= 0)
            {
                _eventsWithInvalidPid++;
                return;
            }

            _samples.Add(sample);
        }
    }

    public void RecordParseError()
    {
        lock (_lock)
        {
            _parseErrors++;
        }
    }

    public void RecordTruncatedRead()
    {
        lock (_lock)
        {
            _truncatedReads++;
        }
    }

    public WmiActivitySnapshot SnapshotAndReset(DateTime nowUtc)
    {
        lock (_lock)
        {
            var interval = _lastSnapshotUtc.HasValue
                ? nowUtc - _lastSnapshotUtc.Value
                : TimeSpan.Zero;
            _lastSnapshotUtc = nowUtc;

            var snapshot = new WmiActivitySnapshot
            {
                TimestampUtc = nowUtc,
                Interval = interval,
                Samples = _samples.ToArray(),
                Counters = new WmiActivityCounters
                {
                    EventsRead = _eventsRead,
                    EventsWithInvalidPid = _eventsWithInvalidPid,
                    ParseErrors = _parseErrors,
                    TruncatedReads = _truncatedReads
                }
            };

            _samples.Clear();
            _eventsRead = 0;
            _eventsWithInvalidPid = 0;
            _parseErrors = 0;
            _truncatedReads = 0;

            return snapshot;
        }
    }
}
