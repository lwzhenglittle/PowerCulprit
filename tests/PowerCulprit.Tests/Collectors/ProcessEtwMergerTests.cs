using PowerCulprit.Collectors;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Collectors;

public class ProcessEtwMergerTests
{
    [Fact]
    public void Merge_WritesNetworkRatesForMatchingPidOnly()
    {
        var samples = new[]
        {
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 10, ProcessName = "a", IsForegroundProcess = false },
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 20, ProcessName = "b", IsForegroundProcess = true }
        };
        var snapshot = new EtwProcessActivitySnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Interval = TimeSpan.FromSeconds(2),
            Activities = new[]
            {
                new EtwProcessActivity { Pid = 10, NetworkReceiveBytes = 2000, NetworkSendBytes = 1000 },
                new EtwProcessActivity { Pid = 30, NetworkReceiveBytes = 9999 }
            }
        };

        var merged = ProcessEtwMerger.Merge(samples, snapshot);

        Assert.Equal(1000, merged[0].NetworkReceiveBytesPerSecond);
        Assert.Equal(500, merged[0].NetworkSendBytesPerSecond);
        Assert.Null(merged[1].NetworkReceiveBytesPerSecond);
        Assert.Null(merged[1].NetworkSendBytesPerSecond);
    }

    [Fact]
    public void Merge_WritesLifecycleCountsForMatchingPid()
    {
        var samples = new[]
        {
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 10, ProcessName = "a", IsForegroundProcess = false }
        };
        var snapshot = new EtwProcessActivitySnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Interval = TimeSpan.FromSeconds(2),
            Activities = new[] { new EtwProcessActivity { Pid = 10, ProcessStartCount = 2, ProcessStopCount = 1 } }
        };

        var merged = ProcessEtwMerger.Merge(samples, snapshot);

        Assert.Equal(2, merged[0].ProcessStartCount);
        Assert.Equal(1, merged[0].ProcessStopCount);
    }

    [Fact]
    public void Merge_WritesShortLivedCountByProcessNameWithoutCreatingRows()
    {
        var samples = new[]
        {
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 10, ProcessName = "helper.exe", IsForegroundProcess = false }
        };
        var snapshot = new EtwProcessActivitySnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Interval = TimeSpan.FromSeconds(2),
            Activities = new[]
            {
                new EtwProcessActivity
                {
                    Pid = 99,
                    ProcessStartCount = 1,
                    ProcessStopCount = 1,
                    Metadata = new EtwProcessMetadata { Pid = 99, ProcessName = "helper.exe" }
                }
            }
        };

        var merged = ProcessEtwMerger.Merge(samples, snapshot);

        Assert.Single(merged);
        Assert.Equal(1, merged[0].ShortLivedProcessCount);
    }

    [Fact]
    public void Merge_DoesNotWriteZeroWhenNoNetworkActivity()
    {
        var samples = new[]
        {
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 10, ProcessName = "a", IsForegroundProcess = false }
        };
        var snapshot = new EtwProcessActivitySnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Interval = TimeSpan.FromSeconds(2),
            Activities = new[] { new EtwProcessActivity { Pid = 10, ProcessStartCount = 1 } }
        };

        var merged = ProcessEtwMerger.Merge(samples, snapshot);

        Assert.Null(merged[0].NetworkReceiveBytesPerSecond);
        Assert.Null(merged[0].NetworkSendBytesPerSecond);
    }

    [Fact]
    public void Merge_DoesNotComputeRatesForInvalidInterval()
    {
        var samples = new[]
        {
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 10, ProcessName = "a", IsForegroundProcess = false }
        };
        var snapshot = new EtwProcessActivitySnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Interval = TimeSpan.Zero,
            Activities = new[] { new EtwProcessActivity { Pid = 10, NetworkReceiveBytes = 2000, NetworkSendBytes = 1000 } }
        };

        var merged = ProcessEtwMerger.Merge(samples, snapshot);

        Assert.Null(merged[0].NetworkReceiveBytesPerSecond);
        Assert.Null(merged[0].NetworkSendBytesPerSecond);
    }

    [Fact]
    public void Merge_CanFillMissingMetadata()
    {
        var samples = new[]
        {
            new ProcessSample { TimestampUtc = DateTime.UtcNow, Pid = 10, ProcessName = "a", IsForegroundProcess = false }
        };
        var snapshot = new EtwProcessActivitySnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Interval = TimeSpan.FromSeconds(2),
            Activities = new[]
            {
                new EtwProcessActivity
                {
                    Pid = 10,
                    Metadata = new EtwProcessMetadata
                    {
                        Pid = 10,
                        ImagePath = @"C:\a.exe",
                        CommandLine = "a --flag",
                        ParentPid = 5
                    }
                }
            }
        };

        var merged = ProcessEtwMerger.Merge(samples, snapshot);

        Assert.Equal(@"C:\a.exe", merged[0].ExecutablePath);
        Assert.Equal("a --flag", merged[0].CommandLine);
        Assert.Equal(5, merged[0].ParentPid);
    }
}
