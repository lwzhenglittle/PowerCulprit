using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Models;

public class ProcessSampleTests
{
    [Fact]
    public void Constructor_SetsDefaultValues()
    {
        var sample = new ProcessSample();

        Assert.Equal(default, sample.TimestampUtc);
        Assert.Equal(0, sample.Pid);
        Assert.Equal(string.Empty, sample.ProcessName);
        Assert.Null(sample.ExecutablePath);
        Assert.Null(sample.CommandLine);
        Assert.Null(sample.ParentPid);
        Assert.Null(sample.CpuPercent);
        Assert.Null(sample.WorkingSetMb);
        Assert.Null(sample.PrivateMemoryMb);
        Assert.Null(sample.ThreadCount);
        Assert.Null(sample.HandleCount);
        Assert.Null(sample.DiskReadBytesPerSecond);
        Assert.Null(sample.DiskWriteBytesPerSecond);
        Assert.Null(sample.NetworkReceiveBytesPerSecond);
        Assert.Null(sample.NetworkSendBytesPerSecond);
        Assert.False(sample.IsForegroundProcess);
    }

    [Fact]
    public void AllowsNullForInaccessibleFields()
    {
        var sample = new ProcessSample
        {
            TimestampUtc = DateTime.UtcNow,
            Pid = 1234,
            ProcessName = "system.exe",
            // Access-denied fields left null
            ExecutablePath = null,
            CommandLine = null,
            CpuPercent = null,
            WorkingSetMb = 50.0
        };

        Assert.Null(sample.ExecutablePath);
        Assert.Null(sample.CommandLine);
        Assert.Null(sample.CpuPercent);
        Assert.Equal(50.0, sample.WorkingSetMb);
    }

    [Fact]
    public void ForegroundProcess_FlagWorks()
    {
        var fg = new ProcessSample { IsForegroundProcess = true };
        var bg = new ProcessSample { IsForegroundProcess = false };

        Assert.True(fg.IsForegroundProcess);
        Assert.False(bg.IsForegroundProcess);
    }
}
