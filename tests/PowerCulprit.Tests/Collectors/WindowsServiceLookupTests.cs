using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class WindowsServiceLookupTests
{
    // Smoke test — talks to the real Service Control Manager. Test project's TFM is
    // net10.0-windows10.0.26100.0 so we're always on a box with an SCM. A standard
    // Windows install has hundreds of services with a few dozen running, so we just
    // assert that at least one running service was discovered and every entry has a
    // valid PID and non-empty name. If this ever returns empty it almost certainly
    // means OpenSCManager failed (perhaps in a locked-down CI container).
    [Fact]
    public void GetServicesByPid_ReturnsAtLeastOneRunningService()
    {
        var lookup = new WindowsServiceLookup();
        var map = lookup.GetServicesByPid();

        Assert.NotEmpty(map);
        foreach (var (pid, services) in map)
        {
            Assert.True(pid > 0, $"PID for service group should be > 0 but was {pid}");
            Assert.NotEmpty(services);
            foreach (var name in services)
                Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }
}
