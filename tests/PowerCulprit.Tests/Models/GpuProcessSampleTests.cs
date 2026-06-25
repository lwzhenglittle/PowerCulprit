using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Models;

public class GpuProcessSampleTests
{
    [Fact]
    public void Constructor_SetsDefaultsAndAllowsNullablePid()
    {
        var sample = new GpuProcessSample
        {
            TimestampUtc = DateTime.UtcNow,
            Pid = null, // PID resolution may fail
            ProcessName = null,
            EngineName = "eng_3d",
            EngineType = GpuEngineType.ThreeD,
            UtilizationPercent = 45.2
        };

        Assert.Null(sample.Pid);
        Assert.Null(sample.ProcessName);
        Assert.Equal("eng_3d", sample.EngineName);
        Assert.Equal(GpuEngineType.ThreeD, sample.EngineType);
        Assert.Equal(45.2, sample.UtilizationPercent);
    }

    [Fact]
    public void AllEngineTypes_AreDistinct()
    {
        var types = Enum.GetValues<GpuEngineType>();
        Assert.Equal(6, types.Length);
        Assert.Contains(GpuEngineType.ThreeD, types);
        Assert.Contains(GpuEngineType.Compute, types);
        Assert.Contains(GpuEngineType.VideoDecode, types);
        Assert.Contains(GpuEngineType.VideoEncode, types);
        Assert.Contains(GpuEngineType.Copy, types);
        Assert.Contains(GpuEngineType.Other, types);
    }
}
