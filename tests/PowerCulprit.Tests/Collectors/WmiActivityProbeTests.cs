using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class WmiActivityProbeTests
{
    [Fact]
    public void ProbeStatus_DoesNotThrow_AndReturnsKnownSourceName()
    {
        // ProbeStatus is the cold probe --diagnose uses. It must not throw
        // regardless of whether the WMI-Activity log exists / is enabled, and
        // it must report the canonical source name + a known status literal.
        var status = WmiActivityCollector.ProbeStatus();

        Assert.Equal(WmiActivityCollector.SourceName, status.SourceName);
        Assert.True(
            status.Status == SourceStatusStrings.Available ||
            status.Status == SourceStatusStrings.Unavailable ||
            status.Status == SourceStatusStrings.Disabled ||
            status.Status == SourceStatusStrings.Partial ||
            status.Status == SourceStatusStrings.RequiresAdmin,
            $"Unexpected status literal: {status.Status}");
    }
}
