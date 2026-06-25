using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Models;

public class CulpritReportItemTests
{
    [Fact]
    public void Constructor_Defaults()
    {
        var item = new CulpritReportItem();

        Assert.Equal(string.Empty, item.ProcessName);
        Assert.Null(item.Pid);
        Assert.Equal(0.0, item.Score);
        Assert.Equal(0, item.Rank);
        Assert.Null(item.AvgCpuPercent);
        Assert.Null(item.MaxCpuPercent);
        Assert.Null(item.AvgGpuPercent);
        Assert.Null(item.MaxGpuPercent);
        Assert.Null(item.PowerCorrelation);
        Assert.Null(item.CpuPowerCorrelation);
        Assert.Null(item.GpuActivityCorrelation);
        Assert.Equal(string.Empty, item.Reason);
    }

    [Fact]
    public void Reason_ProvidesHumanExplanation()
    {
        var item = new CulpritReportItem
        {
            ProcessName = "chrome.exe",
            Pid = 5678,
            Score = 85.3,
            Rank = 1,
            AvgCpuPercent = 12.3,
            PowerCorrelation = 0.71,
            Reason = "chrome.exe: avg CPU 12.3%, power correlation 0.71, ranked #1"
        };

        Assert.Contains("chrome.exe", item.Reason);
        Assert.Contains("0.71", item.Reason);
    }

    [Fact]
    public void MissingData_LeavesFieldsNull()
    {
        var item = new CulpritReportItem
        {
            ProcessName = "unknown.exe",
            Score = 5.0,
            Rank = 10,
            Reason = "Insufficient sensor data"
        };

        Assert.Null(item.AvgCpuPercent);
        Assert.Null(item.PowerCorrelation);
    }
}
