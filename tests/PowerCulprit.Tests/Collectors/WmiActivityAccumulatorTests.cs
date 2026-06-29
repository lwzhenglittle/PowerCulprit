using PowerCulprit.Collectors;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Collectors;

public class WmiActivityAccumulatorTests
{
    [Fact]
    public void SnapshotAndReset_ReturnsSamplesAndClearsActivity()
    {
        var accumulator = new WmiActivityAccumulator();
        var now = DateTime.UtcNow;

        accumulator.RecordSample(new WmiActivitySample
        {
            TimestampUtc = now,
            ClientProcessId = 123,
            EventId = 5858,
            Operation = "Start IWbemServices::ExecQuery - root\\cimv2 : SELECT * FROM Win32_Process"
        });
        accumulator.RecordSample(new WmiActivitySample
        {
            TimestampUtc = now.AddSeconds(1),
            ClientProcessId = 456,
            EventId = 5860,
            Operation = "select * from __InstanceCreationEvent"
        });

        var first = accumulator.SnapshotAndReset(now.AddSeconds(2));
        Assert.Equal(2, first.Samples.Count);
        Assert.Equal(2, first.Counters.EventsRead);

        var second = accumulator.SnapshotAndReset(now.AddSeconds(4));
        Assert.Empty(second.Samples);
        Assert.Equal(0, second.Counters.EventsRead);
    }

    [Fact]
    public void InvalidPid_IsCountedButNotEmitted()
    {
        var accumulator = new WmiActivityAccumulator();
        accumulator.RecordSample(new WmiActivitySample
        {
            TimestampUtc = DateTime.UtcNow,
            ClientProcessId = 0,
            EventId = 5858
        });

        var snapshot = accumulator.SnapshotAndReset(DateTime.UtcNow);

        Assert.Empty(snapshot.Samples);
        Assert.Equal(1, snapshot.Counters.EventsRead);
        Assert.Equal(1, snapshot.Counters.EventsWithInvalidPid);
    }

    [Fact]
    public void ParseErrorAndTruncatedRead_AreReported()
    {
        var accumulator = new WmiActivityAccumulator();
        accumulator.RecordParseError();
        accumulator.RecordTruncatedRead();

        var snapshot = accumulator.SnapshotAndReset(DateTime.UtcNow);

        Assert.Equal(1, snapshot.Counters.ParseErrors);
        Assert.Equal(1, snapshot.Counters.TruncatedReads);
    }
}
