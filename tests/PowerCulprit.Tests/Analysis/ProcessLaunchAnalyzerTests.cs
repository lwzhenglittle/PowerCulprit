using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class ProcessLaunchAnalyzerTests
{
    [Fact]
    public void Analyze_DetectsRestartStormDescendantsAndPowerDelta()
    {
        var from = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var parent = new ProcessInstance
        {
            Id = 1, Pid = 100, StartTimeUtc = from, StartObserved = true,
            ProcessName = "launcher.exe", CaptureReliable = true
        };
        var launchTimes = new[] { from.AddSeconds(20), from.AddSeconds(50), from.AddSeconds(80) };
        var instances = new List<ProcessInstance> { parent };
        for (var index = 0; index < launchTimes.Length; index++)
        {
            var id = index + 2;
            instances.Add(new ProcessInstance
            {
                Id = id, Pid = 200 + index, StartTimeUtc = launchTimes[index],
                StopTimeUtc = launchTimes[index].AddSeconds(4), StartObserved = true,
                StopObserved = true, ProcessName = "worker.exe", ParentPid = parent.Pid,
                ParentInstanceId = parent.Id, ParentProcessName = parent.ProcessName,
                CaptureReliable = true
            });
        }
        instances.Add(new ProcessInstance
        {
            Id = 10, Pid = 300, StartTimeUtc = launchTimes[0].AddSeconds(1),
            StopTimeUtc = launchTimes[0].AddSeconds(2), StartObserved = true,
            StopObserved = true, ProcessName = "helper.exe", ParentPid = 200,
            ParentInstanceId = 2, ParentProcessName = "worker.exe", CaptureReliable = true
        });

        var powerSamples = launchTimes.SelectMany(start => new[]
        {
            Power(start.AddSeconds(-8), 10), Power(start.AddSeconds(-4), 10),
            Power(start.AddSeconds(4), 14), Power(start.AddSeconds(8), 14)
        }).OrderBy(sample => sample.TimestampUtc).ToList();

        var results = ProcessLaunchAnalyzer.Analyze(from, from.AddMinutes(2), instances, powerSamples);
        var insight = Assert.Single(results,
            result => result.LauncherProcessName == "launcher.exe" && result.LaunchedProcessName == "worker.exe");

        Assert.Equal(3, insight.LaunchCount);
        Assert.Equal(3, insight.ShortLivedCount);
        Assert.Equal(1, insight.DescendantLaunchCount);
        Assert.Equal(4, insight.MedianLifetimeSeconds);
        Assert.Equal(4, insight.EstimatedPowerDeltaWatts);
        Assert.True(insight.IsRestartStorm);
        Assert.Equal(AttributionConfidence.High, insight.Confidence);
    }

    [Fact]
    public void Analyze_LostEventsAndUnresolvedParent_LowersConfidence()
    {
        var from = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var instances = new[]
        {
            new ProcessInstance
            {
                Id = 1, Pid = 500, StartTimeUtc = from.AddSeconds(5),
                StartObserved = true, ProcessName = "worker.exe", ParentPid = 42,
                CaptureReliable = false
            }
        };

        var insight = Assert.Single(ProcessLaunchAnalyzer.Analyze(
            from, from.AddMinutes(1), instances, Array.Empty<SystemPowerSample>()));

        Assert.Equal("PID 42", insight.LauncherProcessName);
        Assert.Equal(AttributionConfidence.Low, insight.Confidence);
        Assert.Contains("lost events", insight.Evidence);
        Assert.Contains("0/1", insight.Evidence);
    }

    private static SystemPowerSample Power(DateTime timestampUtc, double watts) => new()
    {
        TimestampUtc = timestampUtc,
        IsAcOnline = false,
        ChargeRateMilliwatts = -watts * 1000
    };
}
