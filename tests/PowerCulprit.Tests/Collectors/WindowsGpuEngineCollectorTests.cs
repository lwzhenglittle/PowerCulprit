using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class WindowsGpuEngineCollectorTests
{
    [Fact]
    public void ParsePid_StandardInstanceName_ExtractsPid()
    {
        Assert.Equal(11872, WindowsGpuEngineCollector.ParsePid(
            "pid_11872_luid_0x00000000_0x00000000_phys_0_eng_0_engtype_3d"));
    }

    [Fact]
    public void ParsePid_InstanceNameWithoutEngineType_StillExtractsPid()
    {
        Assert.Equal(4, WindowsGpuEngineCollector.ParsePid("pid_4_luid_0x00000000_0x00000000"));
    }

    [Fact]
    public void ParsePid_NoPidSegment_ReturnsNull()
    {
        Assert.Null(WindowsGpuEngineCollector.ParsePid("luid_0x00000000_0x00000000_phys_0_eng_0"));
    }

    [Fact]
    public void ParsePid_PidOverflowingInt_ReturnsNull()
    {
        // A malformed instance name must not surface as OverflowException —
        // int.Parse throwing here previously disabled the collector permanently.
        Assert.Null(WindowsGpuEngineCollector.ParsePid("pid_99999999999999999999_luid_0x0"));
    }

    [Fact]
    public void ParsePid_EmptyInstanceName_ReturnsNull()
    {
        Assert.Null(WindowsGpuEngineCollector.ParsePid(string.Empty));
    }
}
