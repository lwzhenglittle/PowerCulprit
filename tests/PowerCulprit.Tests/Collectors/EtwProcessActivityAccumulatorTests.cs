using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class EtwProcessActivityAccumulatorTests
{
    [Fact]
    public void SnapshotAndReset_ReturnsCountsAndClearsActivityButKeepsMetadata()
    {
        var accumulator = new EtwProcessActivityAccumulator();
        var t0 = new DateTime(2026, 06, 25, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(2);
        var t2 = t1.AddSeconds(2);

        accumulator.SnapshotAndReset(t0);
        accumulator.RecordProcessStart(123, "app.exe", imagePath: @"C:\app.exe", commandLine: "app.exe --flag", parentPid: 10, startTimeUtc: t0);
        accumulator.RecordTcpReceive(123, 2048);
        accumulator.RecordTcpSend(123, 1024);
        accumulator.RecordProcessStop(123, t1);

        var snapshot = accumulator.SnapshotAndReset(t1);

        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.Interval);
        var activity = Assert.Single(snapshot.Activities);
        Assert.Equal(123, activity.Pid);
        Assert.Equal(2048, activity.NetworkReceiveBytes);
        Assert.Equal(1024, activity.NetworkSendBytes);
        Assert.Equal(1, activity.ProcessStartCount);
        Assert.Equal(1, activity.ProcessStopCount);
        Assert.True(activity.IsShortLived);
        Assert.Equal("app.exe", activity.Metadata?.ProcessName);
        Assert.Equal(@"C:\app.exe", activity.Metadata?.ImagePath);
        Assert.Equal(1, snapshot.Counters.ProcessStartEvents);
        Assert.Equal(1, snapshot.Counters.ProcessStopEvents);
        Assert.Equal(1, snapshot.Counters.TcpReceiveEvents);
        Assert.Equal(1, snapshot.Counters.TcpSendEvents);

        var second = accumulator.SnapshotAndReset(t2);
        Assert.Empty(second.Activities);

        accumulator.RecordTcpSend(123, 512);
        var third = accumulator.SnapshotAndReset(t2.AddSeconds(2));
        var retained = Assert.Single(third.Activities);
        Assert.Equal(@"C:\app.exe", retained.Metadata?.ImagePath);
    }

    [Fact]
    public void RecordTcp_IgnoresInvalidPidAndBytes()
    {
        var accumulator = new EtwProcessActivityAccumulator();
        var t0 = new DateTime(2026, 06, 25, 12, 0, 0, DateTimeKind.Utc);

        accumulator.RecordTcpSend(0, 100);
        accumulator.RecordTcpSend(1, -100);
        accumulator.RecordTcpReceive(-1, 100);

        var snapshot = accumulator.SnapshotAndReset(t0);

        Assert.Empty(snapshot.Activities);
        Assert.Equal(2, snapshot.Counters.NetworkEventsIgnoredInvalidPid);
        Assert.Equal(1, snapshot.Counters.NetworkEventsIgnoredMissingBytes);
    }

    [Fact]
    public void RecordUdp_AddsToNetworkBytesAndCounters()
    {
        var accumulator = new EtwProcessActivityAccumulator();
        var t0 = new DateTime(2026, 06, 25, 12, 0, 0, DateTimeKind.Utc);

        accumulator.RecordUdpReceive(42, 300);
        accumulator.RecordUdpSend(42, 700);

        var snapshot = accumulator.SnapshotAndReset(t0);
        var activity = Assert.Single(snapshot.Activities);
        Assert.Equal(300, activity.NetworkReceiveBytes);
        Assert.Equal(700, activity.NetworkSendBytes);
        Assert.Equal(1, snapshot.Counters.UdpReceiveEvents);
        Assert.Equal(1, snapshot.Counters.UdpSendEvents);
    }

    [Fact]
    public void RecordPolledProcess_PreventsShortLivedClassification()
    {
        var accumulator = new EtwProcessActivityAccumulator();
        var t0 = new DateTime(2026, 06, 25, 12, 0, 0, DateTimeKind.Utc);

        accumulator.RecordProcessStart(42, "worker.exe", startTimeUtc: t0);
        accumulator.RecordProcessStop(42, t0.AddMilliseconds(100));
        accumulator.RecordPolledProcess(42);

        var snapshot = accumulator.SnapshotAndReset(t0.AddSeconds(2));
        var activity = Assert.Single(snapshot.Activities);
        Assert.False(activity.IsShortLived);
        Assert.True(activity.WasObservedInPoll);
    }
}
